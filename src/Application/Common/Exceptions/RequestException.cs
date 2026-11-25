namespace Application.Common.Exceptions;

/// <summary>
/// A request the use case refuses for a reason the client can act on. <see cref="Reason"/>, when set,
/// is a short machine-readable word editors read (for example <c>hash-mismatch</c> or <c>board-full</c>).
/// </summary>
public abstract class RequestException(string? reason, string message) : Exception(message)
{
    public string? Reason { get; } = reason;
}

/// <summary>The request is malformed (400).</summary>
public sealed class BadRequestException(string? reason = null) : RequestException(reason, "The request is not valid.");

/// <summary>What was asked for does not exist, with a reason such as <c>removed</c> (404).</summary>
public sealed class MissingException(string? reason = null) : RequestException(reason, "The specified resource was not found.");

/// <summary>The request clashes with what exists, such as a taken name (409).</summary>
public sealed class ConflictException(string? reason = null) : RequestException(reason, "The request conflicts with the current state.");

/// <summary>What was asked for is gone for good, such as changes older than the kept log (410).</summary>
public sealed class GoneException(string? reason = null) : RequestException(reason, "The resource is gone.");

/// <summary>The content is larger than allowed (413).</summary>
public sealed class PayloadTooLargeException(string? reason = null) : RequestException(reason, "The content is too large.");

/// <summary>The content type is not accepted (415).</summary>
public sealed class UnsupportedMediaTypeException(string? reason = null) : RequestException(reason, "The content type is not supported.");

/// <summary>The host cannot do it right now; trying again later may work (503).</summary>
public sealed class ServiceUnavailableException(string? reason = null) : RequestException(reason, "The service is unavailable.");
