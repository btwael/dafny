using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static Microsoft.Dafny.ParseErrors;

namespace Microsoft.Dafny;

public record DfyParseResult(
  BatchErrorReporter ErrorReporter,
  FileModuleDefinition Module,
  IReadOnlyList<Action<BuiltIns>> ModifyBuiltins
  );

public class ProgramParser {
  protected readonly ILogger<ProgramParser> logger;

  public ProgramParser() : this(NullLogger<ProgramParser>.Instance) {
  }

  public ProgramParser(ILogger<ProgramParser> logger) {
    this.logger = logger;
  }

  public Program ParseFiles(string programName, IReadOnlyList<DafnyFile> files, ErrorReporter errorReporter,
    CancellationToken cancellationToken) {
    var options = errorReporter.Options;
    var builtIns = new BuiltIns(options);
    var defaultModule = new DefaultModuleDefinition(files.Where(f => !f.IsPreverified).Select(f => f.Uri).ToList(), options.VerifyAllModules);

    var verifiedRoots = files.Where(df => df.IsPreverified).Select(df => df.Uri).ToHashSet();
    var compiledRoots = files.Where(df => df.IsPrecompiled).Select(df => df.Uri).ToHashSet();
    var program = new Program(
      programName,
      new LiteralModuleDecl(defaultModule, null),
      builtIns,
      errorReporter, verifiedRoots, compiledRoots
    );

    foreach (var dafnyFile in files) {
      if (options.Trace) {
        options.OutputWriter.WriteLine("Parsing " + dafnyFile.FilePath);
      }

      if (options.XmlSink is { IsOpen: true } && dafnyFile.Uri.IsFile) {
        options.XmlSink.WriteFileFragment(dafnyFile.Uri.LocalPath);
      }

      try {
        var parseResult = ParseFile(
          options,
          dafnyFile.Content,
          dafnyFile.Uri
        );
        if (parseResult.ErrorReporter.ErrorCount != 0) {
          logger.LogDebug($"encountered {parseResult.ErrorReporter.ErrorCount} errors while parsing {dafnyFile.Uri}");
        }

        AddParseResultToProgram(parseResult, program);
        if (defaultModule.RangeToken.StartToken.Uri == null) {
          defaultModule.RangeToken = parseResult.Module.RangeToken;
        }

      } catch (Exception e) {
        logger.LogDebug(e, $"encountered an exception while parsing {dafnyFile.Uri}");
        var internalErrorDummyToken = new Token {
          Uri = dafnyFile.Uri,
          line = 1,
          col = 1,
          pos = 0,
          val = string.Empty
        };
        errorReporter.Error(MessageSource.Parser, internalErrorDummyToken, "[internal error] Parser exception: " + e.Message);
      }
    }

    if (!(options.DisallowIncludes || options.PrintIncludesMode == DafnyOptions.IncludesModes.Immediate)) {
      var includedModules = TryParseIncludes(files, defaultModule.Includes.ToList(),
        builtIns, errorReporter, cancellationToken);

      foreach (var module in includedModules) {
        AddParseResultToProgram(module, program);
      }
    }

    if (options.PrintIncludesMode == DafnyOptions.IncludesModes.Immediate) {
      var dependencyMap = new DependencyMap();
      dependencyMap.AddIncludes(defaultModule.Includes);
      dependencyMap.PrintMap(options);
    }

    if (errorReporter.ErrorCount == 0) {
      DafnyMain.MaybePrintProgram(program, options.DafnyPrintFile, false);
    }

    ShowWarningsForIncludeCycles(program);


    return program;
  }

  private void ShowWarningsForIncludeCycles(Program program) {
    var graph = new Graph<Uri>();
    foreach (var edgesForUri in program.Includes.GroupBy(i => i.IncluderFilename)) {
      foreach (var edge in edgesForUri) {
        graph.AddEdge(edge.IncluderFilename, edge.IncludedFilename);
      }
    }

    var sortedSccRoots = graph.TopologicallySortedComponents();
    var includeCycles = sortedSccRoots.Select(graph.GetSCC).Where(scc => scc.Count > 1);
    foreach (var cycle in includeCycles) {
      program.Reporter.Info(MessageSource.Parser, program.GetFirstTopLevelToken(),
        $"Program contains a cycle of includes, consisting of:\n{string.Join("\n", cycle.Select(c => c.LocalPath))}");
    }
  }

  public static void AddParseResultToProgram(DfyParseResult parseResult, Program program) {
    var defaultModule = program.DefaultModuleDef;
    var fileModule = parseResult.Module;

    foreach (var modify in parseResult.ModifyBuiltins) {
      modify(program.BuiltIns);
    }

    foreach (var diagnostic in parseResult.ErrorReporter.AllMessages) {
      program.Reporter.Message(diagnostic.Source, diagnostic.Level, diagnostic.ErrorId, diagnostic.Token,
        diagnostic.Message);
    }

    foreach (var declToMove in fileModule.TopLevelDecls.
               Where(d => d != null) // Can occur when there are parse errors. Error correction is at fault but we workaround it here
             ) {
      declToMove.EnclosingModuleDefinition = defaultModule;
      if (declToMove is LiteralModuleDecl literalModuleDecl) {
        literalModuleDecl.ModuleDef.EnclosingModule = defaultModule;
      }

      if (declToMove is ClassLikeDecl classDecl) {
        classDecl.NonNullTypeDecl.EnclosingModuleDefinition = defaultModule;
      }
      if (declToMove is DefaultClassDecl defaultClassDecl) {
        foreach (var member in defaultClassDecl.Members) {
          defaultModule.DefaultClass.Members.Add(member);
          member.EnclosingClass = defaultModule.DefaultClass;
        }
      } else {
        defaultModule.SourceDecls.Add(declToMove);
      }
    }

    foreach (var include in fileModule.Includes) {
      defaultModule.Includes.Add(include);
    }

    foreach (var prefixNamedModule in fileModule.PrefixNamedModules) {
      defaultModule.PrefixNamedModules.Add(prefixNamedModule);
    }

    defaultModule.DefaultClass.SetMembersBeforeResolution();
  }

  public IList<DfyParseResult> TryParseIncludes(
    IReadOnlyList<DafnyFile> files,
    IEnumerable<Include> roots,
    BuiltIns builtIns,
    ErrorReporter errorReporter,
    CancellationToken cancellationToken
  ) {
    var stack = new Stack<DafnyFile>();
    var result = new List<DfyParseResult>();
    var resolvedFiles = new HashSet<Uri>();
    foreach (var rootFile in files) {
      resolvedFiles.Add(rootFile.Uri);
    }

    foreach (var root in roots) {
      var dafnyFile = IncludeToDafnyFile(builtIns, errorReporter, root);
      if (dafnyFile != null) {
        stack.Push(dafnyFile);
      }
    }

    while (stack.Any()) {
      var top = stack.Pop();
      if (!resolvedFiles.Add(top.Uri)) {
        continue;
      }

      cancellationToken.ThrowIfCancellationRequested();
      try {
        var parseIncludeResult = ParseFile(
          errorReporter.Options,
          top.Content,
          top.Uri
        );
        result.Add(parseIncludeResult);

        foreach (var include in parseIncludeResult.Module.Includes) {
          var dafnyFile = IncludeToDafnyFile(builtIns, errorReporter, include);
          if (dafnyFile != null) {
            stack.Push(dafnyFile);
          }
        }
      } catch (Exception) {
        // ignored
      }
    }

    return result;
  }

  private static DafnyFile IncludeToDafnyFile(BuiltIns builtIns, ErrorReporter errorReporter, Include include) {
    try {
      return new DafnyFile(builtIns.Options, include.IncludedFilename);
    } catch (IllegalDafnyFile) {
      errorReporter.Error(MessageSource.Parser, include.tok, $"Include of file '{include.IncludedFilename}' failed.");
      return null;
    } catch (IOException) {
      errorReporter.Error(MessageSource.Parser, include.tok,
        $"Unable to open the include {include.IncludedFilename}.");
      return null;
    }
  }

  ///<summary>
  /// Parses top-level things (modules, classes, datatypes, class members) from "filename"
  /// and appends them in appropriate form to "module".
  /// Returns the number of parsing errors encountered.
  /// Note: first initialize the Scanner.
  ///</summary>
  protected virtual DfyParseResult ParseFile(DafnyOptions options, TextReader reader, Uri uri) /* throws System.IO.IOException */ {
    Contract.Requires(uri != null);
    var text = SourcePreprocessor.ProcessDirectives(reader, new List<string>());
    try {
      return ParseFile(options, text, uri);
    } catch (Exception e) {
      var internalErrorDummyToken = new Token {
        Uri = uri,
        line = 1,
        col = 1,
        pos = 0,
        val = string.Empty
      };
      var reporter = new BatchErrorReporter(options);
      reporter.Error(MessageSource.Parser, ErrorId.p_internal_exception, internalErrorDummyToken,
        "[internal error] Parser exception: " + e.Message + (!options.Verbose ? "" :
            "\n" + e.StackTrace));
      return new DfyParseResult(reporter, null, new Action<BuiltIns>[] { });
    }
  }

  ///<summary>
  /// Parses top-level things (modules, classes, datatypes, class members)
  /// and appends them in appropriate form to "module".
  /// Returns the number of parsing errors encountered.
  /// Note: first initialize the Scanner with the given Errors sink.
  ///</summary>
  private static DfyParseResult ParseFile(DafnyOptions options, string /*!*/ s, Uri /*!*/ uri) {
    var batchErrorReporter = new BatchErrorReporter(options);
    Parser parser = SetupParser(s, uri, batchErrorReporter);
    parser.Parse();

    if (parser.theModule.DefaultClass.Members.Count == 0 && parser.theModule.Includes.Count == 0 && !parser.theModule.SourceDecls.Any()
        && (parser.theModule.PrefixNamedModules == null || parser.theModule.PrefixNamedModules.Count == 0)) {
      batchErrorReporter.Warning(MessageSource.Parser, ErrorId.p_file_has_no_code, new Token(1, 1) { Uri = uri }, "File contains no code");
    }

    return new DfyParseResult(batchErrorReporter, parser.theModule, parser.BuiltinsModifiers);
  }

  private static Parser SetupParser(string /*!*/ s, Uri /*!*/ uri, ErrorReporter /*!*/ errorReporter) {
    Contract.Requires(s != null);
    Contract.Requires(uri != null);
    System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(ParseErrors).TypeHandle);
    System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(ResolutionErrors).TypeHandle);
    byte[] /*!*/ buffer = cce.NonNull(Encoding.Default.GetBytes(s));
    var ms = new MemoryStream(buffer, false);
    var firstToken = new Token {
      Uri = uri
    };

    var errors = new Errors(errorReporter);

    var scanner = new Scanner(ms, errors, uri, firstToken: firstToken);
    return new Parser(errorReporter.Options, scanner, errors);
  }

  public Program Parse(string source, Uri uri, ErrorReporter reporter) {
    var files = new[] { new DafnyFile(reporter.Options, uri, new StringReader(source)) };
    return ParseFiles(uri.ToString(), files, reporter, CancellationToken.None);
  }
}
