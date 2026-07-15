using System.Text;
using System.Xml;
using Mono.Cecil;

namespace DefScribe;

public sealed class SchemaGenerator(GeneratorOptions options) {
    private const string DefTypeName = "Verse.Def";
    private const string UnsavedAttributeName = "Verse.UnsavedAttribute";
    private const string LoadAliasAttributeName = "Verse.LoadAliasAttribute";
    private const string CustomLoaderMethodName = "LoadDataFromXmlCustom";

    private readonly DefaultAssemblyResolver _resolver = new();
    private readonly Dictionary<string, TypeDefinition> _types = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _definitions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _generating = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _conservativeTypes = new(StringComparer.Ordinal);
    private int _exactPatternCount;

    public GenerationResult Generate() {
        AddResolverDirectory(Path.GetDirectoryName(options.AssemblyPath));
        AddResolverDirectory(options.AssemblyDirectory);

        using var module = ModuleDefinition.ReadModule(
            options.AssemblyPath,
            new ReaderParameters { AssemblyResolver = _resolver });
        IndexTypes(module);

        var rootDefTypes = _types.Values
            .Where(type => !type.IsAbstract && IsAssignableTo(type, DefTypeName))
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToArray();

        foreach (var type in rootDefTypes) {
            EnsureComplexDefinition(type);
        }

        var writer = new XsdWriter();
        writer.StartSchema();
        WriteRootElement(writer, rootDefTypes);
        foreach (var definition in _definitions.OrderBy(pair => pair.Key, StringComparer.Ordinal)) {
            writer.Raw(definition.Value);
        }

        writer.EndSchema();
        return new GenerationResult(writer.ToString(), _exactPatternCount, _conservativeTypes.ToArray());
    }

    private static void WriteRootElement(XsdWriter writer, IReadOnlyList<TypeDefinition> rootDefTypes) {
        writer.Start("element", ("name", "Defs"));
        writer.Start("complexType");
        writer.Start("choice", ("minOccurs", "0"), ("maxOccurs", "unbounded"));

        foreach (var group in rootDefTypes.GroupBy(type => type.Name, StringComparer.Ordinal)) {
            writer.Start("element", ("name", group.Key));
            var types = group.ToArray();
            if (types.Length == 1) {
                writer.Attribute("type", ComplexTypeName(types[0]));
            } else {
                WriteLooseComplexType(writer);
            }

            writer.End();
        }

        writer.End();
        WriteAnyAttribute(writer);
        writer.End();
        writer.End();
    }

    private void AddResolverDirectory(string? directory) {
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory)) {
            _resolver.AddSearchDirectory(directory);
        }
    }

    private void IndexTypes(ModuleDefinition module) {
        foreach (var type in module.Types.SelectMany(Flatten)) {
            _types.TryAdd(type.FullName, type);
        }
    }

    private static IEnumerable<TypeDefinition> Flatten(TypeDefinition type) {
        yield return type;
        foreach (var descendant in type.NestedTypes.SelectMany(Flatten)) {
            yield return descendant;
        }
    }

    private static TypeDefinition? Resolve(TypeReference? type) {
        if (type is null) return null;

        try {
            return type.Resolve();
        } catch (AssemblyResolutionException) {
            return null;
        }
    }

    private void EnsureComplexDefinition(TypeDefinition type) {
        var name = ComplexTypeName(type);
        if (_definitions.ContainsKey(name) || !_generating.Add(name)) return;

        try {
            var definition = new XsdWriter();
            definition.Start("complexType", ("name", name));

            if (HasCustomLoader(type)) {
                _conservativeTypes.Add(type.FullName);
                WriteLooseComplexContent(definition);
            } else {
                _exactPatternCount++;
                var fieldGroups = GetFields(type)
                    .SelectMany(field => GetElementNames(field).Select(n => (Name: n, Field: field)))
                    .GroupBy(item => item.Name, StringComparer.Ordinal)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .ToArray();

                if (fieldGroups.Length > 0) {
                    definition.Start("all");
                    foreach (var group in fieldGroups) {
                        WriteValueElement(
                            definition,
                            group.Key,
                            group.Select(item => item.Field).ToArray(),
                            "0",
                            "1");
                    }

                    definition.End();
                }

                WriteAnyAttribute(definition);
            }

            definition.End();
            _definitions[name] = definition.ToString();
        } finally {
            _generating.Remove(name);
        }
    }

    private static IEnumerable<FieldDefinition> GetFields(TypeDefinition type) {
        var hierarchy = new Stack<TypeDefinition>();
        for (var current = type;
             current is not null && current.FullName != "System.Object";
             current = Resolve(current.BaseType)) {
            hierarchy.Push(current);
        }

        while (hierarchy.Count > 0) {
            foreach (var field in hierarchy.Pop().Fields) {
                if (!field.IsStatic && IsXmlElementName(field.Name) && !IsUnsavedForLoading(field)) {
                    yield return field;
                }
            }
        }
    }

    private static bool IsUnsavedForLoading(FieldDefinition field) {
        var attribute = field.CustomAttributes.FirstOrDefault(candidate =>
            candidate.AttributeType.FullName == UnsavedAttributeName);
        return attribute is not null &&
               (attribute.ConstructorArguments.Count == 0 || attribute.ConstructorArguments[0].Value is not true);
    }

    private static bool IsXmlElementName(string name) {
        try {
            XmlConvert.VerifyNCName(name);
            return true;
        } catch (XmlException) {
            return false;
        }
    }

    private static bool HasCustomLoader(TypeDefinition type) {
        for (var current = type;
             current is not null && current.FullName != "System.Object";
             current = Resolve(current.BaseType)) {
            if (current.Methods.Any(method => method.Name == CustomLoaderMethodName)) {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> GetElementNames(FieldDefinition field) {
        yield return field.Name;
        foreach (var attribute in field.CustomAttributes.Where(candidate =>
                     candidate.AttributeType.FullName == LoadAliasAttributeName)) {
            if (attribute.ConstructorArguments.FirstOrDefault().Value is string alias &&
                !string.IsNullOrWhiteSpace(alias) && IsXmlElementName(alias)) {
                yield return alias;
            }
        }
    }

    private void WriteValueElement(
        XsdWriter writer,
        string name,
        IReadOnlyList<FieldDefinition> fields,
        string minOccurs,
        string maxOccurs) {
        writer.Start("element", ("name", name), ("minOccurs", minOccurs), ("maxOccurs", maxOccurs));
        if (fields.Count == 1) {
            WriteValueType(writer, fields[0].FieldType);
        } else {
            WriteLooseComplexType(writer);
        }

        writer.End();
    }

    private void WriteValueType(XsdWriter writer, TypeReference type) {
        type = UnwrapNullable(type);
        var resolved = Resolve(type);

        if (resolved is not null && IsDef(resolved)) {
            WriteSimpleContent(writer, EnsureDefReferenceType(resolved));
            return;
        }

        if (TryGetCollectionArguments(type, "System.Collections.Generic.List`1", out var listArguments) ||
            TryGetCollectionArguments(type, "System.Collections.Generic.HashSet`1", out listArguments)) {
            WriteListType(writer, listArguments[0]);
            return;
        }

        if (TryGetCollectionArguments(type, "System.Collections.Generic.Dictionary`2", out var dictionaryArguments)) {
            WriteDictionaryType(writer, dictionaryArguments[0], dictionaryArguments[1]);
            return;
        }

        if (resolved?.IsEnum == true) {
            if (HasFlagsAttribute(resolved)) {
                _conservativeTypes.Add(resolved.FullName);
                WriteLooseComplexType(writer);
            } else {
                WriteSimpleContent(writer, EnsureEnumType(resolved));
            }

            return;
        }

        if (TryGetScalarType(type, out var scalarType)) {
            WriteSimpleContent(writer, scalarType);
            return;
        }

        if (resolved is not null && (resolved.IsClass || resolved.IsValueType)) {
            if (HasConcreteSubtype(resolved)) {
                _conservativeTypes.Add(resolved.FullName);
                WriteLooseComplexType(writer);
                return;
            }

            EnsureComplexDefinition(resolved);
            writer.Attribute("type", ComplexTypeName(resolved));
            return;
        }

        _conservativeTypes.Add(type.FullName);
        WriteLooseComplexType(writer);
    }

    private void WriteListType(XsdWriter writer, TypeReference itemType) {
        writer.Start("complexType");
        writer.Start("sequence");
        var resolvedItemType = Resolve(UnwrapNullable(itemType));
        if (resolvedItemType is not null && HasCustomLoader(resolvedItemType)) {
            writer.Empty("any", ("minOccurs", "0"), ("maxOccurs", "unbounded"), ("processContents", "skip"));
        } else {
            WriteValueElement(writer, "li", [CreateSyntheticField(itemType)], "0", "unbounded");
        }

        writer.End();
        WriteAnyAttribute(writer);
        writer.End();
    }

    private void WriteDictionaryType(XsdWriter writer, TypeReference keyType, TypeReference valueType) {
        writer.Start("complexType");
        writer.Start("sequence");
        writer.Start("element", ("name", "li"), ("minOccurs", "0"), ("maxOccurs", "unbounded"));
        writer.Start("complexType");
        writer.Start("all");
        WriteValueElement(writer, "key", [CreateSyntheticField(keyType)], "0", "1");
        WriteValueElement(writer, "value", [CreateSyntheticField(valueType)], "0", "1");
        writer.End();
        WriteAnyAttribute(writer);
        writer.End();
        writer.End();
        writer.End();
        WriteAnyAttribute(writer);
        writer.End();
    }

    private static FieldDefinition CreateSyntheticField(TypeReference type) =>
        new("value", FieldAttributes.Public, type);

    private static TypeReference UnwrapNullable(TypeReference type) {
        return type is GenericInstanceType { ElementType.FullName: "System.Nullable`1" } nullable
            ? nullable.GenericArguments[0]
            : type;
    }

    private static bool TryGetCollectionArguments(
        TypeReference type,
        string collectionTypeName,
        out IReadOnlyList<TypeReference> arguments) {
        if (type is GenericInstanceType generic && generic.ElementType.FullName == collectionTypeName) {
            arguments = generic.GenericArguments.ToArray();
            return true;
        }

        arguments = [];
        return false;
    }

    private static bool IsDef(TypeDefinition type) => IsAssignableTo(type, DefTypeName);

    private static bool HasFlagsAttribute(TypeDefinition type) =>
        type.CustomAttributes.Any(attribute => attribute.AttributeType.FullName == "System.FlagsAttribute");

    private static bool IsAssignableTo(TypeDefinition type, string baseTypeName) {
        for (var current = type;
             current is not null && current.FullName != "System.Object";
             current = Resolve(current.BaseType)) {
            if (current.FullName == baseTypeName) {
                return true;
            }
        }

        return false;
    }

    private bool HasConcreteSubtype(TypeDefinition type) =>
        _types.Values.Any(candidate => !candidate.IsAbstract && candidate.FullName != type.FullName &&
                                       IsAssignableTo(candidate, type.FullName));

    private string EnsureDefReferenceType(TypeDefinition type) {
        var name = $"D_{Sanitize(type.FullName)}";
        if (_definitions.ContainsKey(name)) {
            return name;
        }

        _definitions[name] =
            $"<xs:simpleType xmlns:xs=\"{XsdWriter.Namespace}\" name=\"{name}\"><xs:restriction base=\"xs:string\" /></xs:simpleType>";
        return name;
    }

    private string EnsureEnumType(TypeDefinition type) {
        var name = $"E_{Sanitize(type.FullName)}";
        if (_definitions.ContainsKey(name)) {
            return name;
        }

        var writer = new XsdWriter();
        writer.Start("simpleType", ("name", name));
        writer.Start("restriction", ("base", "xs:string"));
        foreach (var field in type.Fields.Where(field => field.IsStatic)) {
            writer.Empty("enumeration", ("value", field.Name));
        }

        writer.End();
        writer.End();
        _definitions[name] = writer.ToString();
        return name;
    }

    private bool TryGetScalarType(TypeReference type, out string xsdType) {
        xsdType = type.FullName switch {
            "System.String" or "Verse.TaggedString" or "System.Type" => "xs:string",
            "System.Boolean" => EnsureBooleanType(),
            "System.SByte" or "System.Byte" or "System.Int16" or "System.UInt16" or "System.Int32" or
                "System.UInt32" or "System.Int64" or "System.UInt64" => "xs:integer",
            "System.Single" or "System.Double" or "System.Decimal" => "xs:decimal",
            _ => string.Empty
        };
        return xsdType.Length > 0;
    }

    private string EnsureBooleanType() {
        const string booleanType = "B_Boolean";
        if (_definitions.ContainsKey(booleanType)) {
            return booleanType;
        }

        _definitions["B_True"] =
            $"<xs:simpleType xmlns:xs=\"{XsdWriter.Namespace}\" name=\"B_True\"><xs:restriction base=\"xs:string\"><xs:length value=\"4\" /><xs:pattern value=\"[Tt][Rr][Uu][Ee]\" /></xs:restriction></xs:simpleType>";
        _definitions["B_False"] =
            $"<xs:simpleType xmlns:xs=\"{XsdWriter.Namespace}\" name=\"B_False\"><xs:restriction base=\"xs:string\"><xs:length value=\"5\" /><xs:pattern value=\"[Ff][Aa][Ll][Ss][Ee]\" /></xs:restriction></xs:simpleType>";
        _definitions[booleanType] =
            $"<xs:simpleType xmlns:xs=\"{XsdWriter.Namespace}\" name=\"{booleanType}\"><xs:union memberTypes=\"B_True B_False\" /></xs:simpleType>";
        return booleanType;
    }

    private static void WriteSimpleContent(XsdWriter writer, string baseType) {
        writer.Start("complexType");
        writer.Start("simpleContent");
        writer.Start("extension", ("base", baseType));
        WriteAnyAttribute(writer);
        writer.End();
        writer.End();
        writer.End();
    }

    private static void WriteLooseComplexType(XsdWriter writer) {
        writer.Start("complexType", ("mixed", "true"));
        WriteLooseComplexContent(writer);
        writer.End();
    }

    private static void WriteLooseComplexContent(XsdWriter writer) {
        writer.Start("sequence");
        writer.Empty("any", ("minOccurs", "0"), ("maxOccurs", "unbounded"), ("processContents", "skip"));
        writer.End();
        WriteAnyAttribute(writer);
    }

    private static void WriteAnyAttribute(XsdWriter writer) =>
        writer.Empty("anyAttribute", ("processContents", "skip"));

    private static string ComplexTypeName(TypeDefinition type) => $"T_{Sanitize(type.FullName)}";

    private static string Sanitize(string value) {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value) {
            builder.Append(char.IsLetterOrDigit(character) ? character : '_');
        }

        return builder.ToString();
    }
}

public sealed record GenerationResult(string Schema, int ExactPatternCount, IReadOnlyList<string> ConservativeTypes);

internal sealed class XsdWriter {
    public const string Namespace = "http://www.w3.org/2001/XMLSchema";

    private readonly StringBuilder _builder = new();
    private readonly XmlWriter _writer;

    public XsdWriter() {
        _writer = XmlWriter.Create(_builder, new XmlWriterSettings { Indent = true, OmitXmlDeclaration = true });
    }

    public void StartSchema() {
        _writer.WriteStartElement("xs", "schema", Namespace);
        _writer.WriteAttributeString("elementFormDefault", "unqualified");
        _writer.WriteAttributeString("attributeFormDefault", "unqualified");
    }

    public void EndSchema() {
        _writer.WriteEndElement();
        _writer.Flush();
    }

    public void Start(string name, params (string Name, string Value)[] attributes) {
        _writer.WriteStartElement("xs", name, Namespace);
        foreach (var (attributeName, value) in attributes) {
            _writer.WriteAttributeString(attributeName, value);
        }
    }

    public void Attribute(string name, string value) => _writer.WriteAttributeString(name, value);

    public void Empty(string name, params (string Name, string Value)[] attributes) {
        Start(name, attributes);
        End();
    }

    public void End() => _writer.WriteEndElement();

    public void Raw(string xml) => _writer.WriteRaw(xml);

    public override string ToString() {
        _writer.Flush();
        return _builder.ToString();
    }
}