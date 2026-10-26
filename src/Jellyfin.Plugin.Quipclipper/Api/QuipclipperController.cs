using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Quipclipper.Server;
using Jellyfin.Plugin.Quipclipper.Subtitles;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Quipclipper.Api;

/// <summary>
/// What the plugin's settings page calls: connect (pair), check on it, disconnect, find
/// quipclipper on the LAN, and how the features are doing. Administrators only. The page never talks to quipclipper
/// itself; Jellyfin does, so no CORS is involved.
/// </summary>
[ApiController]
[Route("Quipclipper")]
[Authorize(Policy = "RequiresElevation")]
[Produces(MediaTypeNames.Application.Json)]
public class QuipclipperController : ControllerBase
{
    private readonly PairingService _pairing;
    private readonly SubtitleWatch _watch;
    private readonly IConfigStore _config;
    private readonly MediaBrowser.Controller.Library.ILibraryManager _libraryManager;
    private readonly Discovery _discovery;
    private readonly ILogger<QuipclipperController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="QuipclipperController"/> class.
    /// </summary>
    /// <param name="pairing">Pairing.</param>
    /// <param name="watch">The subtitle watcher.</param>
    /// <param name="config">The plugin's settings.</param>
    /// <param name="libraryManager">Jellyfin's library.</param>
    /// <param name="discovery">Finds quipclipper on the LAN.</param>
    /// <param name="logger">The logger.</param>
    public QuipclipperController(PairingService pairing, SubtitleWatch watch, IConfigStore config, MediaBrowser.Controller.Library.ILibraryManager libraryManager, Discovery discovery, ILogger<QuipclipperController> logger)
    {
        _pairing = pairing;
        _watch = watch;
        _config = config;
        _libraryManager = libraryManager;
        _discovery = discovery;
        _logger = logger;
    }

    /// <summary>Whether this server is connected to quipclipper, and how the subtitle watcher is doing.</summary>
    /// <returns>The status.</returns>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> Status()
    {
        var config = _config.Current;
        var last = _watch.LastSend;
        return new
        {
            connected = config.Connected,
            serverUrl = config.ServerUrl,
            clientId = config.ClientId,
            scopes = config.Scopes,
            lastError = config.LastError,
            watching = config.Connected && config.WatchSubtitles,
            seeding = _watch.Seeding,
            seeded = _watch.State.Seeded.Count,
            known = _watch.State.Known,
            pending = _watch.State.PendingCount,
            dropped = _watch.State.Dropped,
            lastSend = last is { } l ? new { at = l.At, sent = l.Outcome.Sent, refused = l.Outcome.Refused, problem = l.Outcome.Detail } : null,
            features = Feature.All.Select(f => new { name = f.Name, on = f.IsOn, reason = f.Reason }).ToList(),
        };
    }

    /// <summary>Looks for quipclipper servers that advertise themselves on the LAN (<c>QC_ADVERTISE=1</c>), for the page to offer their URLs.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The servers found, and why none could be looked for.</returns>
    [HttpGet("Discover")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> Discover(CancellationToken cancellationToken)
    {
        try
        {
            var servers = await _discovery.FindAsync(Discovery.Window, cancellationToken).ConfigureAwait(false);
            return new { servers = servers.Select(s => new { name = s.Name, id = s.Id, urls = s.Urls }).ToList(), problem = (string?)null };
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            return new { servers = Array.Empty<object>(), problem = "Couldn't look on the network: " + ex.Message };
        }
    }

    /// <summary>Asks quipclipper for access; the reply's code shows in quipclipper's banner too.</summary>
    /// <param name="request">quipclipper's URL.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The pairing state.</returns>
    [HttpPost("Pair")]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<PairingState>> Pair([FromBody] PairRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return await _pairing.StartAsync(request.ServerUrl ?? string.Empty, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { detail = ex.Message });
        }
        catch (QuipclipperException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { detail = ex.Message });
        }
    }

    /// <summary>Checks on the pairing request; once allowed in quipclipper, the token is kept and this answers <c>connected</c>.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The pairing state.</returns>
    [HttpGet("Pair")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<PairingState>> PollPair(CancellationToken cancellationToken)
    {
        try
        {
            return await _pairing.PollAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (QuipclipperException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { detail = ex.Message });
        }
    }

    /// <summary>Stops waiting for a pairing request.</summary>
    /// <returns>Nothing.</returns>
    [HttpDelete("Pair")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult CancelPair()
    {
        _pairing.Cancel();
        return NoContent();
    }

    /// <summary>Disconnects: revokes the token at quipclipper and forgets it here.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether quipclipper confirmed it, and why not.</returns>
    [HttpPost("Disconnect")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> Disconnect(CancellationToken cancellationToken)
    {
        var problem = await _pairing.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        return new { disconnected = true, problem };
    }

    /// <summary>The libraries the page offers to watch.</summary>
    /// <returns>Each library's id and name.</returns>
    [HttpGet("Libraries")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> Libraries()
        => Feature.SubtitleReindex.Run<IReadOnlyList<WatchedLibrary>>(_logger, () => SubtitleWatcher.Libraries(_libraryManager), [])
            .Select(l => new { id = l.Id, name = l.Name, locations = l.Locations }).ToList();
}

/// <summary>Body of <c>POST /Quipclipper/Pair</c>.</summary>
public sealed class PairRequest
{
    /// <summary>Gets or sets quipclipper's URL as this server reaches it.</summary>
    public string? ServerUrl { get; set; }
}
