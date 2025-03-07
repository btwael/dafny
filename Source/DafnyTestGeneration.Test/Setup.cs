using System.IO;
using Microsoft.Dafny;

namespace DafnyTestGeneration.Test {

  public class Setup {


    public static DafnyOptions GetDafnyOptions(TextWriter writer, params string[] arguments) {
      var options = DafnyOptions.Create(writer, TextReader.Null, arguments ?? System.Array.Empty<string>());
      options.DefiniteAssignmentLevel = 3;
      options.WarnShadowing = true;
      options.VerifyAllModules = true;
      options.LoopUnrollCount = 5;
      options.TestGenOptions.SeqLengthLimit = 3;
      options.TestGenOptions.Mode = TestGenerationOptions.Modes.Block;
      options.TestGenOptions.WarnDeadCode = false;
      options.TimeLimit = 10;
      return options;
    }

  }
}
