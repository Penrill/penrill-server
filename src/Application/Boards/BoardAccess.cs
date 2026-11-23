using Penrill.Application.Common.Exceptions;
using Penrill.Application.Common.Interfaces;

namespace Penrill.Application.Boards;

public enum BoardAccess { Missing, Denied, Allowed }

/// <summary>
/// Who may open a board: its owner, members of a folder it is shared into, and people let onto it by name or by its
/// draw link. Whoever may open a board may also change it.
/// </summary>
public static class BoardAccessExtensions
{
    /// <summary>The reason of a 404 for a board its owner deleted and took off everyone's devices.</summary>
    public const string BoardRemoved = "removed";

    public static IQueryable<Board> VisibleTo(this IQueryable<Board> boards, IApplicationDbContext context, string user) =>
        boards.Where(b => b.Owner == user
            || context.BoardPeople.Any(p => p.BoardId == b.Id && p.UserId == user)
            || context.FolderBoards.Any(fb => fb.BoardId == b.Id && context.FolderMembers.Any(m => m.FolderId == fb.FolderId && m.UserId == user)));

    public static async Task<BoardAccess> AccessAsync(this IApplicationDbContext context, string user, string board, CancellationToken cancellationToken)
    {
        if (!await context.Boards.AnyAsync(b => b.Id == board, cancellationToken)) return BoardAccess.Missing;
        return await context.Boards.Where(b => b.Id == board).VisibleTo(context, user).AnyAsync(cancellationToken)
            ? BoardAccess.Allowed : BoardAccess.Denied;
    }

    /// <summary>Throws <see cref="NotFoundException"/> or <see cref="ForbiddenAccessException"/> unless the user may open the board.</summary>
    public static async Task EnsureAccessAsync(this IApplicationDbContext context, string user, string board, CancellationToken cancellationToken)
    {
        switch (await context.AccessAsync(user, board, cancellationToken))
        {
            case BoardAccess.Missing:
                if (await context.RemovedBoards.AnyAsync(b => b.Id == board, cancellationToken)) throw new MissingException(BoardRemoved);
                throw new NotFoundException(board, nameof(Board));
            case BoardAccess.Denied: throw new ForbiddenAccessException();
        }
    }

    /// <summary>True when the board exists and <paramref name="user"/> owns it.</summary>
    public static Task<bool> OwnsAsync(this IApplicationDbContext context, string user, string board, CancellationToken cancellationToken) =>
        context.Boards.AnyAsync(b => b.Id == board && b.Owner == user, cancellationToken);
}

/// <summary>Validation shared by requests that name a board.</summary>
public static class BoardRules
{
    public static IRuleBuilderOptions<T, string> MustBeBoardId<T>(this IRuleBuilder<T, string> rule) =>
        rule.Must(BoardLimits.IsBoardId).WithMessage("Not a board id.");
}
