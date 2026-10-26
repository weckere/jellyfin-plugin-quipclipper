using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Quipclipper.Server;

/// <summary>
/// Connects this server to quipclipper by pairing (<c>quipclipper.pair/1</c>): asks for
/// access with the plugin's scopes, hands the settings page the code to compare with
/// quipclipper's banner, and keeps the token once a person allows it. Done server-side, so
/// the browser never talks to quipclipper and no CORS is needed. One request at a time.
/// </summary>
public sealed class PairingService
{
    private readonly IConfigStore _config;
    private readonly Func<QuipclipperClient> _client;
    private readonly Func<string> _serverName;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private Pending? _pending;

    /// <summary>
    /// Initializes a new instance of the <see cref="PairingService"/> class.
    /// </summary>
    /// <param name="config">The plugin's settings.</param>
    /// <param name="client">Makes a quipclipper client.</param>
    /// <param name="serverName">This Jellyfin server's name, shown in quipclipper's banner.</param>
    /// <param name="time">The clock.</param>
    public PairingService(IConfigStore config, Func<QuipclipperClient> client, Func<string> serverName, TimeProvider time)
    {
        _config = config;
        _client = client;
        _serverName = serverName;
        _time = time;
    }

    /// <summary>Asks quipclipper at <paramref name="serverUrl"/> for access.</summary>
    /// <param name="serverUrl">quipclipper's URL as this server reaches it.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The pairing as it stands (<c>pending</c> with the code).</returns>
    /// <exception cref="ArgumentException">The URL isn't an http(s) URL.</exception>
    /// <exception cref="QuipclipperException">quipclipper refused or couldn't be reached.</exception>
    public async Task<PairingState> StartAsync(string serverUrl, CancellationToken cancellationToken)
    {
        var server = QuipclipperClient.ParseServer(serverUrl)
                     ?? throw new ArgumentException("Enter quipclipper's URL, like http://quipclipper:8080.", nameof(serverUrl));
        var host = (_serverName() ?? string.Empty).Trim();
        host = host.Length > 100 ? host[..100] : host;
        var started = await _client().StartPairAsync(server, "Jellyfin", host, cancellationToken).ConfigureAwait(false);
        var pending = new Pending(server, started.Id, started.PollSecret, started.Code,
            _time.GetUtcNow().AddSeconds(Math.Max(started.ExpiresIn, 1)));
        lock (_lock)
        {
            _pending = pending;
        }

        return PairingState.Waiting(pending.Code, pending.ExpiresAt);
    }

    /// <summary>Checks on the request: once approved, keeps the token and the server's URL.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The pairing as it stands.</returns>
    /// <exception cref="QuipclipperException">quipclipper couldn't be reached.</exception>
    public async Task<PairingState> PollAsync(CancellationToken cancellationToken)
    {
        Pending? pending;
        lock (_lock)
        {
            pending = _pending;
        }

        if (pending is null)
        {
            return _config.Current.Connected ? PairingState.Of("connected") : PairingState.Of("idle");
        }

        var poll = await _client().PollPairAsync(pending.Server, pending.Id, pending.PollSecret, cancellationToken).ConfigureAwait(false);
        switch (poll.Status)
        {
            case "pending":
                return PairingState.Waiting(pending.Code, pending.ExpiresAt);
            case "approved" when !string.IsNullOrEmpty(poll.Token):
                var config = _config.Current;
                config.ServerUrl = pending.Server.AbsoluteUri.TrimEnd('/');
                config.Token = poll.Token!;
                config.ClientId = poll.ClientId ?? string.Empty;
                config.Scopes = poll.Scopes?.ToArray() ?? []; // none: an older quipclipper's full token
                config.LastError = string.Empty;
                Clear(pending);
                _config.Save(config);
                return PairingState.Of("connected");
            default:
                Clear(pending);
                return PairingState.Of(poll.Status is "denied" or "expired" ? poll.Status : "expired");
        }
    }

    /// <summary>Forgets a request still waiting (the settings page's Cancel). quipclipper lets it expire.</summary>
    public void Cancel()
    {
        lock (_lock)
        {
            _pending = null;
        }
    }

    /// <summary>Disconnects: revokes the token at quipclipper, and forgets it here either way.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Null when quipclipper revoked it (or knew it no more); else why it couldn't, to revoke it there by hand.</returns>
    public async Task<string?> DisconnectAsync(CancellationToken cancellationToken)
    {
        var config = _config.Current;
        string? problem = null;
        if (config.Connected && QuipclipperClient.ParseServer(config.ServerUrl) is { } server)
        {
            try
            {
                await _client().DisconnectAsync(server, config.Token, cancellationToken).ConfigureAwait(false);
            }
            catch (QuipclipperException ex)
            {
                problem = ex.Message + ". Revoke \"Jellyfin\" under quipclipper's Settings, Connected apps.";
            }
        }

        config.Token = string.Empty;
        config.ClientId = string.Empty;
        config.Scopes = [];
        config.LastError = string.Empty;
        _config.Save(config);
        return problem;
    }

    private void Clear(Pending pending)
    {
        lock (_lock)
        {
            if (ReferenceEquals(_pending, pending))
            {
                _pending = null;
            }
        }
    }

    private sealed record Pending(Uri Server, string Id, string PollSecret, string Code, DateTimeOffset ExpiresAt);
}

/// <summary>How pairing stands, for the settings page.</summary>
/// <param name="Status"><c>idle</c>, <c>pending</c>, <c>connected</c>, <c>denied</c> or <c>expired</c>.</param>
/// <param name="Code">The code to compare with quipclipper's banner, while pending.</param>
/// <param name="ExpiresAt">When a pending request expires.</param>
public sealed record PairingState(string Status, string? Code, DateTimeOffset? ExpiresAt)
{
    /// <summary>A request waiting for a person.</summary>
    /// <param name="code">Its code.</param>
    /// <param name="expiresAt">When it expires.</param>
    /// <returns>The state.</returns>
    public static PairingState Waiting(string code, DateTimeOffset expiresAt) => new("pending", code, expiresAt);

    /// <summary>A state with no code.</summary>
    /// <param name="status">The status.</param>
    /// <returns>The state.</returns>
    public static PairingState Of(string status) => new(status, null, null);
}
