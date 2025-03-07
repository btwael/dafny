using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using Microsoft.Boogie;

namespace Microsoft.Dafny;

public class BuiltIns {
  public DafnyOptions Options { get; }
  public readonly ModuleDefinition SystemModule = new(RangeToken.NoToken, new Name("_System"), new List<IToken>(),
    false, false, null, null, null, true, false);
  internal readonly Dictionary<int, ClassDecl> arrayTypeDecls = new Dictionary<int, ClassDecl>();
  public readonly Dictionary<int, ArrowTypeDecl> ArrowTypeDecls = new Dictionary<int, ArrowTypeDecl>();
  public readonly Dictionary<int, SubsetTypeDecl> PartialArrowTypeDecls = new Dictionary<int, SubsetTypeDecl>();  // same keys as arrowTypeDecl
  public readonly Dictionary<int, SubsetTypeDecl> TotalArrowTypeDecls = new Dictionary<int, SubsetTypeDecl>();  // same keys as arrowTypeDecl
  readonly Dictionary<List<bool>, TupleTypeDecl> tupleTypeDecls = new Dictionary<List<bool>, TupleTypeDecl>(new Dafny.IEnumerableComparer<bool>());
  public int MaxNonGhostTupleSizeUsed { get; private set; }
  public IToken MaxNonGhostTupleSizeToken { get; private set; }
  public readonly ISet<int> Bitwidths = new HashSet<int>();
  [FilledInDuringResolution] public SpecialField ORDINAL_Offset;  // used by the translator

  public readonly SubsetTypeDecl NatDecl;
  public UserDefinedType Nat() { return new UserDefinedType(Token.NoToken, "nat", NatDecl, new List<Type>()); }
  public readonly TraitDecl ObjectDecl;
  public UserDefinedType ObjectQ() {
    Contract.Assume(ObjectDecl != null);
    return new UserDefinedType(Token.NoToken, "object?", null) { ResolvedClass = ObjectDecl };
  }

  public BuiltIns(DafnyOptions options) {
    this.Options = options;
    SystemModule.Height = -1;  // the system module doesn't get a height assigned later, so we set it here to something below everything else
    // create type synonym 'string'
    var str = new TypeSynonymDecl(RangeToken.NoToken, new Name("string"),
      new TypeParameter.TypeParameterCharacteristics(TypeParameter.EqualitySupportValue.InferredRequired, Type.AutoInitInfo.CompilableValue, false),
      new List<TypeParameter>(), SystemModule, new SeqType(new CharType()), null);
    SystemModule.SourceDecls.Add(str);
    // create subset type 'nat'
    var bvNat = new BoundVar(Token.NoToken, "x", Type.Int);
    var natConstraint = Expression.CreateAtMost(Expression.CreateIntLiteral(Token.NoToken, 0), Expression.CreateIdentExpr(bvNat));
    var ax = AxiomAttribute();
    NatDecl = new SubsetTypeDecl(RangeToken.NoToken, new Name("nat"),
      new TypeParameter.TypeParameterCharacteristics(TypeParameter.EqualitySupportValue.InferredRequired, Type.AutoInitInfo.CompilableValue, false),
      new List<TypeParameter>(), SystemModule, bvNat, natConstraint, SubsetTypeDecl.WKind.CompiledZero, null, ax);
    SystemModule.SourceDecls.Add(NatDecl);
    // create trait 'object'
    ObjectDecl = new TraitDecl(RangeToken.NoToken, new Name("object"), SystemModule, new List<TypeParameter>(), new List<MemberDecl>(), DontCompile(), false, null);
    SystemModule.SourceDecls.Add(ObjectDecl);
    // add one-dimensional arrays, since they may arise during type checking
    // Arrays of other dimensions may be added during parsing as the parser detects the need for these
    UserDefinedType tmp = ArrayType(1, Type.Int, true);
    // Arrow types of other dimensions may be added during parsing as the parser detects the need for these.  For the 0-arity
    // arrow type, the resolver adds a Valid() predicate for iterators, whose corresponding arrow type is conveniently created here.
    CreateArrowTypeDecl(0);
  }

  private Attributes DontCompile() {
    var flse = Expression.CreateBoolLiteral(Token.NoToken, false);
    return new Attributes("compile", new List<Expression>() { flse }, null);
  }

  public static Attributes AxiomAttribute() {
    return new Attributes("axiom", new List<Expression>(), null);
  }

  /// <summary>
  /// Ensures this instance contains the requested type
  /// </summary>
  public UserDefinedType ArrayType(int dims, Type arg, bool allowCreationOfNewClass) {
    Contract.Requires(1 <= dims);
    Contract.Requires(arg != null);
    var (result, mod) = ArrayType(Token.NoToken, dims, new List<Type> { arg }, allowCreationOfNewClass);
    mod(this);
    return result;
  }

  public static (UserDefinedType type, Action<BuiltIns> ModifyBuiltins) ArrayType(IToken tok, int dims, List<Type> optTypeArgs, bool allowCreationOfNewClass, bool useClassNameType = false) {
    Contract.Requires(tok != null);
    Contract.Requires(1 <= dims);
    Contract.Requires(optTypeArgs == null || optTypeArgs.Count > 0);  // ideally, it is 1, but more will generate an error later, and null means it will be filled in automatically
    Contract.Ensures(Contract.Result<UserDefinedType>() != null);

    var arrayName = ArrayClassName(dims);
    if (useClassNameType) {
      arrayName += "?";
    }

    void ModifyBuiltins(BuiltIns builtIns) {
      if (!allowCreationOfNewClass || builtIns.arrayTypeDecls.ContainsKey(dims)) {
        return;
      }

      ArrayClassDecl arrayClass = new ArrayClassDecl(dims, builtIns.SystemModule, builtIns.DontCompile());
      for (int d = 0; d < dims; d++) {
        string name = dims == 1 ? "Length" : "Length" + d;
        Field len = new SpecialField(RangeToken.NoToken, name, SpecialField.ID.ArrayLength, dims == 1 ? null : (object)d, false, false, false, Type.Int, null);
        len.EnclosingClass = arrayClass; // resolve here
        arrayClass.Members.Add(len);
      }

      builtIns.arrayTypeDecls.Add(dims, arrayClass);
      builtIns.SystemModule.SourceDecls.Add(arrayClass);
      builtIns.CreateArrowTypeDecl(dims); // also create an arrow type with this arity, since it may be used in an initializing expression for the array
    }

    var udt = new UserDefinedType(tok, arrayName, optTypeArgs);
    return (udt, ModifyBuiltins);
  }

  public static string ArrayClassName(int dims) {
    Contract.Requires(1 <= dims);
    if (dims == 1) {
      return "array";
    } else {
      return "array" + dims;
    }
  }

  /// <summary>
  /// Idempotently add an arrow type with arity 'arity' to the system module, and along
  /// with this arrow type, the two built-in subset types based on the arrow type.
  /// </summary>
  public void CreateArrowTypeDecl(int arity) {
    Contract.Requires(0 <= arity);
    if (ArrowTypeDecls.ContainsKey(arity)) {
      // The work has already been done.
      return;
    }

    IToken tok = Token.NoToken;
    var tps = Util.Map(Enumerable.Range(0, arity + 1),
      x => x < arity
        ? new TypeParameter(RangeToken.NoToken, new Name("T" + x), TypeParameter.TPVarianceSyntax.Contravariance)
        : new TypeParameter(RangeToken.NoToken, new Name("R"), TypeParameter.TPVarianceSyntax.Covariant_Strict));
    var tys = tps.ConvertAll(tp => (Type)(new UserDefinedType(tp)));

    Function createMember(string name, Type resultType, Function readsFunction = null) {
      var args = Util.Map(Enumerable.Range(0, arity), i => new Formal(tok, "x" + i, tys[i], true, false, null));
      var argExprs = args.ConvertAll(a =>
        (Expression)new IdentifierExpr(tok, a.Name) { Var = a, Type = a.Type });
      var readsIS = new FunctionCallExpr(tok, "reads", new ImplicitThisExpr(tok), tok, tok, argExprs) {
        Type = new SetType(true, ObjectQ()),
      };
      var readsFrame = new List<FrameExpression> { new FrameExpression(tok, readsIS, null) };
      var function = new Function(RangeToken.NoToken, new Name(name), false, true, false,
        new List<TypeParameter>(), args, null, resultType,
        new List<AttributedExpression>(), readsFrame, new List<AttributedExpression>(),
        new Specification<Expression>(new List<Expression>(), null),
        null, null, null, null, null);
      readsIS.Function = readsFunction ?? function; // just so we can really claim the member declarations are resolved
      readsIS.TypeApplication_AtEnclosingClass = tys; // ditto
      readsIS.TypeApplication_JustFunction = new List<Type>(); // ditto
      return function;
    }

    var reads = createMember("reads", new SetType(true, ObjectQ()), null);
    var req = createMember("requires", Type.Bool, reads);

    var arrowDecl = new ArrowTypeDecl(tps, req, reads, SystemModule, DontCompile());
    ArrowTypeDecls.Add(arity, arrowDecl);
    SystemModule.SourceDecls.Add(arrowDecl);

    // declaration of read-effect-free arrow-type, aka heap-independent arrow-type, aka partial-function arrow-type
    tps = Util.Map(Enumerable.Range(0, arity + 1),
      x => x < arity
        ? new TypeParameter(RangeToken.NoToken, new Name("T" + x), TypeParameter.TPVarianceSyntax.Contravariance)
        : new TypeParameter(RangeToken.NoToken, new Name("R"), TypeParameter.TPVarianceSyntax.Covariant_Strict));
    tys = tps.ConvertAll(tp => (Type)(new UserDefinedType(tp)));
    var id = new BoundVar(tok, "f", new ArrowType(tok, arrowDecl, tys));
    var partialArrow = new SubsetTypeDecl(RangeToken.NoToken, new Name(ArrowType.PartialArrowTypeName(arity)),
      new TypeParameter.TypeParameterCharacteristics(false), tps, SystemModule,
      id, ArrowSubtypeConstraint(tok, tok.ToRange(), id, reads, tps, false), SubsetTypeDecl.WKind.Special, null, DontCompile());
    PartialArrowTypeDecls.Add(arity, partialArrow);
    SystemModule.SourceDecls.Add(partialArrow);

    // declaration of total arrow-type

    tps = Util.Map(Enumerable.Range(0, arity + 1),
      x => x < arity
        ? new TypeParameter(RangeToken.NoToken, new Name("T" + x), TypeParameter.TPVarianceSyntax.Contravariance)
        : new TypeParameter(RangeToken.NoToken, new Name("R"), TypeParameter.TPVarianceSyntax.Covariant_Strict));
    tys = tps.ConvertAll(tp => (Type)(new UserDefinedType(tp)));
    id = new BoundVar(tok, "f", new UserDefinedType(tok, partialArrow.Name, partialArrow, tys));
    var totalArrow = new SubsetTypeDecl(RangeToken.NoToken, new Name(ArrowType.TotalArrowTypeName(arity)),
      new TypeParameter.TypeParameterCharacteristics(false), tps, SystemModule,
      id, ArrowSubtypeConstraint(tok, tok.ToRange(), id, req, tps, true), SubsetTypeDecl.WKind.Special, null, DontCompile());
    TotalArrowTypeDecls.Add(arity, totalArrow);
    SystemModule.SourceDecls.Add(totalArrow);
  }

  /// <summary>
  /// Returns an expression that is the constraint of:
  /// the built-in partial-arrow type (if "!total", in which case "member" is expected to denote the "reads" member), or
  /// the built-in total-arrow type (if "total", in which case "member" is expected to denote the "requires" member).
  /// The given "id" is expected to be already resolved.
  /// </summary>
  private Expression ArrowSubtypeConstraint(IToken tok, RangeToken rangeToken, BoundVar id, Function member, List<TypeParameter> tps, bool total) {
    Contract.Requires(tok != null);
    Contract.Requires(id != null);
    Contract.Requires(member != null);
    Contract.Requires(tps != null && 1 <= tps.Count);
    var f = new IdentifierExpr(tok, id);
    // forall x0,x1,x2 :: f.reads(x0,x1,x2) == {}
    // OR
    // forall x0,x1,x2 :: f.requires(x0,x1,x2)
    var bvs = new List<BoundVar>();
    var args = new List<Expression>();
    var bounds = new List<ComprehensionExpr.BoundedPool>();
    for (int i = 0; i < tps.Count - 1; i++) {
      var bv = new BoundVar(tok, "x" + i, new UserDefinedType(tps[i]));
      bvs.Add(bv);
      args.Add(new IdentifierExpr(tok, bv));
      bounds.Add(new ComprehensionExpr.SpecialAllocIndependenceAllocatedBoundedPool());
    }
    var fn = new MemberSelectExpr(tok, f, member.Name) {
      Member = member,
      TypeApplication_AtEnclosingClass = f.Type.TypeArgs,
      TypeApplication_JustMember = new List<Type>(),
      Type = GetTypeOfFunction(member, tps.ConvertAll(tp => (Type)new UserDefinedType(tp)), new List<Type>())
    };
    Expression body = new ApplyExpr(tok, fn, args, tok);
    body.Type = member.ResultType;  // resolve here
    if (!total) {
      Expression emptySet = new SetDisplayExpr(tok, true, new List<Expression>());
      emptySet.Type = member.ResultType;  // resolve here
      body = Expression.CreateEq(body, emptySet, member.ResultType);
    }
    if (tps.Count > 1) {
      body = new ForallExpr(tok, rangeToken, bvs, null, body, null) { Type = Type.Bool, Bounds = bounds };
    }
    return body;
  }

  Type GetTypeOfFunction(Function f, List<Type> typeArgumentsClass, List<Type> typeArgumentsMember) {
    Contract.Requires(f != null);
    Contract.Requires(f.EnclosingClass != null);
    Contract.Requires(typeArgumentsClass != null);
    Contract.Requires(typeArgumentsMember != null);
    Contract.Requires(typeArgumentsClass.Count == f.EnclosingClass.TypeArgs.Count);
    Contract.Requires(typeArgumentsMember.Count == f.TypeArgs.Count);

    var atd = ArrowTypeDecls[f.Formals.Count];

    var formals = Util.Concat(f.EnclosingClass.TypeArgs, f.TypeArgs);
    var actuals = Util.Concat(typeArgumentsClass, typeArgumentsMember);
    var typeMap = TypeParameter.SubstitutionMap(formals, actuals);
    return new ArrowType(f.tok, atd, f.Formals.ConvertAll(arg => arg.Type.Subst(typeMap)), f.ResultType.Subst(typeMap));
  }

  public TupleTypeDecl TupleType(IToken tok, int dims, bool allowCreationOfNewType, List<bool> argumentGhostness = null) {
    Contract.Requires(tok != null);
    Contract.Requires(0 <= dims);
    Contract.Requires(argumentGhostness == null || argumentGhostness.Count == dims);
    Contract.Ensures(Contract.Result<TupleTypeDecl>() != null);

    argumentGhostness ??= new bool[dims].Select(_ => false).ToList();
    if (!tupleTypeDecls.TryGetValue(argumentGhostness, out var tt)) {
      Contract.Assume(allowCreationOfNewType);  // the parser should ensure that all needed tuple types exist by the time of resolution

      // A tuple type with ghost components is represented as the shorter tuple type with the ghost components erased, except
      // possibly when that shorter tuple type is a 1-tuple. Ordinarily, such a 1-tuple is optimized into its component, so
      // there's no reason to create it here; but if either the compiler doesn't support datatype wrapper erasure or if
      // the user has disabled this optimization, then we still create the 1-tuple here.
      var nonGhostDims = argumentGhostness.Count(x => !x);
      TupleTypeDecl nonGhostTupleTypeDecl = null;
      if (nonGhostDims != dims &&
          (nonGhostDims != 1 || !Options.Backend.SupportsDatatypeWrapperErasure || !Options.Get(CommonOptionBag.OptimizeErasableDatatypeWrapper))) {
        // make sure the corresponding non-ghost tuple type also exists
        nonGhostTupleTypeDecl = TupleType(tok, nonGhostDims, allowCreationOfNewType);
      }

      tt = new TupleTypeDecl(argumentGhostness, SystemModule, nonGhostTupleTypeDecl, DontCompile());
      if (tt.NonGhostDims > MaxNonGhostTupleSizeUsed) {
        MaxNonGhostTupleSizeToken = tok;
        MaxNonGhostTupleSizeUsed = tt.NonGhostDims;
      }

      tupleTypeDecls.Add(argumentGhostness, tt);
      SystemModule.SourceDecls.Add(tt);
    }
    return tt;
  }

  public static char IsGhostToChar(bool isGhost) {
    return isGhost ? 'G' : 'O';
  }

  public static bool IsGhostFromChar(char c) {
    Contract.Requires(c == 'G' || c == 'O');
    return c == 'G';
  }

  public static string ArgumentGhostnessToString(List<bool> argumentGhostness) {
    return argumentGhostness.Count + (!argumentGhostness.Contains(true)
      ? "" : String.Concat(argumentGhostness.Select(IsGhostToChar)));
  }

  public static IEnumerable<bool> ArgumentGhostnessFromString(string s, int count) {
    List<bool> argumentGhostness = new bool[count].ToList();
    if (System.Char.IsDigit(s[s.Length - 1])) {
      return argumentGhostness.Select(_ => false);
    } else {
      return argumentGhostness.Select((_, i) => IsGhostFromChar(s[s.Length - count + i]));
    }
  }

  public static string TupleTypeName(List<bool> argumentGhostness) {
    return "_tuple#" + ArgumentGhostnessToString(argumentGhostness);
  }

  public static bool IsTupleTypeName(string s) {
    Contract.Requires(s != null);
    return s.StartsWith("_tuple#");
  }
  public const string TupleTypeCtorNamePrefix = "_#Make";  // the printer wants this name prefix to be uniquely recognizable

  public static string TupleTypeCtorName(int dims) {
    Contract.Assert(0 <= dims);
    return TupleTypeCtorNamePrefix + dims;
  }
}
