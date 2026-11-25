using System.Text.Json;
using System.Text.Json.Nodes;
using Application.Common.Exceptions;

namespace Web.Infrastructure;

/// <summary>Request bodies with a size limit, read before anything is stored.</summary>
public static class Bodies
{
    /// <summary>The whole body, or <see cref="PayloadTooLargeException"/> once it passes <paramref name="max"/> bytes.</summary>
    public static async Task<byte[]> ReadAsync(HttpRequest request, int max, CancellationToken cancellationToken)
    {
        if (request.ContentLength > max) throw new PayloadTooLargeException();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > max) throw new PayloadTooLargeException();
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>The body as JSON; null when it is not JSON.</summary>
    public static async Task<JsonNode?> ReadJsonAsync(HttpRequest request, int max, CancellationToken cancellationToken)
    {
        var body = await ReadAsync(request, max, cancellationToken);
        try
        {
            return JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
