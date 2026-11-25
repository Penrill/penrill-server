using System.Text.Json.Nodes;

namespace Sync.Protocol;

public abstract record Operation;

public sealed record PutItem(string ItemId, JsonObject Item) : Operation;

public sealed record PatchItem(string ItemId, JsonObject Set, IReadOnlyList<string> Unset) : Operation;

public sealed record DeleteItem(string ItemId) : Operation;

public sealed record SetMeta(string? Title, string? Background) : Operation;

public sealed record Change(string Id, IReadOnlyList<Operation> Ops)
{
    public int Additions => Ops.Count(op => op is PutItem);
}
