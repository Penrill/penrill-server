using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sync.Protocol;

public sealed class BoardDocument
{
    private readonly Dictionary<string, JsonObject> items = new(StringComparer.Ordinal);

    public BoardDocument(string id, long seq, string title, string background, IEnumerable<JsonObject> content)
    {
        Id = id;
        Seq = seq;
        Title = title;
        Background = background;
        foreach (var item in content) items[(string)item["id"]!] = item;
    }

    public string Id { get; }
    public long Seq { get; private set; }
    public string Title { get; private set; }
    public string Background { get; private set; }
    public int ItemCount => items.Count;

    public bool Fits(Change change, int? maxItems = null) =>
        items.Count + change.Additions <= (maxItems ?? BoardLimitPolicy.SelfHostedDefault.MaxItems);

    public (string Title, string Background) MetaAfter(Change change)
    {
        var (title, background) = (Title, Background);
        foreach (var op in change.Ops)
        {
            if (op is not SetMeta meta) continue;
            title = meta.Title ?? title;
            background = meta.Background ?? background;
        }
        return (title, background);
    }

    public void Apply(long seq, Change change)
    {
        foreach (var op in change.Ops)
        {
            switch (op)
            {
                case PutItem put:
                    items[put.ItemId] = (JsonObject)put.Item.DeepClone();
                    break;
                case PatchItem patch when items.TryGetValue(patch.ItemId, out var item):
                    foreach (var (field, value) in patch.Set) item[field] = value?.DeepClone();
                    foreach (var field in patch.Unset) item.Remove(field);
                    break;
                case DeleteItem delete:
                    items.Remove(delete.ItemId);
                    break;
                case SetMeta meta:
                    Title = meta.Title ?? Title;
                    Background = meta.Background ?? Background;
                    break;
            }
        }
        Seq = seq;
    }

    public byte[] SerializeItems()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) WriteItems(writer);
        return buffer.ToArray();
    }

    public void WriteSnapshot(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("id", Id);
        writer.WriteNumber("seq", Seq);
        writer.WriteString("title", Title);
        writer.WriteString("background", Background);
        writer.WritePropertyName("items");
        WriteItems(writer);
        writer.WriteEndObject();
    }

    public static IReadOnlyList<JsonObject> ReadItems(ReadOnlySpan<byte> json)
    {
        if (JsonNode.Parse(json) is not JsonArray array) throw new InvalidDataException("Board items must be a JSON array.");
        var items = array.Select(node => node as JsonObject ?? throw new InvalidDataException("Board item must be an object.")).ToList();
        array.Clear();
        return items;
    }

    private void WriteItems(Utf8JsonWriter writer)
    {
        writer.WriteStartArray();
        foreach (var item in items.Values) item.WriteTo(writer);
        writer.WriteEndArray();
    }
}
