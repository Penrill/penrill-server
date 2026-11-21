using System.Collections.Concurrent;
using System.Security.Cryptography;
using Penrill.Core.Accounts;

namespace Penrill.Core.Linking;

/// <summary>
/// The life of one link request (State pattern). Every transition goes through <see cref="LinkRequest"/>,
/// so the HTTP layer cannot approve an expired request or hand out a token twice.
/// <code>
/// Pending --allow--> Approved --poll--> Consumed
///    | deny              |
///    v                   +--expire--> Expired
///  Denied
/// </code>
/// </summary>
public enum LinkState { Pending, Approved, Denied, Consumed, Expired }

public sealed record LinkStart(string DeviceCode, string UserCode, DateTimeOffset Expires);

/// <summary>What the approval page shows: which device asks, from where, and when.</summary>
public sealed record LinkInfo(string UserCode, string Device, string App, string Address, DateTimeOffset Created, LinkState State);

public enum PollStatus { Pending, Approved, Denied, Expired }

public readonly record struct PollResult(PollStatus Status, string? User = null, string? Token = null);

internal sealed class LinkRequest(string deviceHash, string userCode, string device, string app, string address, DateTimeOffset created, DateTimeOffset expires)
{
    private readonly TaskCompletionSource decided = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private LinkState state = LinkState.Pending;

    public string DeviceHash { get; } = deviceHash;
    public string UserCode { get; } = userCode;
    public DateTimeOffset Expires { get; } = expires;
    public string? User { get; private set; }

    public LinkInfo Info(DateTimeOffset now) => new(UserCode, device, app, address, created, State(now));

    public string Device => device;

    public Task Decided => decided.Task;

    public LinkState State(DateTimeOffset now)
    {
        lock (decided)
            return state is LinkState.Pending or LinkState.Approved && now >= Expires ? LinkState.Expired : state;
    }

    public bool Decide(string user, bool allow, DateTimeOffset now)
    {
        lock (decided)
        {
            if (state != LinkState.Pending || now >= Expires) return false;
            state = allow ? LinkState.Approved : LinkState.Denied;
            User = allow ? user : null;
        }
        decided.TrySetResult();
        return true;
    }

    /// <summary>Moves an approved request to consumed. Only one caller ever gets true.</summary>
    public bool TryConsume(DateTimeOffset now)
    {
        lock (decided)
        {
            if (state != LinkState.Approved || now >= Expires) return false;
            state = LinkState.Consumed;
            return true;
        }
    }
}

public sealed class LinkOptions
{
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromMinutes(10);
    /// <summary>Shorter than the 20 second request timeout editors use.</summary>
    public TimeSpan LongPoll { get; set; } = TimeSpan.FromSeconds(15);
    public int MaxPending { get; set; } = 1000;
}

/// <summary>
/// Device linking after the OAuth 2.0 Device Authorization Grant (RFC 8628): an editor starts a request,
/// a signed-in person approves it in a browser by its short code, and the editor's poll receives a device token.
/// Requests live in memory: they last minutes, and a restart only makes the editor start over.
/// </summary>
public sealed class LinkService(AccountService accounts, TimeProvider time, LinkOptions options)
{
    /// <summary>No vowels (no accidental words) and no look-alikes such as 0/O and 1/I.</summary>
    private const string Alphabet = "BCDFGHJKLMNPQRSTVWXZ";

    private readonly ConcurrentDictionary<string, LinkRequest> byDevice = new();
    private readonly ConcurrentDictionary<string, LinkRequest> byUser = new();

    public TimeSpan Lifetime => options.Lifetime;

    public LinkStart? Start(string? device, string? app, string address)
    {
        Sweep();
        if (byDevice.Count >= options.MaxPending) return null;
        var now = time.GetUtcNow();
        var deviceCode = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        while (true)
        {
            var userCode = NewUserCode();
            var request = new LinkRequest(AccountService.HashToken(deviceCode), userCode, AccountService.DeviceName(device),
                Clip(app, 64), Clip(address, 64), now, now + options.Lifetime);
            if (!byUser.TryAdd(userCode, request)) continue;
            byDevice[request.DeviceHash] = request;
            return new(deviceCode, Format(userCode), request.Expires);
        }
    }

    public LinkInfo? Describe(string? userCode) => Find(userCode)?.Info(time.GetUtcNow());

    public bool Decide(string? userCode, string user, bool allow) => Find(userCode)?.Decide(user, allow, time.GetUtcNow()) ?? false;

    /// <summary>Waits up to the long-poll time for a decision, then answers once.</summary>
    public async Task<PollResult> Poll(string? deviceCode, CancellationToken cancel)
    {
        if (deviceCode is not { Length: 64 } || !byDevice.TryGetValue(AccountService.HashToken(deviceCode), out var request))
            return new(PollStatus.Expired);
        if (request.State(time.GetUtcNow()) == LinkState.Pending)
        {
            var wait = Min(options.LongPoll, request.Expires - time.GetUtcNow());
            if (wait > TimeSpan.Zero)
            {
                try
                {
                    await request.Decided.WaitAsync(wait, time, cancel);
                }
                catch (TimeoutException)
                {
                }
            }
        }
        var now = time.GetUtcNow();
        switch (request.State(now))
        {
            case LinkState.Pending:
                return new(PollStatus.Pending);
            case LinkState.Denied:
                Forget(request);
                return new(PollStatus.Denied);
            case LinkState.Approved when request.TryConsume(now):
                Forget(request);
                return new(PollStatus.Approved, request.User, await accounts.IssueDeviceToken(request.User!, request.Device, cancel));
            default:
                Forget(request);
                return new(PollStatus.Expired);
        }
    }

    /// <summary>Accepts "wdjb-mjht", "WDJB MJHT" or "WDJBMJHT".</summary>
    public static string? NormalizeCode(string? code)
    {
        if (code is null) return null;
        var clean = new string(code.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        return clean.Length == 8 && clean.All(Alphabet.Contains) ? clean : null;
    }

    public static string Format(string userCode) => userCode[..4] + "-" + userCode[4..];

    private LinkRequest? Find(string? userCode) =>
        NormalizeCode(userCode) is { } code && byUser.TryGetValue(code, out var request) ? request : null;

    private void Forget(LinkRequest request)
    {
        byDevice.TryRemove(request.DeviceHash, out _);
        byUser.TryRemove(request.UserCode, out _);
    }

    private void Sweep()
    {
        var now = time.GetUtcNow();
        foreach (var (_, request) in byDevice)
            if (now >= request.Expires + options.Lifetime) Forget(request);
    }

    private static string NewUserCode() =>
        string.Create(8, 0, (span, _) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        });

    private static string Clip(string? text, int max)
    {
        var clean = new string((text ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length > max ? clean[..max] : clean;
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
