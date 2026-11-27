using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sync.Protocol;

public sealed record BoardContent(string Title, string Background, IReadOnlyList<JsonObject> Items)
{
    public static bool TryParse(JsonNode? node, [NotNullWhen(true)] out BoardContent? content, BoardLimitPolicy? limits = null)
    {
        var maxItems = (limits ?? BoardLimitPolicy.SelfHostedDefault).MaxItems;
        content = null;
        if (node is not JsonObject root || !Json.HasOnly(root, "title", "background", "items") ||
            !TryBoardTitle(root["title"], out var title) ||
            !Json.TryText(root["background"], ProtocolConstants.MaxBackground, out var background) ||
            root["items"] is not JsonArray array || array.Count > maxItems) return false;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<JsonObject>(array.Count);
        foreach (var entry in array)
        {
            if (entry is not JsonObject item || !Json.TryText(item["id"], ProtocolConstants.MaxItemId, out var id) || !ids.Add(id)) return false;
            items.Add(item);
        }
        array.Clear();
        content = new BoardContent(title, background, items);
        return true;
    }

    private static bool TryBoardTitle(JsonNode? node, [NotNullWhen(true)] out string? title)
    {
        title = null;
        if (node is not JsonValue value || !value.TryGetValue(out string? candidate) || candidate.Length > ProtocolConstants.MaxTitle)
            return false;
        title = string.IsNullOrWhiteSpace(candidate) ? "Untitled" : candidate;
        return true;
    }

    public byte[] SerializeItems()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var item in Items) item.WriteTo(writer);
            writer.WriteEndArray();
        }
        return buffer.ToArray();
    }
}
