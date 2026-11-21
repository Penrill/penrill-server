using System.Collections.Concurrent;
using Penrill.Core.Accounts;
using Penrill.Core.Rooms;

namespace Penrill.Api.Live;

/// <summary>Open live sockets by credential; closes them when the credential is signed out, revoked or disabled.</summary>
public sealed class LiveConnections
{
    public LiveConnections(AccountService accounts) => accounts.CredentialRevoked += CloseSession;

    private readonly ConcurrentDictionary<Guid, (string TokenHash, IRoomMember Member)> open = new();

    public IDisposable Register(string tokenHash, IRoomMember member)
    {
        open[member.Id] = (tokenHash, member);
        return new Registration(this, member.Id);
    }

    public void CloseSession(string tokenHash)
    {
        foreach (var (_, entry) in open)
            if (entry.TokenHash == tokenHash) entry.Member.Close(LiveCloseReason.SessionEnded);
    }

    private sealed class Registration(LiveConnections owner, Guid id) : IDisposable
    {
        public void Dispose() => owner.open.TryRemove(id, out _);
    }
}
