using System.Text.Json;

internal static class SpaceMoltJson
{
    public static string? TryGetString(JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            return null;

        if (!obj.TryGetProperty(key, out var prop))
            return null;

        return prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    public static bool? TryGetBool(JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(key, out var prop))
            return null;

        return prop.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    public static int? TryGetInt(JsonElement obj, params string[] keys)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var key in keys)
        {
            if (!obj.TryGetProperty(key, out var prop))
                continue;

            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out int value))
                return value;
        }

        return null;
    }
}
