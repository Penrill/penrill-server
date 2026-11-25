namespace Application.Boards.Queries.GetBoards;

/// <summary>A board the user can open. <c>Owner</c> is who made it; empty once that account is gone.</summary>
public sealed record BoardSummaryDto
{
    public string Id { get; init; } = "";

    public string Title { get; init; } = "";

    public long Seq { get; init; }

    public DateTimeOffset Updated { get; init; }

    public string Owner { get; init; } = "";

    /// <summary>Whether the people the owner shares the board with may duplicate, export or copy it out.</summary>
    public bool Copying { get; init; }

    /// <summary>For its owner: whether anyone else can open the board (people, a draw link or a folder).</summary>
    public bool Shared { get; init; }
}
