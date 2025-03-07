using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using Microsoft.Dafny.Auditor;

namespace Microsoft.Dafny;

public abstract class Declaration : RangeNode, IAttributeBearingDeclaration, IDeclarationOrUsage {
  [ContractInvariantMethod]
  void ObjectInvariant() {
    Contract.Invariant(Name != null);
  }

  public IToken BodyStartTok = Token.NoToken;
  public IToken TokenWithTrailingDocString = Token.NoToken;
  public Name NameNode;

  public override IToken Tok => NameNode.StartToken;
  public IToken NameToken => NameNode.StartToken;

  public string Name => NameNode.Value;
  public bool IsRefining;

  private VisibilityScope opaqueScope = new();
  private VisibilityScope revealScope = new();

  private bool scopeIsInherited = false;

  public bool HasAxiomAttribute =>
    Attributes.Contains(Attributes, Attributes.AxiomAttributeName);

  public bool HasConcurrentAttribute =>
    Attributes.Contains(Attributes, Attributes.ConcurrentAttributeName);

  public bool HasExternAttribute =>
    Attributes.Contains(Attributes, Attributes.ExternAttributeName);

  public bool HasAutoGeneratedAttribute =>
    Attributes.Contains(Attributes, Attributes.AutoGeneratedAttributeName);

  public bool HasVerifyFalseAttribute =>
    Attributes.Find(Attributes, Attributes.VerifyAttributeName) is Attributes va &&
    va.Args.Count == 1 &&
    Expression.IsBoolLiteral(va.Args[0], out var verify) &&
    verify == false;

  public override IEnumerable<Assumption> Assumptions(Declaration decl) {
    if (HasAxiomAttribute && !HasAutoGeneratedAttribute) {
      yield return new Assumption(this, tok, AssumptionDescription.HasAxiomAttribute);
    }

    if (HasVerifyFalseAttribute && !HasAutoGeneratedAttribute) {
      yield return new Assumption(this, tok, AssumptionDescription.HasVerifyFalseAttribute);
    }
  }

  public virtual bool CanBeExported() {
    return true;
  }

  public virtual bool CanBeRevealed() {
    return false;
  }

  public bool ScopeIsInherited { get { return scopeIsInherited; } }

  public void AddVisibilityScope(VisibilityScope scope, bool isOpaque) {
    if (isOpaque) {
      opaqueScope.Augment(scope);
    } else {
      revealScope.Augment(scope);
    }
  }

  public void InheritVisibility(Declaration d, bool onlyRevealed = true) {
    Contract.Assert(opaqueScope.IsEmpty());
    Contract.Assert(revealScope.IsEmpty());
    scopeIsInherited = false;

    revealScope = d.revealScope;

    if (!onlyRevealed) {
      opaqueScope = d.opaqueScope;
    }
    scopeIsInherited = true;

  }

  public bool IsRevealedInScope(VisibilityScope scope) {
    return revealScope.VisibleInScope(scope);
  }

  public bool IsVisibleInScope(VisibilityScope scope) {
    return IsRevealedInScope(scope) || opaqueScope.VisibleInScope(scope);
  }

  protected string sanitizedName;
  public virtual string SanitizedName => sanitizedName ??= NonglobalVariable.SanitizeName(Name);

  protected string compileName;

  public virtual string GetCompileName(DafnyOptions options) {
    if (compileName == null) {
      IsExtern(options, out _, out compileName);
      compileName ??= SanitizedName;
    }

    return compileName;
  }

  public bool IsExtern(DafnyOptions options, out string/*?*/ qualification, out string/*?*/ name) {
    // ensures result==false ==> qualification == null && name == null
    Contract.Ensures(Contract.Result<bool>() || (Contract.ValueAtReturn(out qualification) == null && Contract.ValueAtReturn(out name) == null));
    // ensures result==true ==> qualification != null ==> name != null
    Contract.Ensures(!Contract.Result<bool>() || Contract.ValueAtReturn(out qualification) == null || Contract.ValueAtReturn(out name) != null);

    qualification = null;
    name = null;
    if (!options.DisallowExterns) {
      var externArgs = Attributes.FindExpressions(this.Attributes, "extern");
      if (externArgs != null) {
        if (externArgs.Count == 0) {
          return true;
        } else if (externArgs.Count == 1 && externArgs[0] is StringLiteralExpr) {
          name = externArgs[0].AsStringLiteral();
          return true;
        } else if (externArgs.Count == 2 && externArgs[0] is StringLiteralExpr && externArgs[1] is StringLiteralExpr) {
          qualification = externArgs[0].AsStringLiteral();
          name = externArgs[1].AsStringLiteral();
          return true;
        }
      }
    }
    return false;
  }
  public Attributes Attributes;  // readonly, except during class merging in the refinement transformations and when changed by Compiler.MarkCapitalizationConflict
  Attributes IAttributeBearingDeclaration.Attributes => Attributes;

  protected Declaration(RangeToken rangeToken, Name name, Attributes attributes, bool isRefining) : base(rangeToken) {
    Contract.Requires(rangeToken != null);
    Contract.Requires(name != null);
    this.NameNode = name;
    this.Attributes = attributes;
    this.IsRefining = isRefining;
  }

  [Pure]
  public override string ToString() {
    Contract.Ensures(Contract.Result<string>() != null);
    return Name;
  }

  internal FreshIdGenerator IdGenerator = new();
  public override IEnumerable<Node> Children => (Attributes != null ? new List<Node> { Attributes } : Enumerable.Empty<Node>());
  public override IEnumerable<Node> PreResolveChildren => Children;
}