using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;

namespace Penrill.Core.Boards;

public static class ChangeCodec
{
    public const string Invalid = "invalid";

    public static bool TryParse(JsonNode? node, [NotNullWhen(true)] out Change? change)
    {
        change = null;
        if (node is not JsonObject root || !Json.HasOnly(root, "id", "ops") ||
            !Json.TryText(root["id"], BoardLimits.MaxChangeId, out var id) ||
            root["ops"] is not JsonArray list || list.Count is 0 or > BoardLimits.MaxOps) return false;
        var ops = new List<Operation>(list.Count);
        foreach (var entry in list)
        {
            if (ParseOperation(entry) is not { } op) return false;
            ops.Add(op);
        }
        change = new Change(id, ops);
        return true;
    }

    public static string? IdOf(JsonNode? node) =>
        node is JsonObject root && Json.TryText(root["id"], BoardLimits.MaxChangeId, out var id) ? id : null;

    private static Operation? ParseOperation(JsonNode? node)
    {
        if (node is not JsonObject op || !Json.TryText(op["op"], 16, out var kind)) return null;
        return kind switch
        {
            "put" => ParsePut(op),
            "patch" => ParsePatch(op),
            "delete" => Json.HasOnly(op, "op", "id") && Json.TryText(op["id"], BoardLimits.MaxItemId, out var id)
                ? new DeleteItem(id) : null,
            "meta" => ParseMeta(op),
            _ => null
        };
    }

    private static PutItem? ParsePut(JsonObject op) =>
        Json.HasOnly(op, "op", "item") && op["item"] is JsonObject item &&
        Json.TryText(item["id"], BoardLimits.MaxItemId, out var id) ? new PutItem(id, item) : null;

    private static PatchItem? ParsePatch(JsonObject op)
    {
        if (!Json.HasOnly(op, "op", "id", "set", "unset") || !Json.TryText(op["id"], BoardLimits.MaxItemId, out var id)) return null;
        var set = op["set"] switch { null => new JsonObject(), JsonObject fields => fields, _ => null };
        if (set is null || set.ContainsKey("id")) return null;
        var unset = new List<string>();
        if (op["unset"] is JsonArray names)
        {
            foreach (var name in names)
            {
                if (!Json.TryText(name, 256, out var field) || field == "id") return null;
                unset.Add(field);
            }
        }
        else if (op["unset"] is not null) return null;
        return set.Count + unset.Count == 0 ? null : new PatchItem(id, set, unset);
    }

    private static SetMeta? ParseMeta(JsonObject op)
    {
        if (!Json.HasOnly(op, "op", "title", "background")) return null;
        string? title = null, background = null;
        if (op["title"] is not null && !Json.TryText(op["title"], BoardLimits.MaxTitle, out title)) return null;
        if (op["background"] is not null && !Json.TryText(op["background"], BoardLimits.MaxBackground, out background)) return null;
        return title is null && background is null ? null : new SetMeta(title, background);
    }
}
