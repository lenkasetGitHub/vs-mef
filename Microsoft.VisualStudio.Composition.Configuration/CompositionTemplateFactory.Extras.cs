﻿namespace Microsoft.VisualStudio.Composition
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.ComponentModel;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Text;
    using System.Threading.Tasks;
    using Validation;

    partial class CompositionTemplateFactory
    {
        private const string InstantiatedPartLocalVarName = "result";

        private readonly HashSet<Assembly> relevantAssemblies = new HashSet<Assembly>();

        public CompositionConfiguration Configuration { get; set; }

        /// <summary>
        /// Gets the relevant assemblies that must be referenced when compiling the generated code.
        /// </summary>
        public ISet<Assembly> RelevantAssemblies
        {
            get { return this.relevantAssemblies; }
        }

        private IDisposable EmitMemberAssignment(Import import)
        {
            Requires.NotNull(import, "import");

            var importingField = import.ImportingMember as FieldInfo;
            var importingProperty = import.ImportingMember as PropertyInfo;
            Assumes.True(importingField != null || importingProperty != null);

            string tail;
            if (IsPublic(import.ImportingMember, import.PartDefinition.Type, setter: true))
            {
                this.Write("{0}.{1} = ", InstantiatedPartLocalVarName, import.ImportingMember.Name);
                tail = ";";
            }
            else
            {
                if (importingField != null)
                {
                    this.Write(
                        "{0}.SetValue({1}, ",
                        this.GetFieldInfoExpression(importingField),
                        InstantiatedPartLocalVarName);
                    tail = ");";
                }
                else // property
                {
                    this.Write(
                        "{0}.Invoke({1}, new object[] {{ ",
                        this.GetMethodInfoExpression(importingProperty.GetSetMethod(true)),
                        InstantiatedPartLocalVarName);
                    tail = " });";
                }
            }

            return new DisposableWithAction(delegate
            {
                this.WriteLine(tail);
            });
        }

        private string GetFieldInfoExpression(FieldInfo fieldInfo)
        {
            Requires.NotNull(fieldInfo, "fieldInfo");

            if (fieldInfo.DeclaringType.IsGenericType)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.GetField({1}, BindingFlags.Instance | BindingFlags.NonPublic)",
                    this.GetClosedGenericTypeExpression(fieldInfo.DeclaringType),
                    Quote(fieldInfo.Name));
            }
            else
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.ManifestModule.ResolveField({1}/*{2}*/)",
                    this.GetAssemblyExpression(fieldInfo.DeclaringType.Assembly),
                    fieldInfo.MetadataToken,
                    fieldInfo.DeclaringType.Name + "." + fieldInfo.Name);
            }
        }

        private string GetMethodInfoExpression(MethodInfo methodInfo)
        {
            Requires.NotNull(methodInfo, "methodInfo");

            if (methodInfo.DeclaringType.IsGenericType)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "((MethodInfo)MethodInfo.GetMethodFromHandle({0}.ManifestModule.ResolveMethod({1}/*{3}*/).MethodHandle, {2}))",
                    this.GetAssemblyExpression(methodInfo.DeclaringType.Assembly),
                    methodInfo.MetadataToken,
                    this.GetClosedGenericTypeHandleExpression(methodInfo.DeclaringType),
                    methodInfo.DeclaringType + "." + methodInfo.Name);
            }
            else
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "((MethodInfo){0}.ManifestModule.ResolveMethod({1}/*{2}*/))",
                    this.GetAssemblyExpression(methodInfo.DeclaringType.Assembly),
                    methodInfo.MetadataToken,
                    methodInfo.DeclaringType + "." + methodInfo.Name);
            }
        }

        private string GetClosedGenericTypeExpression(Type type)
        {
            Requires.NotNull(type, "type");
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}.ManifestModule.ResolveType({1}/*{3}*/).MakeGenericType({2})",
                this.GetAssemblyExpression(type.Assembly),
                type.GetGenericTypeDefinition().MetadataToken,
                string.Join(", ", type.GetGenericArguments().Select(t => t.IsGenericType && t.ContainsGenericParameters ? GetClosedGenericTypeExpression(t) : GetTypeExpression(t))),
                type.ContainsGenericParameters ? "incomplete" : this.GetTypeName(type, evenNonPublic: true));
        }

        private string GetClosedGenericTypeHandleExpression(Type type)
        {
            return GetClosedGenericTypeExpression(type) + ".TypeHandle";
        }

        private string GetAssemblyExpression(Assembly assembly)
        {
            Requires.NotNull(assembly, "assembly");

            return string.Format(CultureInfo.InvariantCulture, "Assembly.Load({0})", Quote(assembly.FullName));
        }

        private void EmitImportSatisfyingAssignment(KeyValuePair<Import, IReadOnlyList<Export>> satisfyingExport)
        {
            Requires.Argument(satisfyingExport.Key.ImportingMember != null, "satisfyingExport", "No member to satisfy.");
            var import = satisfyingExport.Key;
            var importingMember = satisfyingExport.Key.ImportingMember;
            var exports = satisfyingExport.Value;

            var right = new StringWriter();
            EmitImportSatisfyingExpression(import, exports, right);
            string rightString = right.ToString();
            if (rightString.Length > 0)
            {
                using (this.EmitMemberAssignment(import))
                {
                    this.Write(rightString);
                }
            }
        }

        private void EmitImportSatisfyingExpression(Import import, IReadOnlyList<Export> exports, StringWriter writer)
        {
            if (import.ImportDefinition.Cardinality == ImportCardinality.ZeroOrMore)
            {
                Type enumerableOfTType = typeof(IEnumerable<>).MakeGenericType(import.ImportDefinition.MemberWithoutManyWrapper);
                if (import.ImportDefinition.MemberType.IsArray || import.ImportDefinition.MemberType.IsEquivalentTo(enumerableOfTType))
                {
                    this.EmitSatisfyImportManyArrayOrEnumerable(import, exports);
                }
                else
                {
                    this.EmitSatisfyImportManyCollection(import, exports);
                }
            }
            else if (exports.Any())
            {
                this.EmitValueFactory(import, exports.Single(), writer);
            }
        }

        private void EmitSatisfyImportManyArrayOrEnumerable(Import import, IReadOnlyList<Export> exports)
        {
            Requires.NotNull(import, "import");
            Requires.NotNull(exports, "exports");

            IDisposable memberAssignment = null;
            if (import.ImportingMember != null)
            {
                memberAssignment = this.EmitMemberAssignment(import);
            }

            this.EmitSatisfyImportManyArrayOrEnumerableExpression(import, exports);

            if (memberAssignment != null)
            {
                memberAssignment.Dispose();
            }
        }

        private void EmitSatisfyImportManyArrayOrEnumerableExpression(Import import, IEnumerable<Export> exports)
        {
            Requires.NotNull(import, "import");
            Requires.NotNull(exports, "exports");

            this.Write("new ");
            if (import.ImportDefinition.MemberType.IsArray)
            {
                this.WriteLine("{0}[]", GetTypeName(import.ImportDefinition.MemberWithoutManyWrapper));
            }
            else
            {
                this.WriteLine("List<{0}>", GetTypeName(import.ImportDefinition.MemberWithoutManyWrapper));
            }

            this.WriteLine("{");
            using (Indent())
            {
                foreach (var export in exports)
                {
                    var valueWriter = new StringWriter();
                    EmitValueFactory(import, export, valueWriter);
                    this.WriteLine("{0},", valueWriter);
                }
            }

            this.Write("}");
        }

        private void EmitSatisfyImportManyCollection(Import import, IReadOnlyList<Export> exports)
        {
            Requires.NotNull(import, "import");
            var importDefinition = import.ImportDefinition;
            Type elementType = PartDiscovery.GetElementTypeFromMany(importDefinition.MemberType);
            string elementTypeName = GetTypeName(elementType);
            Type listType = typeof(List<>).MakeGenericType(elementType);

            // Casting the collection to ICollection<T> instead of the concrete type guarantees
            // that we'll be able to call Add(T) and Clear() on it even if the type is NonPublic
            // or its methods are explicit interface implementations.
            string importManyLocalVarTypeName = GetTypeName(typeof(ICollection<>).MakeGenericType(import.ImportDefinition.MemberWithoutManyWrapper));
            if (import.ImportingMember is FieldInfo)
            {
                this.WriteLine("var {0} = ({3}){1}.GetValue({2});", import.ImportingMember.Name, GetFieldInfoExpression((FieldInfo)import.ImportingMember), InstantiatedPartLocalVarName, importManyLocalVarTypeName);
            }
            else
            {
                this.WriteLine("var {0} = ({3}){1}.Invoke({2}, new object[0]);", import.ImportingMember.Name, GetMethodInfoExpression(((PropertyInfo)import.ImportingMember).GetGetMethod(true)), InstantiatedPartLocalVarName, importManyLocalVarTypeName);
            }

            this.WriteLine("if ({0} == null)", import.ImportingMember.Name);
            using (Indent(withBraces: true))
            {
                if (PartDiscovery.IsImportManyCollectionTypeCreateable(importDefinition))
                {
                    if (importDefinition.MemberType.IsAssignableFrom(listType))
                    {
                        this.WriteLine("{0} = new List<{1}>();", import.ImportingMember.Name, elementTypeName);
                    }
                    else
                    {
                        this.Write("{0} = ({1})(", import.ImportingMember.Name, importManyLocalVarTypeName);
                        using (this.EmitConstructorInvocationExpression(importDefinition.MemberType.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new Type[0], null)))
                        {
                            // no arguments to constructor
                        }

                        this.WriteLine(");");
                    }

                    if (import.ImportingMember is FieldInfo)
                    {
                        this.WriteLine("{0}.SetValue({1}, {2});", GetFieldInfoExpression((FieldInfo)import.ImportingMember), InstantiatedPartLocalVarName, import.ImportingMember.Name);
                    }
                    else
                    {
                        this.WriteLine("{0}.Invoke({1}, new object[] {{ {2} }});", GetMethodInfoExpression(((PropertyInfo)import.ImportingMember).GetSetMethod(true)), InstantiatedPartLocalVarName, import.ImportingMember.Name);
                    }
                }
                else
                {
                    this.WriteLine(
                        "throw new InvalidOperationException(\"The {0}.{1} collection must be instantiated by the importing constructor.\");",
                        import.PartDefinition.Type.Name,
                        import.ImportingMember.Name);
                }
            }

            this.WriteLine("else");
            using (Indent(withBraces: true))
            {
                this.WriteLine("{0}.Clear();", import.ImportingMember.Name);
            }

            this.WriteLine(string.Empty);

            foreach (var export in exports)
            {
                var valueWriter = new StringWriter();
                EmitValueFactory(import, export, valueWriter);
                this.WriteLine("{0}.Add({1});", import.ImportingMember.Name, valueWriter);
            }
        }

        private void EmitValueFactory(Import import, Export export, StringWriter writer)
        {
            using (this.ValueFactoryWrapper(import, export, writer))
            {
                if (export.PartDefinition == import.PartDefinition && !import.ImportDefinition.IsExportFactory)
                {
                    // The part is importing itself. So just assign it directly.
                    writer.Write(InstantiatedPartLocalVarName);
                }
                else if (export.IsStaticExport)
                {
                    if (IsPublic(export.ExportingMember, export.PartDefinition.Type))
                    {
                        writer.Write(GetTypeName(export.PartDefinition.Type));
                    }
                    else
                    {
                        // What we write here will be emitted as the argument to a reflection GetValue method call.
                        writer.Write("null");
                    }
                }
                else
                {
                    writer.Write("{0}(", GetPartFactoryMethodName(export.PartDefinition, import.ImportDefinition.Contract.Type.GetGenericArguments().Select(GetTypeName).ToArray()));
                    if (import.ImportDefinition.IsExportFactory)
                    {
                        writer.Write("new Dictionary<Type, object>()");
                    }
                    else
                    {
                        writer.Write("provisionalSharedObjects");
                    }

                    writer.Write("{0})",
                        import.ImportDefinition.RequiredCreationPolicy == CreationPolicy.NonShared ? ", nonSharedInstanceRequired: true" : string.Empty);
                }
            }
        }

        private string GetExportMetadata(Export export)
        {
            var builder = new StringBuilder();
            builder.Append("new Dictionary<string, object> {");
            foreach (var metadatum in export.ExportDefinition.Metadata)
            {
                builder.AppendFormat(" {{ \"{0}\", {1} }}, ", metadatum.Key, GetExportMetadataValueExpression(metadatum.Value));
            }
            builder.Append("}.ToImmutableDictionary()");
            return builder.ToString();
        }

        private string GetExportMetadataValueExpression(object value)
        {
            if (value == null)
            {
                return "null";
            }

            Type valueType = value.GetType();
            if (value is string)
            {
                return "\"" + value + "\"";
            }
            else if (typeof(char).IsEquivalentTo(valueType))
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "'{0}'",
                    (char)value == '\'' ? "\\'" : value);
            }
            else if (typeof(bool).IsEquivalentTo(valueType))
            {
                return (bool)value ? "true" : "false";
            }
            else if (valueType.IsPrimitive)
            {
                return string.Format(CultureInfo.InvariantCulture, "({0}){1}", GetTypeName(valueType), value);
            }
            else if (valueType.IsEquivalentTo(typeof(Guid)))
            {
                return string.Format(CultureInfo.InvariantCulture, "Guid.Parse(\"{0}\")", value);
            }
            else if (valueType.IsEnum)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "({0}){1}",
                    GetTypeName(valueType),
                    Convert.ChangeType(value, Enum.GetUnderlyingType(valueType)));
            }
            else if (typeof(Type).IsAssignableFrom(valueType))
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "({1})typeof({0})",
                    GetTypeName((Type)value),
                    GetTypeName(valueType));
            }
            else if (valueType.IsArray)
            {
                var builder = new StringBuilder();
                builder.AppendFormat("new {0}[] {{ ", GetTypeName(valueType.GetElementType()));
                bool firstValue = true;
                foreach (object element in (Array)value)
                {
                    if (!firstValue)
                    {
                        builder.Append(", ");
                    }

                    builder.Append(GetExportMetadataValueExpression(element));
                    firstValue = false;
                }

                builder.Append("}");
                return builder.ToString();
            }

            throw new NotSupportedException();
        }

        private IDisposable EmitConstructorInvocationExpression(ComposablePartDefinition partDefinition)
        {
            Requires.NotNull(partDefinition, "partDefinition");
            return this.EmitConstructorInvocationExpression(partDefinition.ImportingConstructorInfo);
        }

        private IDisposable EmitConstructorInvocationExpression(ConstructorInfo ctor)
        {
            Requires.NotNull(ctor, "ctor");

            bool publicCtor = IsPublic(ctor, ctor.DeclaringType);
            if (publicCtor)
            {
                this.Write("new {0}(", GetTypeName(ctor.DeclaringType));
            }
            else
            {
                string assemblyExpression = GetAssemblyExpression(ctor.DeclaringType.Assembly);
                string ctorExpression;
                if (ctor.DeclaringType.IsGenericType)
                {
                    ctorExpression = string.Format(
                        CultureInfo.InvariantCulture,
                        "(ConstructorInfo)MethodInfo.GetMethodFromHandle({2}.ManifestModule.ResolveMethod({0}/*{3}*/).MethodHandle, {1})",
                        ctor.MetadataToken,
                        this.GetClosedGenericTypeHandleExpression(ctor.DeclaringType),
                        GetAssemblyExpression(ctor.DeclaringType.Assembly),
                        ctor.DeclaringType.Name + "." + ctor.Name);
                }
                else
                {
                    ctorExpression = string.Format(
                        CultureInfo.InvariantCulture,
                        "(ConstructorInfo){0}.ManifestModule.ResolveMethod({1}/*{2}*/)",
                        GetAssemblyExpression(ctor.DeclaringType.Assembly),
                        ctor.MetadataToken,
                        ctor.DeclaringType.Name + "." + ctor.Name);
                }

                this.Write("({0})({1}).Invoke(new object[] {{", GetTypeName(ctor.DeclaringType), ctorExpression);
            }
            var indent = this.Indent();

            return new DisposableWithAction(delegate
            {
                indent.Dispose();
                if (publicCtor)
                {
                    this.Write(")");
                }
                else
                {
                    this.Write(" })");
                }
            });
        }

        private void EmitInstantiatePart(ComposablePart part)
        {
            if (part.Definition.ImportingConstructor == null)
            {
                this.Write("throw new InvalidOperationException(\"Cannot instantiate this part.\");");
                return;
            }

            this.Write("var {0} = ", InstantiatedPartLocalVarName);
            using (this.EmitConstructorInvocationExpression(part.Definition))
            {
                if (part.Definition.ImportingConstructor.Count > 0)
                {
                    this.WriteLine(string.Empty);
                    bool first = true;
                    foreach (var import in part.GetImportingConstructorImports())
                    {
                        if (!first)
                        {
                            this.WriteLine(",");
                        }

                        var expressionWriter = new StringWriter();
                        this.EmitImportSatisfyingExpression(import.Key, import.Value, expressionWriter);
                        this.Write(expressionWriter.ToString());
                        first = false;
                    }
                }
            }

            this.WriteLine(";");
            if (typeof(IDisposable).IsAssignableFrom(part.Definition.Type))
            {
                this.WriteLine("this.TrackDisposableValue((IDisposable){0});", InstantiatedPartLocalVarName);
            }

            this.WriteLine("provisionalSharedObjects.Add({0}, {1});", GetTypeExpression(part.Definition.Type), InstantiatedPartLocalVarName);

            foreach (var satisfyingExport in part.SatisfyingExports.Where(i => i.Key.ImportingMember != null))
            {
                this.EmitImportSatisfyingAssignment(satisfyingExport);
            }

            if (part.Definition.OnImportsSatisfied != null)
            {
                if (part.Definition.OnImportsSatisfied.DeclaringType.IsInterface)
                {
                    this.WriteLine("var onImportsSatisfiedInterface = ({0}){1};", part.Definition.OnImportsSatisfied.DeclaringType.FullName, InstantiatedPartLocalVarName);
                    this.WriteLine("onImportsSatisfiedInterface.{0}();", part.Definition.OnImportsSatisfied.Name);
                }
                else
                {
                    this.WriteLine("{0}.{1}();", InstantiatedPartLocalVarName, part.Definition.OnImportsSatisfied.Name);
                }
            }

            this.WriteLine("return {0};", InstantiatedPartLocalVarName);
        }

        private HashSet<Type> GetMetadataViewInterfaces()
        {
            var set = new HashSet<Type>();

            set.UnionWith(
                from part in this.Configuration.Parts
                from importAndExports in part.SatisfyingExports
                where importAndExports.Value.Count > 0
                let metadataType = importAndExports.Key.ImportDefinition.MetadataType
                where metadataType != null && metadataType.IsInterface && metadataType != typeof(IDictionary<string, object>)
                select metadataType);

            return set;
        }

        private IDisposable ValueFactoryWrapper(Import import, Export export, TextWriter writer)
        {
            var importDefinition = import.ImportDefinition;

            IDisposable closeLazy = null;
            bool closeParenthesis = false;
            if (importDefinition.IsLazyConcreteType || (export.ExportingMember != null && importDefinition.IsLazy))
            {
                if (IsPublic(importDefinition.ElementType))
                {
                    string lazyTypeName = GetTypeName(LazyPart.FromLazy(importDefinition.MemberWithoutManyWrapper));
                    if (importDefinition.MetadataType == null && importDefinition.Contract.Type.IsEquivalentTo(export.PartDefinition.Type) && import.PartDefinition != export.PartDefinition)
                    {
                        writer.Write("({0})", lazyTypeName);
                    }
                    else
                    {
                        writer.Write("new {0}(() => ", lazyTypeName);
                        closeParenthesis = true;
                    }
                }
                else
                {
                    closeLazy = this.EmitLazyConstruction(importDefinition.ElementType, writer);
                }
            }
            else if (importDefinition.IsExportFactory)
            {
                writer.Write(
                    "new {0}(() => {{ var temp = ",
                    GetTypeName(importDefinition.ExportFactoryType));

                if (importDefinition.ExportFactorySharingBoundaries.Count > 0)
                {
                    writer.Write("new CompiledExportProvider(this, new [] { ");
                    writer.Write(string.Join(", ", importDefinition.ExportFactorySharingBoundaries.Select(Quote)));
                    writer.Write(" }).");
                }

                return new DisposableWithAction(delegate
                {
                    writer.Write(".Value; return Tuple.Create<{0}, Action>(({0})temp, () => {{ ", GetTypeName(importDefinition.ElementType));
                    if (typeof(IDisposable).IsAssignableFrom(export.PartDefinition.Type))
                    {
                        writer.Write("((IDisposable)temp).Dispose(); ");
                    }

                    writer.Write("}); }");
                    this.WriteExportMetadataReference(export, importDefinition, writer);
                    writer.Write(")");
                });
            }
            else if (!IsPublic(export.PartDefinition.Type))
            {
                writer.Write("({0})", GetTypeName(import.ImportDefinition.MemberWithoutManyWrapper));
            }

            if (export.ExportingMember != null && !IsPublic(export.ExportingMember, export.PartDefinition.Type))
            {
                closeParenthesis = true;

                switch (export.ExportingMember.MemberType)
                {
                    case MemberTypes.Field:
                        writer.Write(
                            "({0}){1}.GetValue(",
                            GetTypeName(import.ImportDefinition.ElementType),
                            GetFieldInfoExpression((FieldInfo)export.ExportingMember));
                        break;
                    case MemberTypes.Method:
                        writer.Write(
                            "({0}){1}.CreateDelegate({2}, ",
                            GetTypeName(import.ImportDefinition.ElementType),
                            GetMethodInfoExpression((MethodInfo)export.ExportingMember),
                            GetTypeExpression(import.ImportDefinition.ElementType));
                        break;
                    case MemberTypes.Property:
                        writer.Write(
                            "({0}){1}.Invoke(",
                            GetTypeName(import.ImportDefinition.ElementType),
                            GetMethodInfoExpression(((PropertyInfo)export.ExportingMember).GetGetMethod(true)));
                        break;
                    default:
                        throw new NotSupportedException();
                }
            }

            return new DisposableWithAction(() =>
            {
                string memberModifier = string.Empty;
                if (export.ExportingMember != null)
                {
                    if (IsPublic(export.ExportingMember, export.PartDefinition.Type))
                    {
                        memberModifier = "." + export.ExportingMember.Name;
                    }
                    else
                    {
                        switch (export.ExportingMember.MemberType)
                        {
                            case MemberTypes.Field:
                                memberModifier = ")";
                                break;
                            case MemberTypes.Property:
                                memberModifier = ", new object[0])";
                                break;
                            case MemberTypes.Method:
                                memberModifier = ")";
                                break;
                        }
                    }
                }

                string memberAccessor = memberModifier;
                if ((export.PartDefinition != import.PartDefinition || import.ImportDefinition.IsExportFactory) && !export.IsStaticExport)
                {
                    memberAccessor = ".Value" + memberAccessor;
                }

                if (importDefinition.IsLazy)
                {
                    if (importDefinition.MetadataType != null)
                    {
                        writer.Write(memberAccessor);
                        this.WriteExportMetadataReference(export, importDefinition, writer);
                    }
                    else if (importDefinition.IsLazyConcreteType && !importDefinition.Contract.Type.IsEquivalentTo(export.PartDefinition.Type))
                    {
                        writer.Write(memberAccessor);
                    }
                    else if (closeLazy != null || (export.ExportingMember != null && importDefinition.IsLazy))
                    {
                        writer.Write(memberAccessor);
                    }

                    if (closeLazy != null)
                    {
                        closeLazy.Dispose();
                    }
                    else if (closeParenthesis)
                    {
                        writer.Write(")");
                    }
                }
                else if (import.PartDefinition != export.PartDefinition)
                {
                    writer.Write(memberAccessor);
                }
            });
        }

        private void EmitGetExportsReturnExpression(CompositionContract contract, IEnumerable<Export> exports)
        {
            using (Indent(4))
            {
                if (contract.Type.IsGenericTypeDefinition)
                {
                    // TODO: implement this.
                    this.WriteLine("throw new NotImplementedException();");
                }
                else
                {
                    this.Write("return ");
                    this.EmitSatisfyImportManyArrayOrEnumerableExpression(WrapContractAsImport(contract), exports);
                    this.WriteLine(";");
                }
            }
        }

        private void WriteExportMetadataReference(Export export, ImportDefinition importDefinition, TextWriter writer)
        {
            if (importDefinition.MetadataType != null)
            {
                writer.Write(", ");
                if (importDefinition.MetadataType != typeof(IDictionary<string, object>))
                {
                    writer.Write("new {0}(", GetClassNameForMetadataView(importDefinition.MetadataType));
                }

                writer.Write(GetExportMetadata(export));
                if (importDefinition.MetadataType != typeof(IDictionary<string, object>))
                {
                    writer.Write(")");
                }
            }
        }

        private IEnumerable<IGrouping<CompositionContract, Export>> RootExportsByContract
        {
            get
            {
                return
                    from part in this.Configuration.Parts
                    from exportingMemberAndDefinition in part.Definition.ExportDefinitions
                    let export = new Export(exportingMemberAndDefinition.Value, part.Definition, exportingMemberAndDefinition.Key)
                    where part.RequiredSharingBoundaries.Count == 0
                    where part.Definition.IsInstantiable
                    group export by export.ExportDefinition.Contract into exportsByContract
                    select exportsByContract;
            }
        }

        private string GetTypeName(Type type)
        {
            return this.GetTypeName(type, false, false);
        }

        private string GetTypeName(Type type, bool genericTypeDefinition = false, bool evenNonPublic = false)
        {
            return ReflectionHelpers.GetTypeName(type, genericTypeDefinition, evenNonPublic, this.relevantAssemblies);
        }

        /// <summary>
        /// Gets a C# expression that evaluates to a System.Type instance for the specified type.
        /// </summary>
        private string GetTypeExpression(Type type, bool genericTypeDefinition = false)
        {
            Requires.NotNull(type, "type");

            if (IsPublic(type))
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "typeof({0})",
                    this.GetTypeName(type, genericTypeDefinition));
            }
            else
            {
                var targetType = (genericTypeDefinition && type.IsGenericType) ? type.GetGenericTypeDefinition() : type;
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "Type.GetType({0})",
                    Quote(targetType.AssemblyQualifiedName));
            }
        }

        private static bool IsPublic(Type type)
        {
            Requires.NotNull(type, "type");

            if (type.IsNotPublic)
            {
                return false;
            }

            if (type.IsPublic || type.IsNestedPublic)
            {
                return true;
            }

            return false;
        }

        private string GetClassNameForMetadataView(Type metadataView)
        {
            Requires.NotNull(metadataView, "metadataView");

            if (metadataView.IsInterface)
            {
                return "ClassFor" + metadataView.Name;
            }

            return this.GetTypeName(metadataView);
        }

        private string GetValueOrDefaultForMetadataView(PropertyInfo property, string sourceVarName)
        {
            var defaultValueAttribute = property.GetCustomAttribute<DefaultValueAttribute>();
            if (defaultValueAttribute != null)
            {
                return String.Format(
                    CultureInfo.InvariantCulture,
                    @"({0})({1}.ContainsKey(""{2}"") ? {1}[""{2}""]{4} : {3})",
                    this.GetTypeName(property.PropertyType),
                    sourceVarName,
                    property.Name,
                    GetExportMetadataValueExpression(defaultValueAttribute.Value),
                    property.PropertyType.IsValueType ? string.Empty : (" as " + this.GetTypeName(property.PropertyType)));
            }
            else
            {
                return String.Format(
                    CultureInfo.InvariantCulture,
                    @"({0}){1}[""{2}""]",
                    this.GetTypeName(property.PropertyType),
                    sourceVarName,
                    property.Name);
            }
        }

        private static void Test<T>() { }

        private static string GetPartFactoryMethodNameNoTypeArgs(ComposablePartDefinition part)
        {
            string name = GetPartFactoryMethodName(part);
            int indexOfTypeArgs = name.IndexOf('<');
            if (indexOfTypeArgs >= 0)
            {
                return name.Substring(0, indexOfTypeArgs);
            }

            return name;
        }

        private static string GetPartFactoryMethodName(ComposablePartDefinition part, params string[] typeArguments)
        {
            if (typeArguments == null || typeArguments.Length == 0)
            {
                typeArguments = part.Type.GetGenericArguments().Select(t => t.Name).ToArray();
            }

            string name = "GetOrCreate" + ReflectionHelpers.ReplaceBackTickWithTypeArgs(part.Id, typeArguments);
            return name;
        }

        private static string GetPartFactoryMethodInvokeExpression(ComposablePartDefinition part)
        {
            if (part.Type.IsGenericType)
            {
                return GetGenericPartFactoryMethodInvokeExpression(part);
            }
            else
            {
                return "this." + GetPartFactoryMethodName(part) + "(provisionalSharedObjects)";
            }
        }

        private static string GetGenericPartFactoryMethodInfoExpression(ComposablePartDefinition part)
        {
            return "typeof(CompiledExportProvider).GetMethod(\"" + GetPartFactoryMethodNameNoTypeArgs(part) + "\", BindingFlags.Instance | BindingFlags.NonPublic)"
                + ".MakeGenericMethod(exportDefinition.Contract.Type.GetGenericArguments())";
        }

        private static string GetGenericPartFactoryMethodInvokeExpression(ComposablePartDefinition part)
        {
            return GetGenericPartFactoryMethodInfoExpression(part) +
                ".Invoke(this, new object[] { provisionalSharedObjects, /* nonSharedInstanceRequired: */ false })";
        }

        private string GetPartOrMemberLazy(Export export)
        {
            Requires.NotNull(export, "export");

            MemberInfo member = export.ExportingMember;
            ExportDefinition exportDefinition = export.ExportDefinition;

            string partExpression = GetPartFactoryMethodInvokeExpression(export.PartDefinition);

            if (member == null)
            {
                return partExpression;
            }

            string valueFactoryExpression;
            if (IsPublic(member, export.PartDefinition.Type))
            {
                string memberExpression = string.Format(
                    CultureInfo.InvariantCulture,
                    "({0}).Value.{1}",
                    partExpression,
                    member.Name);
                switch (member.MemberType)
                {
                    case MemberTypes.Method:
                        valueFactoryExpression = string.Format(
                            CultureInfo.InvariantCulture,
                            "new {0}({1})",
                            GetTypeName(exportDefinition.Contract.Type),
                            memberExpression);
                        break;
                    case MemberTypes.Field:
                    case MemberTypes.Property:
                        valueFactoryExpression = memberExpression;
                        break;
                    default:
                        throw new NotSupportedException();
                }
            }
            else
            {
                switch (member.MemberType)
                {
                    case MemberTypes.Method:
                        valueFactoryExpression = string.Format(
                            CultureInfo.InvariantCulture,
                            "({0}){1}.CreateDelegate({3}, ({2}).Value)",
                            GetTypeName(exportDefinition.Contract.Type),
                            GetMethodInfoExpression((MethodInfo)member),
                            partExpression,
                            GetTypeExpression(exportDefinition.Contract.Type));
                        break;
                    case MemberTypes.Field:
                        valueFactoryExpression = string.Format(
                            CultureInfo.InvariantCulture,
                            "({0}){1}.GetValue(({2}).Value)",
                            GetTypeName(((FieldInfo)member).FieldType),
                            GetFieldInfoExpression((FieldInfo)member),
                            partExpression);
                        break;
                    case MemberTypes.Property:
                        valueFactoryExpression = string.Format(
                            CultureInfo.InvariantCulture,
                            "({0}){1}.Invoke(({2}).Value, new object[0])",
                            GetTypeName(((PropertyInfo)member).PropertyType),
                            GetMethodInfoExpression(((PropertyInfo)member).GetGetMethod(true)),
                            partExpression);
                        break;
                    default:
                        throw new NotSupportedException();
                }
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "new LazyPart<{0}>(() => {1})",
                GetTypeName(exportDefinition.Contract.Type),
                valueFactoryExpression);
        }

        private static string Quote(string value)
        {
            return "@\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private static bool IsPublic(MemberInfo memberInfo, Type reflectedType, bool setter = false)
        {
            Requires.NotNull(memberInfo, "memberInfo");
            Requires.NotNull(reflectedType, "reflectedType");
            Requires.Argument(memberInfo.ReflectedType.IsAssignableFrom(reflectedType), "reflectedType", "Type must be the one that defines memberInfo or a derived type.");

            if (!IsPublic(reflectedType))
            {
                return false;
            }

            switch (memberInfo.MemberType)
            {
                case MemberTypes.Constructor:
                    return ((ConstructorInfo)memberInfo).IsPublic;
                case MemberTypes.Field:
                    return ((FieldInfo)memberInfo).IsPublic;
                case MemberTypes.Method:
                    return ((MethodInfo)memberInfo).IsPublic;
                case MemberTypes.Property:
                    var property = (PropertyInfo)memberInfo;
                    var method = setter ? property.GetSetMethod(true) : property.GetGetMethod(true);
                    return IsPublic(method, reflectedType);
                default:
                    throw new NotSupportedException();
            }
        }

        private static Import WrapContractAsImport(CompositionContract contract)
        {
            Requires.NotNull(contract, "contract");

            var importDefinition = new ImportDefinition(
                contract,
                ImportCardinality.ZeroOrMore,
                typeof(IEnumerable<>).MakeGenericType(typeof(ILazy<>).MakeGenericType(contract.Type)),
                ImmutableList.Create<IImportSatisfiabilityConstraint>(),
                CreationPolicy.Any);
            return new Import(importDefinition);
        }

        private IDisposable EmitLazyConstruction(Type valueType, TextWriter writer = null)
        {
            writer = writer ?? new SelfTextWriter(this);
            if (IsPublic(valueType))
            {
                writer.Write("new LazyPart<{0}>(() => ", GetTypeName(valueType));
                return new DisposableWithAction(delegate
                {
                    writer.Write(")");
                });
            }
            else
            {
                var ctor = typeof(LazyPart<>).GetConstructor(new Type[] { typeof(Func<object>) });
                writer.WriteLine(
                    "((ILazy<{4}>)((ConstructorInfo)MethodInfo.GetMethodFromHandle({0}.ManifestModule.ResolveMethod({1}/*{3}*/).MethodHandle, {2})).Invoke(new object[] {{ (Func<object>)(() => ",
                    GetAssemblyExpression(typeof(LazyPart<>).Assembly),
                    ctor.MetadataToken,
                    this.GetClosedGenericTypeHandleExpression(typeof(LazyPart<>).MakeGenericType(valueType)),
                    ctor.DeclaringType.Name + "." + ctor.Name,
                    GetTypeName(valueType));
                var indent = Indent();
                return new DisposableWithAction(delegate
                {
                    indent.Dispose();
                    writer.Write(" ) }))");
                });
            }
        }

        private IDisposable Indent(int count = 1, bool withBraces = false)
        {
            if (withBraces)
            {
                this.WriteLine("{");
            }

            this.PushIndent(new string(' ', count * 4));

            return new DisposableWithAction(delegate
            {
                this.PopIndent();
                if (withBraces)
                {
                    this.WriteLine("}");
                }
            });
        }

        private class SelfTextWriter : TextWriter
        {
            private CompositionTemplateFactory factory;

            internal SelfTextWriter(CompositionTemplateFactory factory)
            {
                this.factory = factory;
            }

            public override Encoding Encoding
            {
                get { return Encoding.Default; }
            }

            public override void Write(char value)
            {
                this.factory.Write(value.ToString());
            }

            public override void Write(string value)
            {
                this.factory.Write(value);
            }
        }
    }
}
