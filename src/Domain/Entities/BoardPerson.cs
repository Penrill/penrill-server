namespace Penrill.Domain.Entities;

/// <summary>Someone the owner let onto a board by name, or a guest who came in by its draw link.</summary>
public class BoardPerson
{
    public string BoardId { get; set; } = "";

    public string UserId { get; set; } = "";

    /// <summary>The draw link token this person came in by; empty when the owner added them by name.</summary>
    public string Via { get; set; } = "";
}
