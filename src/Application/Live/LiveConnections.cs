using System.Collections.Concurrent;

namespace Penrill.Application.Live;

/// <summary>Open live connections by credential; closes them when the credential is signed out, revoked or disabled.</summary>
public sealed class LiveConnections
{
    private readonly ConcurrentDictionary<Guid, (string CredentialId, IRoomMember Member)> _open = new();

    public IDisposable Register(string credentialId, IRoomMember member)
    {
        _open[member.Id] = (credentialId, member);
        return new Registration(this, member.Id);
    }

    public void CloseCredential(string credentialId)
    {
        foreach (var (_, entry) in _open)
            if (entry.CredentialId == credentialId) entry.Member.Close(LiveCloseReason.SessionEnded);
    }

    private sealed class Registration(LiveConnections owner, Guid id) : IDisposable
    {
        public void Dispose() => owner._open.TryRemove(id, out _);
    }
}
