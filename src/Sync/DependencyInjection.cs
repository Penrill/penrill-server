using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sync.Live;

namespace Sync;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the shared live board room engine. The host supplies <see cref="IBoardStore"/>,
    /// <see cref="ILiveAuthenticator"/> and <see cref="ILiveAccessPolicy"/>, and may optionally replace
    /// <see cref="IBoardHistorySink"/>, <see cref="ILiveEventObserver"/>, <see cref="IRoomBackplane"/> or <see cref="RoomOptions"/>.
    /// </summary>
    public static IServiceCollection AddBoardSync(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(new RoomOptions());
        services.TryAddSingleton<IBoardHistorySink, NullBoardHistorySink>();
        services.TryAddSingleton<ILiveEventObserver, NullLiveEventObserver>();
        services.TryAddSingleton<IRoomBackplane, InProcessRoomBackplane>();
        services.AddSingleton<RoomRegistry>();
        services.AddSingleton<LiveConnections>();
        services.AddSingleton<LiveSessions>();
        services.AddHostedService<RoomJanitor>();
        return services;
    }
}
