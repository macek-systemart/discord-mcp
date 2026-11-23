using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DiscordMcp.Cli;

/// <summary>Tolerant readers for Discord JSON: ids and bitfields arrive as strings, sometimes as numbers.</summary>
internal static class Json
{
    public static ulong? U64(this JsonElement e, string property) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var v) ? AsU64(v) : null;

    public static ulong? AsU64(this JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => ulong.TryParse(v.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : null,
        JsonValueKind.Number => v.TryGetUInt64(out var n) ? n : null,
        _ => null,
    };

    public static int? I32(this JsonElement e, string property) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
            ? n
            : null;

    public static string? Str(this JsonElement e, string property) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static bool Has(this JsonElement e, string property) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var v) && v.ValueKind != JsonValueKind.Null;

    public static JsonElement? Arr(this JsonElement e, string property) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Array ? v : null;

    public static int Count(this JsonElement? array) => array is { ValueKind: JsonValueKind.Array } a ? a.GetArrayLength() : 0;

    /// <summary>Strips control and bidi characters from names before they reach the terminal.</summary>
    public static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (!char.IsControl(c) && c is not (>= (char)0x202A and <= (char)0x202E) and not (>= (char)0x2066 and <= (char)0x2069))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
