using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using DafnyCore;
using Microsoft.Boogie;

namespace Microsoft.Dafny;

public class DafnyConsolePrinter : ConsolePrinter {
  public new DafnyOptions Options {
    get => options;
    set {
      base.Options = value;
      options = value;
    }
  }

  private readonly ConcurrentDictionary<string, List<string>> fsCache = new();
  private DafnyOptions options;
  public ConcurrentBag<(Implementation, VerificationResult)> VerificationResults { get; } = new();

  public override void AdvisoryWriteLine(TextWriter output, string format, params object[] args) {
    if (output == Console.Out) {
      int foregroundColor = (int)Console.ForegroundColor;
      Console.ForegroundColor = ConsoleColor.Yellow;
      output.WriteLine(format, args);
      Console.ForegroundColor = (ConsoleColor)foregroundColor;
    } else {
      output.WriteLine(format, args);
    }
  }

  private string GetFileLine(string filename, int lineIndex) {
    List<string> lines = fsCache.GetOrAdd(filename, key => {
      try {
        // Note: This is not guaranteed to be the same file that Dafny parsed. To ensure that, Dafny should keep
        // an in-memory version of each file it parses.
        lines = File.ReadLines(filename).ToList();
      } catch (Exception) {
        lines = new List<string>();
      }
      return lines;
    });
    if (0 <= lineIndex && lineIndex < lines.Count) {
      return lines[lineIndex];
    }
    return "<nonexistent line>";
  }

  public void WriteSourceCodeSnippet(Boogie.IToken tok, TextWriter tw) {
    string line = GetFileLine(((IToken)tok).Filepath, tok.line - 1);
    string lineNumber = tok.line.ToString();
    string lineNumberSpaces = new string(' ', lineNumber.Length);
    string columnSpaces = new string(' ', tok.col - 1);
    var lineStartPos = tok.pos - tok.col + 1;
    var lineEndPos = lineStartPos + line.Length;
    var tokEndPos = tok.pos + tok.val.Length;
    var underlineLength = Math.Max(1, Math.Min(tokEndPos - tok.pos, lineEndPos - tok.pos));
    string underline = new string('^', underlineLength);
    tw.WriteLine($"{lineNumberSpaces} |");
    tw.WriteLine($"{lineNumber} | {line}");
    tw.WriteLine($"{lineNumberSpaces} | {columnSpaces}{underline}");
    tw.WriteLine("");
  }

  public static readonly Option<bool> ShowSnippets = new("--show-snippets",
    "Show a source code snippet for each Dafny message.");

  static DafnyConsolePrinter() {
    DooFile.RegisterNoChecksNeeded(ShowSnippets);
  }

  public DafnyConsolePrinter(DafnyOptions options) {
    Options = options;
  }

  public override void ReportBplError(Boogie.IToken tok, string message, bool error, TextWriter tw, string category = null) {
    // Dafny has 0-indexed columns, but Boogie counts from 1
    var realigned_tok = new Boogie.Token(tok.line, tok.col - 1);
    realigned_tok.kind = tok.kind;
    realigned_tok.pos = tok.pos;
    realigned_tok.val = tok.val;
    realigned_tok.filename = tok.filename;

    if (Options.Verbosity == CoreOptions.VerbosityLevel.Silent) {
      return;
    }

    if (category != null) {
      message = $"{category}: {message}";
    }

    message = $"{tok.TokenToString(Options)}: {message}";

    if (error) {
      ErrorWriteLine(tw, message);
    } else {
      tw.WriteLine(message);
    }

    if (Options.Get(ShowSnippets)) {
      WriteSourceCodeSnippet(tok, tw);
    }

    if (tok is Dafny.NestedToken) {
      var nt = (Dafny.NestedToken)tok;
      ReportBplError(nt.Inner, "Related location", false, tw);
    }
  }

  public override void ReportEndVerifyImplementation(Implementation implementation, Boogie.VerificationResult result) {
    VerificationResults.Add((implementation, result));
  }
}
