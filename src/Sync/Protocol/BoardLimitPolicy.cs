namespace Sync.Protocol;

/// <summary>
/// Runtime board and room limits. Self-hosted servers use <see cref="SelfHostedDefault"/>;
/// cloud hosts compute an instance per workspace plan.
/// </summary>
public sealed record BoardLimitPolicy
{
    public static readonly BoardLimitPolicy SelfHostedDefault = new();

    public int MaxOpsPerChange { get; init; } = 10_000;

    public int MaxChangeBytes { get; init; } = 8 * 1024 * 1024;

    public int MaxBoardBytes { get; init; } = 32 * 1024 * 1024;

    public int MaxItems { get; init; } = 200_000;

    public int MaxAssetBytes { get; init; } = 25 * 1024 * 1024;

    /// <summary>All images on one board together, so a draw link cannot fill the storage.</summary>
    public long MaxBoardAssetBytes { get; init; } = 256L * 1024 * 1024;

    public int MaxSignalBytes { get; init; } = 64 * 1024;

    /// <summary>Optional cap on simultaneous live connections in one board room.</summary>
    public int? MaxConcurrentPeers { get; init; }
}
