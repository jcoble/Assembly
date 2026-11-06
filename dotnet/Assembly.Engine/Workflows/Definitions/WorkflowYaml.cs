using System.Globalization;
using System.Text.Json;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Assembly.Engine.Workflows.Definitions;

internal static class WorkflowYaml
{
    // Only event parsing is used: no CLR type resolution or reflection-based deserialization.
    internal static JsonElement Parse(string yaml, CancellationToken cancellationToken)
    {
        try
        {
            var parser = new Parser(new StringReader(yaml));
            using var output = new MemoryStream();
            using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { MaxDepth = int.MaxValue }))
            {
                var stack = new Stack<Container>();
                var documents = 0;
                while (parser.MoveNext())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    switch (parser.Current)
                    {
                        case DocumentStart:
                            if (++documents != 1) throw Error("expected one YAML document");
                            break;
                        case AnchorAlias: throw Error("YAML aliases are not supported");
                        case NodeEvent node when !SupportedTag(node): throw Error("custom/non-JSON YAML tags are not supported");
                        case MappingStart:
                            var objectField = BeginValue(stack); writer.WriteStartObject(); stack.Push(new Container(true, objectField)); break;
                        case SequenceStart:
                            var arrayField = BeginValue(stack); writer.WriteStartArray(); stack.Push(new Container(false, arrayField)); break;
                        case MappingEnd:
                            stack.Pop(); writer.WriteEndObject(); break;
                        case SequenceEnd:
                            stack.Pop(); writer.WriteEndArray(); break;
                        case Scalar scalar:
                            if (stack.TryPeek(out var parent) && parent.Mapping && parent.IsKey)
                            {
                                if (!StringScalar(scalar)) throw Error("YAML keys must be strings");
                                parent.CurrentKey = scalar.Value;
                                if (!parent.Keys.Add(scalar.Value)) throw new WorkflowFieldException(parent.ValueField, "duplicate YAML key");
                                writer.WritePropertyName(scalar.Value); parent.IsKey = false;
                            }
                            else { BeginValue(stack); WriteScalar(writer, scalar); }
                            break;
                    }
                }
                if (documents != 1) throw Error("expected one YAML document");
            }
            return JsonDocument.Parse(output.ToArray(), new JsonDocumentOptions { MaxDepth = int.MaxValue }).RootElement.Clone();
        }
        catch (Exception ex) when (ex is YamlException or JsonException or InvalidOperationException or FormatException)
        { throw Error(ex.Message); }
    }

    private static string BeginValue(Stack<Container> stack)
    {
        if (!stack.TryPeek(out var parent)) return "";
        if (!parent.Mapping) return parent.Field + "[]";
        if (parent.IsKey) throw Error("YAML keys must be strings");
        parent.IsKey = true;
        return parent.ValueField;
    }

    private static void WriteScalar(Utf8JsonWriter writer, Scalar scalar)
    {
        var value = scalar.Value;
        var tag = scalar.Tag.IsEmpty ? "" : scalar.Tag.Value;
        if (tag == "tag:yaml.org,2002:str" || tag.Length == 0 && scalar.Style != ScalarStyle.Plain) { writer.WriteStringValue(value); return; }
        if (tag == "tag:yaml.org,2002:bool")
        {
            if (!bool.TryParse(value, out var boolean)) throw Error("invalid boolean tag value");
            writer.WriteBooleanValue(boolean); return;
        }
        if (tag == "tag:yaml.org,2002:null")
        {
            if (value is not ("null" or "Null" or "NULL" or "~" or "")) throw Error("invalid null tag value");
            writer.WriteNullValue(); return;
        }
        if (tag == "tag:yaml.org,2002:int")
        {
            if (long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var tagged) || TryRadix(value, out tagged)) writer.WriteNumberValue(tagged);
            else throw Error("invalid integer tag value");
            return;
        }
        if (tag == "tag:yaml.org,2002:float")
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var floating) || !double.IsFinite(floating)) throw Error("invalid float tag value");
            writer.WriteNumberValue(floating); return;
        }
        if (value is "null" or "Null" or "NULL" or "~" or "") writer.WriteNullValue();
        else if (value is "true" or "True" or "TRUE") writer.WriteBooleanValue(true);
        else if (value is "false" or "False" or "FALSE") writer.WriteBooleanValue(false);
        else if (long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer)) writer.WriteNumberValue(integer);
        else if ((value.StartsWith("0x", StringComparison.Ordinal) || value.StartsWith("0o", StringComparison.Ordinal)) &&
            TryRadix(value, out integer)) writer.WriteNumberValue(integer);
        else if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            if (!double.IsFinite(number)) throw Error("number must be JSON-compatible");
            writer.WriteNumberValue(number);
        }
        else if (value.ToLowerInvariant() is ".nan" or ".inf" or "-.inf" or "+.inf") throw Error("number must be JSON-compatible");
        else writer.WriteStringValue(value);
    }

    private static bool SupportedTag(NodeEvent node)
    {
        if (node.Tag.IsEmpty) return true;
        var tag = node.Tag.Value;
        return node switch
        {
            Scalar => tag is "tag:yaml.org,2002:str" or "tag:yaml.org,2002:bool" or "tag:yaml.org,2002:int" or "tag:yaml.org,2002:float" or "tag:yaml.org,2002:null",
            MappingStart => tag == "tag:yaml.org,2002:map",
            SequenceStart => tag == "tag:yaml.org,2002:seq",
            _ => false
        };
    }
    private static bool StringScalar(Scalar scalar)
    {
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes)) WriteScalar(writer, scalar);
        return bytes.GetBuffer()[0] == (byte)'"';
    }
    private static bool TryRadix(string value, out long number)
    {
        try
        {
            if (!value.StartsWith("0x", StringComparison.Ordinal) && !value.StartsWith("0o", StringComparison.Ordinal)) { number = 0; return false; }
            number = Convert.ToInt64(value[2..], value[1] == 'x' ? 16 : 8); return true;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException) { number = 0; return false; }
    }
    private static WorkflowFieldException Error(string message) => new("yaml", message);
    private sealed class Container(bool mapping, string field)
    {
        public bool Mapping { get; } = mapping;
        public string Field { get; } = field;
        public string CurrentKey { get; set; } = "";
        public string ValueField => Field.Length == 0 ? CurrentKey : Field + "." + CurrentKey;
        public bool IsKey { get; set; } = true;
        public HashSet<string> Keys { get; } = new(StringComparer.Ordinal);
    }
}
