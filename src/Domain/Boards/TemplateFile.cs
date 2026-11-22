using System.Text.Json;

namespace Penrill.Domain.Boards;

/// <summary>A board file as the editor saves it, the body of a shared template.</summary>
public static class TemplateFile
{
    public static bool IsBoard(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var reader = new Utf8JsonReader(bytes);
            using var json = JsonDocument.ParseValue(ref reader);
            var root = json.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.String && format.ValueEquals("inkboard") &&
                root.TryGetProperty("version", out var version) && version.TryGetInt32(out var number) && number >= 1 &&
                root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
