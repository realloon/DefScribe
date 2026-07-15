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
        foreach (var directory in options.AssemblyDirectories) {
            AddResolverDirectory(directory);
        }

        using var module =
            ModuleDefinition.ReadModule(options.AssemblyPath, new ReaderParameters { AssemblyResolver = _resolver });
        IndexTypes(module);

        var rootDefTypes = _types.Values
            .Where(type => !type.IsAbstract && IsAssignableTo(type, DefTypeName))
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToArray();

        foreach (var type in rootDefTypes) {
            EnsureComplexDefinition(type);
        }

        var writer = new RngWriter();
        writer.StartGrammar();
        writer.StartElement("start");
        writer.StartElement("element", ("name", "Defs"));
        writer.StartElement("interleave");
        WriteAnyAttributes(writer);
        writer.StartElement("zeroOrMore");
        writer.StartElement("choice");
        foreach (var type in rootDefTypes) {
            WriteElement(writer, type.Name, () => writer.EmptyElement("ref", ("name", TypeDefinitionName(type))));
        }

        writer.EndElement();
        writer.EndElement();
        writer.EndElement();
        writer.EndElement();
        writer.EndElement();

        WriteAnyContentDefinition(writer);
        foreach (var definition in _definitions.OrderBy(pair => pair.Key, StringComparer.Ordinal)) {
            writer.Raw(definition.Value);
        }

        writer.EndGrammar();

        return new GenerationResult(writer.ToString(), _exactPatternCount, _conservativeTypes.ToArray());
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

    private TypeDefinition? Resolve(TypeReference? type) {
        if (type is null) {
            return null;
        }

        try {
            return type.Resolve();
        } catch (AssemblyResolutionException) {
            return null;
        }
    }

    private void EnsureComplexDefinition(TypeDefinition type) {
        var name = TypeDefinitionName(type);
        if (_definitions.ContainsKey(name) || !_generating.Add(name)) {
            return;
        }

        try {
            var definition = new RngWriter();
            definition.StartElement("define", ("name", name));

            if (HasCustomLoader(type)) {
                _conservativeTypes.Add(type.FullName);
                definition.EmptyElement("ref", ("name", "any-content"));
            } else {
                _exactPatternCount++;
                var fieldGroups = GetFields(type)
                    .GroupBy(field => field.Name, StringComparer.Ordinal)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .ToArray();

                if (fieldGroups.Length == 0) {
                    definition.EmptyElement("empty");
                } else {
                    definition.StartElement("interleave");
                    foreach (var group in fieldGroups) {
                        definition.StartElement("optional");
                        WriteFieldElement(definition, group.ToArray());
                        definition.EndElement();
                    }

                    definition.EndElement();
                }
            }

            definition.EndElement();
            _definitions[name] = definition.ToString();
        } finally {
            _generating.Remove(name);
        }
    }

    private IEnumerable<FieldDefinition> GetFields(TypeDefinition type) {
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
               (attribute.ConstructorArguments.Count == 0 ||
                attribute.ConstructorArguments[0].Value is not true);
    }

    private static bool IsXmlElementName(string name) {
        try {
            XmlConvert.VerifyNCName(name);
            return true;
        } catch (XmlException) {
            return false;
        }
    }

    private bool HasCustomLoader(TypeDefinition type) {
        for (var current = type;
             current is not null && current.FullName != "System.Object";
             current = Resolve(current.BaseType)) {
            if (current.Methods.Any(method => method.Name == CustomLoaderMethodName)) {
                return true;
            }
        }

        return false;
    }

    private void WriteFieldElement(RngWriter writer, IReadOnlyList<FieldDefinition> fields) {
        var elementNames = fields
            .SelectMany(GetElementNames)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        if (elementNames.Length == 1) {
            WriteElement(writer, elementNames[0], () => WriteFieldValue(writer, fields));
            return;
        }

        writer.StartElement("choice");
        foreach (var elementName in elementNames) {
            WriteElement(writer, elementName, () => WriteFieldValue(writer, fields));
        }

        writer.EndElement();
    }

    private static IEnumerable<string> GetElementNames(FieldDefinition field) {
        yield return field.Name;
        foreach (var attribute in field.CustomAttributes.Where(candidate =>
                     candidate.AttributeType.FullName == LoadAliasAttributeName)) {
            if (attribute.ConstructorArguments.FirstOrDefault().Value is string alias &&
                !string.IsNullOrWhiteSpace(alias)) {
                yield return alias;
            }
        }
    }

    private void WriteFieldValue(RngWriter writer, IReadOnlyList<FieldDefinition> fields) {
        if (fields.Count == 1) {
            WriteValuePattern(writer, fields[0].FieldType);
            return;
        }

        writer.StartElement("choice");
        foreach (var field in fields) {
            WriteValuePattern(writer, field.FieldType);
        }

        writer.EndElement();
    }

    private void WriteValuePattern(RngWriter writer, TypeReference type) {
        type = UnwrapNullable(type);
        var resolved = Resolve(type);

        if (resolved is not null && IsDef(resolved)) {
            WriteDefReference(writer, resolved);
            return;
        }

        if (TryGetCollectionArguments(type, "System.Collections.Generic.List`1", out var listArguments) ||
            TryGetCollectionArguments(type, "System.Collections.Generic.HashSet`1", out listArguments)) {
            writer.StartElement("zeroOrMore");
            var itemType = Resolve(UnwrapNullable(listArguments[0]));
            if (itemType is not null && HasCustomLoader(itemType)) {
                WriteAnyElement(writer);
            } else {
                WriteElement(writer, "li", () => WriteValuePattern(writer, listArguments[0]));
            }

            writer.EndElement();
            return;
        }

        if (TryGetCollectionArguments(type, "System.Collections.Generic.Dictionary`2", out var dictionaryArguments)) {
            writer.StartElement("zeroOrMore");
            WriteElement(writer, "li", () => {
                writer.StartElement("interleave");
                WriteElement(writer, "key", () => WriteValuePattern(writer, dictionaryArguments[0]));
                WriteElement(writer, "value", () => WriteValuePattern(writer, dictionaryArguments[1]));
                writer.EndElement();
            });
            writer.EndElement();
            return;
        }

        if (resolved?.IsEnum == true) {
            if (HasFlagsAttribute(resolved)) {
                writer.EmptyElement("ref", ("name", "any-content"));
            } else {
                WriteEnum(writer, resolved);
            }

            return;
        }

        if (TryWriteScalar(writer, type)) {
            return;
        }

        if (resolved is not null && (resolved.IsClass || resolved.IsValueType)) {
            if (HasConcreteSubtype(resolved)) {
                _conservativeTypes.Add(resolved.FullName);
                writer.EmptyElement("ref", ("name", "any-content"));
                return;
            }

            EnsureComplexDefinition(resolved);
            writer.EmptyElement("ref", ("name", TypeDefinitionName(resolved)));
            return;
        }

        _conservativeTypes.Add(type.FullName);
        writer.EmptyElement("ref", ("name", "any-content"));
    }

    private static TypeReference UnwrapNullable(TypeReference type) {
        return type is GenericInstanceType { ElementType.FullName: "System.Nullable`1" } nullable
            ? nullable.GenericArguments[0]
            : type;
    }

    private static bool TryGetCollectionArguments(TypeReference type, string collectionTypeName,
        out IReadOnlyList<TypeReference> arguments) {
        if (type is GenericInstanceType generic && generic.ElementType.FullName == collectionTypeName) {
            arguments = generic.GenericArguments.ToArray();
            return true;
        }

        arguments = Array.Empty<TypeReference>();
        return false;
    }

    private bool IsDef(TypeDefinition type) {
        return IsAssignableTo(type, DefTypeName);
    }

    private static bool HasFlagsAttribute(TypeDefinition type) =>
        type.CustomAttributes.Any(attribute => attribute.AttributeType.FullName == "System.FlagsAttribute");

    private bool IsAssignableTo(TypeDefinition type, string baseTypeName) {
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

    private void WriteDefReference(RngWriter writer, TypeDefinition type) {
        var name = $"def-ref.{Sanitize(type.FullName)}";
        if (!_definitions.ContainsKey(name)) {
            _definitions[name] =
                $"<define name=\"{name}\"><data type=\"string\"><param name=\"pattern\">[A-Za-z0-9_-]+</param></data></define>";
        }

        writer.EmptyElement("ref", ("name", name));
    }

    private static bool TryWriteScalar(RngWriter writer, TypeReference type) {
        switch (type.FullName) {
            case "System.String":
            case "Verse.TaggedString":
            case "System.Type":
                writer.EmptyElement("text");
                return true;
            case "System.Boolean":
                writer.StartElement("choice");
                writer.TextElement("value", "true");
                writer.TextElement("value", "false");
                writer.EndElement();
                return true;
            case "System.SByte":
            case "System.Byte":
            case "System.Int16":
            case "System.UInt16":
            case "System.Int32":
            case "System.UInt32":
            case "System.Int64":
            case "System.UInt64":
                writer.EmptyElement("data", ("type", "integer"));
                return true;
            case "System.Single":
            case "System.Double":
            case "System.Decimal":
                writer.EmptyElement("data", ("type", "decimal"));
                return true;
            default:
                return false;
        }
    }

    private static void WriteEnum(RngWriter writer, TypeDefinition type) {
        var names = type.Fields.Where(field => field.IsStatic).Select(field => field.Name).ToArray();
        writer.StartElement("choice");
        foreach (var name in names) {
            writer.TextElement("value", name);
        }

        writer.EndElement();
    }

    private static void WriteElement(RngWriter writer, string name, Action content) {
        writer.StartElement("element", ("name", name));
        writer.StartElement("interleave");
        WriteAnyAttributes(writer);
        content();
        writer.EndElement();
        writer.EndElement();
    }

    private static void WriteAnyAttributes(RngWriter writer) {
        writer.StartElement("zeroOrMore");
        writer.StartElement("attribute");
        writer.EmptyElement("anyName");
        writer.EmptyElement("text");
        writer.EndElement();
        writer.EndElement();
    }

    private static void WriteAnyContentDefinition(RngWriter writer) {
        writer.StartElement("define", ("name", "any-content"));
        writer.StartElement("interleave");
        writer.StartElement("zeroOrMore");
        writer.StartElement("choice");
        writer.EmptyElement("text");
        writer.StartElement("element");
        writer.EmptyElement("anyName");
        WriteAnyAttributes(writer);
        writer.EmptyElement("ref", ("name", "any-content"));
        writer.EndElement();
        writer.EndElement();
        writer.EndElement();
        writer.EndElement();
        writer.EndElement();
    }

    private static void WriteAnyElement(RngWriter writer) {
        writer.StartElement("element");
        writer.EmptyElement("anyName");
        writer.StartElement("interleave");
        WriteAnyAttributes(writer);
        writer.EmptyElement("ref", ("name", "any-content"));
        writer.EndElement();
        writer.EndElement();
    }

    private static string TypeDefinitionName(TypeDefinition type) => $"type.{Sanitize(type.FullName)}";

    private static string Sanitize(string value) {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value) {
            builder.Append(char.IsLetterOrDigit(character) ? character : '_');
        }

        return builder.ToString();
    }
}

public sealed record GenerationResult(string Schema, int ExactPatternCount, IReadOnlyList<string> ConservativeTypes);

internal sealed class RngWriter {
    private readonly StringBuilder _builder = new();
    private readonly XmlWriter _writer;

    public RngWriter(bool includeXmlDeclaration = false) {
        _writer = XmlWriter.Create(_builder,
            new XmlWriterSettings { Indent = true, OmitXmlDeclaration = !includeXmlDeclaration });
    }

    public void StartGrammar() {
        _writer.WriteStartElement("grammar", "http://relaxng.org/ns/structure/1.0");
        _writer.WriteAttributeString("datatypeLibrary", "http://www.w3.org/2001/XMLSchema-datatypes");
    }

    public void EndGrammar() {
        _writer.WriteEndElement();
        _writer.Flush();
    }

    public void StartElement(string name, params (string Name, string Value)[] attributes) {
        _writer.WriteStartElement(name);
        foreach (var (attributeName, value) in attributes) {
            _writer.WriteAttributeString(attributeName, value);
        }
    }

    public void EndElement() => _writer.WriteEndElement();

    public void EmptyElement(string name, params (string Name, string Value)[] attributes) {
        _writer.WriteStartElement(name);
        foreach (var (attributeName, value) in attributes) {
            _writer.WriteAttributeString(attributeName, value);
        }

        _writer.WriteEndElement();
    }

    public void TextElement(string name, string text) {
        _writer.WriteElementString(name, text);
    }

    public void Raw(string xml) {
        _writer.WriteRaw(xml);
    }

    public override string ToString() {
        _writer.Flush();
        return _builder.ToString();
    }
}