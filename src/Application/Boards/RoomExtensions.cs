using Penrill.Application.Live;

namespace Penrill.Application.Boards;

public static class RoomExtensions
{
    /// <summary>Runs <paramref name="action"/> on the board's loaded room; the board is gone when it cannot load.</summary>
    public static async Task<T> WithRoom<T>(this RoomRegistry rooms, string board, CancellationToken cancellationToken, Func<BoardRoom, Task<T>> action)
    {
        using var lease = await rooms.Acquire(board, cancellationToken) ?? throw new NotFoundException(board, nameof(Board));
        return await action(lease.Room);
    }
}
