namespace Penrill.Application.Boards.Queries.GetBoards;

/// <summary>A board the user can open. <c>Owner</c> is who made it; empty once that account is gone.</summary>
public sealed record BoardSummaryDto
{
    public string Id { get; init; } = "";

    public string Title { get; init; } = "";

    public long Seq { get; init; }

    public DateTimeOffset Updated { get; init; }

    public string Owner { get; init; } = "";

    private class Mapping : Profile
    {
        public Mapping()
        {
            CreateMap<Board, BoardSummaryDto>().ForMember(d => d.Owner, o => o.MapFrom(s => s.Owner ?? ""));
        }
    }
}
