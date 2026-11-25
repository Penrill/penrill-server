using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;

namespace Sync.Protocol;

internal static class Json
{
    public static bool TryText(JsonNode? node, int max, [NotNullWhen(true)] out string? text)
    {
        text = null;
        if (node is not JsonValue value || !value.TryGetValue(out string? candidate) ||
            string.IsNullOrWhiteSpace(candidate) || candidate.Length > max) return false;
        text = candidate;
        return true;
    }

    public static bool HasOnly(JsonObject node, params ReadOnlySpan<string> keys)
    {
        foreach (var (key, _) in node)
            if (!keys.Contains(key)) return false;
        return true;
    }

    public static string? TypeOf(JsonObject message) => TryText(message["type"], 32, out var type) ? type : null;
}
