using System;
using System.Collections.Generic;

namespace Microsoft.Dafny;

public class ModuleSignature {
  public VisibilityScope VisibilityScope = null;
  public readonly Dictionary<string, ModuleDecl> ShadowedImportedModules = new();
  public readonly Dictionary<string, TopLevelDecl> TopLevels = new Dictionary<string, TopLevelDecl>();
  public readonly Dictionary<string, ModuleExportDecl> ExportSets = new Dictionary<string, ModuleExportDecl>();
  public readonly Dictionary<string, Tuple<DatatypeCtor, bool>> Ctors = new Dictionary<string, Tuple<DatatypeCtor, bool>>();
  public readonly Dictionary<string, MemberDecl> StaticMembers = new Dictionary<string, MemberDecl>();
  public ModuleDefinition ModuleDef = null; // Note: this is null if this signature does not correspond to a specific definition (i.e.
  // it is abstract). Otherwise, it points to that definition.
  public ModuleSignature Refines = null;
  public bool IsAbstract = false;
  public ModuleSignature() { }
  public int? ResolvedHash { get; set; }

  // Qualified accesses follow module imports
  public bool FindImport(string name, out ModuleDecl decl) {
    if (TopLevels.TryGetValue(name, out var top) && top is ModuleDecl) {
      decl = (ModuleDecl)top;
      return true;
    } else {
      decl = null;
      return false;
    }
  }
}