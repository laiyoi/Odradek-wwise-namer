using System.Globalization;
using System.Text;
using OdradekSharp.Rtti;

namespace OdradekSharp.Export;

/// <summary>
/// JSON exporter that reproduces odradek's JsonExporter byte-for-byte:
///   * bare top-level object, no wrapper, no trailing newline
///   * keys in ClassTypeInfo.serializedAttrs() order, original attribute names, no group nesting
///   * null values are skipped entirely
///   * 2-space indentation, LF line endings
///   * byte arrays (Array_uint8/int8) become Base64 strings
///   * enums become their constant name; pointers become "&lt;ref to G:I&gt;"
///   * GGUUID becomes a "8-4-4-4-12" string
/// (see JsonExporter.java:23-131, SimpleTypeVisitor.java:26-53, Ref.java:41-44, ObjectId.java:24-27)
/// </summary>
public static class JsonExporter
{
    public static string Export(TypedObject obj)
    {
        var sb = new StringBuilder();
        WriteObject(sb, obj, 0);
        return sb.ToString();
    }

    private static void WriteObject(StringBuilder sb, TypedObject obj, int indent)
    {
        sb.Append('{');
        var first = true;
        foreach (var attr in obj.Type.SerializedAttrs)
        {
            if (!obj.Fields.TryGetValue(attr.Name, out var value) || value is null) continue;
            if (!first) sb.Append(',');
            first = false;
            sb.Append('\n').Append(' ', (indent + 1) * 2);
            WriteString(sb, attr.Name);
            sb.Append(": ");
            WriteValue(sb, value, attr.Type, indent + 1);
        }
        if (!first) sb.Append('\n').Append(' ', indent * 2);
        sb.Append('}');
    }

    private static void WriteValue(StringBuilder sb, object value, TypeInfo type, int indent)
    {
        switch (value)
        {
            case TypedObject { Type.Name: "GGUUID" } uuid:
                // DS2TypeInfoAdapterFactory / GGUUIDTypeAdapter: render as a canonical GUID string.
                WriteString(sb, FormatUuid(uuid));
                return;
            case TypedObject nested:
                WriteObject(sb, nested, indent);
                return;
            case EnumValue enumValue:
                WriteString(sb, enumValue.Type.Values.Any(v => v.Value == enumValue.Value)
                    ? enumValue.Name
                    : enumValue.Value.ToString(CultureInfo.InvariantCulture));
                return;
            case ObjectRef pointer:
                WriteString(sb, pointer.ToString());
                return;
            case UuidRef uuid:
                WriteString(sb, uuid.ToGuid().ToString("D"));
                return;
            case string s:
                WriteString(sb, s);
                return;
            case bool b:
                sb.Append(b ? "true" : "false");
                return;
            case char c:
                WriteString(sb, c.ToString());
                return;
            case IReadOnlyList<object?> list:
                if (IsByteContainer(type))
                {
                    var bytes = new byte[list.Count];
                    for (var i = 0; i < bytes.Length; i++) bytes[i] = unchecked((byte)Convert.ToSByte(list[i]!));
                    WriteString(sb, Convert.ToBase64String(bytes));
                    return;
                }
                sb.Append('[');
                for (var i = 0; i < list.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('\n').Append(' ', (indent + 1) * 2);
                    var itemType = ((ContainerTypeInfo)type).ItemType;
                    if (list[i] is null) sb.Append("null");
                    else WriteValue(sb, list[i]!, itemType, indent + 1);
                }
                if (list.Count > 0) sb.Append('\n').Append(' ', indent * 2);
                sb.Append(']');
                return;
            case float f:
                sb.Append(FormatFloat(f));
                return;
            case double d:
                sb.Append(FormatDouble(d));
                return;
            case Half h:
                sb.Append(FormatFloat((float)h));
                return;
            case sbyte or byte or short or ushort or int or uint or long or ulong or System.UInt128:
                sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            default:
                WriteString(sb, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
                return;
        }
    }

    private static bool IsByteContainer(TypeInfo type) =>
        type is ContainerTypeInfo { ItemType: AtomTypeInfo atom } &&
        atom.BaseType is "uint8" or "int8";

    /// <summary>
    /// GGUUIDExtension.toDisplayString(): the first three groups are byte-reversed, the rest are printed
    /// in order — "1e8d2e7b-615c-f74f-ae1c-237b58065a3b" from Data0..Data15 = 7B 2E 8D 1E 5C 61 4F F7 AE 1C …
    /// </summary>
    private static string FormatUuid(TypedObject uuid)
    {
        var b = new byte[16];
        for (var i = 0; i < 16; i++) b[i] = unchecked((byte)Convert.ToSByte(uuid.Fields[$"Data{i}"]!));
        return string.Create(36, b, static (span, bytes) =>
        {
            const string hex = "0123456789abcdef";
            var order = new[] { 3, 2, 1, 0, 5, 4, 7, 6, 8, 9, 10, 11, 12, 13, 14, 15 };
            var p = 0;
            for (var i = 0; i < 16; i++)
            {
                if (i is 4 or 6 or 8 or 10) span[p++] = '-';
                var v = bytes[order[i]];
                span[p++] = hex[v >> 4];
                span[p++] = hex[v & 0xF];
            }
        });
    }

    /// <summary>Java's String.valueOf(float) always keeps a decimal point (1.0f -> "1.0").</summary>
    private static string FormatFloat(float value) => EnsureDecimal(value.ToString("R", CultureInfo.InvariantCulture));

    private static string FormatDouble(double value) => EnsureDecimal(value.ToString("R", CultureInfo.InvariantCulture));

    private static string EnsureDecimal(string text)
    {
        if (text.Contains('.') || text.Contains('E') || text.Contains('e') ||
            text.Contains("Infinity") || text.Contains("NaN")) return text;
        return text + ".0";
    }

    private static void WriteString(StringBuilder sb, string value)
    {
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
