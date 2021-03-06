using Microsoft.CodeAnalysis;
using Semmle.Extraction.CSharp.Populators;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Semmle.Extraction.Entities;
using System.IO;

namespace Semmle.Extraction.CSharp.Entities
{
    internal class Parameter : CachedSymbol<IParameterSymbol>, IExpressionParentEntity
    {
        protected IEntity Parent { get; set; }
        protected Parameter Original { get; }

        protected Parameter(Context cx, IParameterSymbol init, IEntity parent, Parameter original)
            : base(cx, init)
        {
            Parent = parent;
            Original = original ?? this;
        }

        public override Microsoft.CodeAnalysis.Location ReportingLocation => Symbol.GetSymbolLocation();

        public enum Kind
        {
            None, Ref, Out, Params, This, In
        }

        protected virtual int Ordinal
        {
            get
            {
                // For some reason, methods of kind ReducedExtension
                // omit the "this" parameter, so the parameters are
                // actually numbered from 1.
                // This is to be consistent from the original (unreduced) extension method.
                var isReducedExtension =
                    Symbol.ContainingSymbol is IMethodSymbol method &&
                    method.MethodKind == MethodKind.ReducedExtension;
                return Symbol.Ordinal + (isReducedExtension ? 1 : 0);
            }
        }

        private Kind ParamKind
        {
            get
            {
                switch (Symbol.RefKind)
                {
                    case RefKind.Out:
                        return Kind.Out;
                    case RefKind.Ref:
                        return Kind.Ref;
                    case RefKind.In:
                        return Kind.In;
                    default:
                        if (Symbol.IsParams)
                            return Kind.Params;

                        if (Ordinal == 0)
                        {
                            if (Symbol.ContainingSymbol is IMethodSymbol method && method.IsExtensionMethod)
                                return Kind.This;
                        }
                        return Kind.None;
                }
            }
        }

        public static Parameter Create(Context cx, IParameterSymbol param, IEntity parent, Parameter original = null) =>
            ParameterFactory.Instance.CreateEntity(cx, param, (param, parent, original));

        public static Parameter Create(Context cx, IParameterSymbol param) =>
            ParameterFactory.Instance.CreateEntity(cx, param, (param, null, null));

        public override void WriteId(TextWriter trapFile)
        {
            if (Parent == null)
                Parent = Method.Create(Context, Symbol.ContainingSymbol as IMethodSymbol);
            trapFile.WriteSubId(Parent);
            trapFile.Write('_');
            trapFile.Write(Ordinal);
            trapFile.Write(";parameter");
        }

        public override bool NeedsPopulation => true;

        private string Name
        {
            get
            {
                // Very rarely, two parameters have the same name according to the data model.
                // This breaks our database constraints.
                // Generate an impossible name to ensure that it doesn't conflict.
                var conflictingCount = Symbol.ContainingSymbol.GetParameters().Count(p => p.Ordinal < Symbol.Ordinal && p.Name == Symbol.Name);
                return conflictingCount > 0 ? Symbol.Name + "`" + conflictingCount : Symbol.Name;
            }
        }

        public override void Populate(TextWriter trapFile)
        {
            PopulateAttributes();
            PopulateNullability(trapFile, Symbol.GetAnnotatedType());
            PopulateRefKind(trapFile, Symbol.RefKind);

            if (Symbol.Name != Original.Symbol.Name)
                Context.ModelError(Symbol, "Inconsistent parameter declaration");

            var type = Type.Create(Context, Symbol.Type);
            trapFile.@params(this, Name, type.TypeRef, Ordinal, ParamKind, Parent, Original);

            foreach (var l in Symbol.Locations)
                trapFile.param_location(this, Context.CreateLocation(l));

            if (!Symbol.Locations.Any() &&
                Symbol.ContainingSymbol is IMethodSymbol ms &&
                ms.Name == WellKnownMemberNames.TopLevelStatementsEntryPointMethodName &&
                ms.ContainingType.Name == WellKnownMemberNames.TopLevelStatementsEntryPointTypeName)
            {
                trapFile.param_location(this, Context.CreateLocation());
            }

            if (!IsSourceDeclaration || !Symbol.FromSource())
                return;

            BindComments();

            if (IsSourceDeclaration)
            {
                foreach (var syntax in Symbol.DeclaringSyntaxReferences
                    .Select(d => d.GetSyntax())
                    .OfType<ParameterSyntax>()
                    .Where(s => s.Type != null))
                {
                    TypeMention.Create(Context, syntax.Type, this, type);
                }
            }

            if (Symbol.HasExplicitDefaultValue && Context.Defines(Symbol))
            {
                // This is a slight bug in the dbscheme
                // We should really define param_default(param, string)
                // And use parameter child #0 to encode the default expression.
                var defaultValue = GetParameterDefaultValue(Symbol);
                if (defaultValue == null)
                {
                    // In case this parameter belongs to an accessor of an indexer, we need
                    // to get the default value from the corresponding parameter belonging
                    // to the indexer itself
                    var method = (IMethodSymbol)Symbol.ContainingSymbol;
                    if (method != null)
                    {
                        var i = method.Parameters.IndexOf(Symbol);
                        var indexer = (IPropertySymbol)method.AssociatedSymbol;
                        if (indexer != null)
                            defaultValue = GetParameterDefaultValue(indexer.Parameters[i]);
                    }
                }

                if (defaultValue != null)
                {
                    Context.PopulateLater(() =>
                    {
                        Expression.Create(Context, defaultValue.Value, this, 0);
                    });
                }
            }
        }

        public override bool IsSourceDeclaration => Symbol.IsSourceDeclaration();

        bool IExpressionParentEntity.IsTopLevelParent => true;

        private static EqualsValueClauseSyntax GetParameterDefaultValue(IParameterSymbol parameter)
        {
            var syntax = parameter.DeclaringSyntaxReferences.Select(@ref => @ref.GetSyntax()).OfType<ParameterSyntax>().FirstOrDefault();
            return syntax?.Default;
        }

        private class ParameterFactory : CachedEntityFactory<(IParameterSymbol, IEntity, Parameter), Parameter>
        {
            public static ParameterFactory Instance { get; } = new ParameterFactory();

            public override Parameter Create(Context cx, (IParameterSymbol, IEntity, Parameter) init) => new Parameter(cx, init.Item1, init.Item2, init.Item3);
        }

        public override TrapStackBehaviour TrapStackBehaviour => TrapStackBehaviour.OptionalLabel;
    }

    internal class VarargsType : Type
    {
        private VarargsType(Context cx)
            : base(cx, null) { }

        public override void Populate(TextWriter trapFile)
        {
            trapFile.types(this, Kinds.TypeKind.ARGLIST, "__arglist");
            trapFile.parent_namespace(this, Namespace.Create(Context, Context.Compilation.GlobalNamespace));
            Modifier.HasModifier(Context, trapFile, this, "public");
        }

        public override bool NeedsPopulation => true;

        public override void WriteId(TextWriter trapFile)
        {
            trapFile.Write("__arglist;type");
        }

        public override int GetHashCode()
        {
            return 98735267;
        }

        public override bool Equals(object obj)
        {
            return obj != null && obj.GetType() == typeof(VarargsType);
        }

        public static VarargsType Create(Context cx) => VarargsTypeFactory.Instance.CreateEntity(cx, typeof(VarargsType), null);

        private class VarargsTypeFactory : CachedEntityFactory<string, VarargsType>
        {
            public static VarargsTypeFactory Instance { get; } = new VarargsTypeFactory();

            public override VarargsType Create(Context cx, string init) => new VarargsType(cx);
        }
    }

    internal class VarargsParam : Parameter
    {
        private VarargsParam(Context cx, Method methodKey)
            : base(cx, null, methodKey, null) { }

        public override void Populate(TextWriter trapFile)
        {
            var typeKey = VarargsType.Create(Context);
            // !! Maybe originaldefinition is wrong
            trapFile.@params(this, "", typeKey, Ordinal, Kind.None, Parent, this);
            trapFile.param_location(this, GeneratedLocation.Create(Context));
        }

        protected override int Ordinal => ((Method)Parent).OriginalDefinition.Symbol.Parameters.Length;

        public override int GetHashCode()
        {
            return 9873567;
        }

        public override bool Equals(object obj)
        {
            return obj != null && obj.GetType() == typeof(VarargsParam);
        }

        public static VarargsParam Create(Context cx, Method method) => VarargsParamFactory.Instance.CreateEntity(cx, typeof(VarargsParam), method);

        private class VarargsParamFactory : CachedEntityFactory<Method, VarargsParam>
        {
            public static VarargsParamFactory Instance { get; } = new VarargsParamFactory();

            public override VarargsParam Create(Context cx, Method init) => new VarargsParam(cx, init);
        }
    }

    internal class ConstructedExtensionParameter : Parameter
    {
        private readonly ITypeSymbol constructedType;

        private ConstructedExtensionParameter(Context cx, Method method, Parameter original)
            : base(cx, original.Symbol, method, original)
        {
            constructedType = method.Symbol.ReceiverType;
        }

        public override void Populate(TextWriter trapFile)
        {
            var typeKey = Type.Create(Context, constructedType);
            trapFile.@params(this, Original.Symbol.Name, typeKey.TypeRef, 0, Kind.This, Parent, Original);
            trapFile.param_location(this, Original.Location);
        }

        public static ConstructedExtensionParameter Create(Context cx, Method method, Parameter parameter) =>
            ExtensionParamFactory.Instance.CreateEntity(cx, (new SymbolEqualityWrapper(parameter.Symbol), new SymbolEqualityWrapper(method.Symbol.ReceiverType)), (method, parameter));

        private class ExtensionParamFactory : CachedEntityFactory<(Method, Parameter), ConstructedExtensionParameter>
        {
            public static ExtensionParamFactory Instance { get; } = new ExtensionParamFactory();

            public override ConstructedExtensionParameter Create(Context cx, (Method, Parameter) init) =>
                new ConstructedExtensionParameter(cx, init.Item1, init.Item2);
        }
    }
}
