using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;

using Penrill.Domain.Constants;

namespace Penrill.Domain.Boards;

public sealed record BoardContent(string Title, string Background, IReadOnlyList<JsonObject> Items)
{
    public static bool TryParse(JsonNode? node, [NotNullWhen(true)] out BoardContent? content)
    {
        content = null;
        if (node is not JsonObject root || !Json.HasOnly(root, "title", "background", "items") ||
            !Json.TryText(root["title"], BoardLimits.MaxTitle, out var title) ||
            !Json.TryText(root["background"], BoardLimits.MaxBackground, out var background) ||
            root["items"] is not JsonArray array || array.Count > BoardLimits.MaxItems) return false;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<JsonObject>(array.Count);
        foreach (var entry in array)
        {
            if (entry is not JsonObject item || !Json.TryText(item["id"], BoardLimits.MaxItemId, out var id) || !ids.Add(id)) return false;
            items.Add(item);
        }
        array.Clear();
        content = new BoardContent(title, background, items);
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
