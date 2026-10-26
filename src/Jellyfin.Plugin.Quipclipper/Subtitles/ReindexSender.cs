using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Quipclipper.Server;

namespace Jellyfin.Plugin.Quipclipper.Subtitles;

/// <summary>Sends the waiting files to quipclipper's re-index queue, at most 1,000 per request.</summary>
public static class ReindexSender
{
    /// <summary>Sends everything waiting, until done or quipclipper can't take more.</summary>
    /// <param name="state">The watcher's state (sent files leave its waiting list).</param>
    /// <param name="client">The quipclipper client.</param>
    /// <param name="server">quipclipper's URL.</param>
    /// <param name="token">The client token.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What happened.</returns>
    public static async Task<SendOutcome> SendAsync(SubtitleState state, QuipclipperClient client, Uri server, string token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(client);
        int sent = 0, refused = 0;
        while (true)
        {
            var batch = state.NextBatch(QuipclipperClient.MaxPaths);
            if (batch.Count == 0)
            {
                return new SendOutcome(SendKind.Done, sent, refused, null);
            }

            var result = await client.QueueReindexAsync(server, token, batch, cancellationToken).ConfigureAwait(false);
            switch (result.Status)
            {
                case 200 or 202:
                    state.Sent(batch);
                    sent += batch.Count;
                    break;
                case 400 or 422:
                    // Nothing in it can be queued (not under quipclipper's roots, no media file):
                    // sending it again wouldn't change that.
                    state.Sent(batch);
                    refused += batch.Count;
                    break;
                case 401 or 403:
                    // Revoked, or a token that may not queue re-indexes: only pairing again helps.
                    // What waits is kept for then.
                    return new SendOutcome(SendKind.Stopped, sent, refused,
                        $"quipclipper refused the token ({result.Status}): {result.Detail ?? "pair again"}");
                default:
                    return new SendOutcome(SendKind.Retry, sent, refused,
                        result.Status is { } code ? $"quipclipper answered {code}: {result.Detail}" : result.Detail);
            }
        }
    }
}

/// <summary>How a send ended.</summary>
public enum SendKind
{
    /// <summary>Nothing waits any more.</summary>
    Done,

    /// <summary>quipclipper couldn't take them now; try again later.</summary>
    Retry,

    /// <summary>quipclipper refused the token; nothing more until this server pairs again.</summary>
    Stopped,
}

/// <summary>What a send did.</summary>
/// <param name="Kind">How it ended.</param>
/// <param name="Sent">Files quipclipper took.</param>
/// <param name="Refused">Files quipclipper said it can't re-index (dropped).</param>
/// <param name="Detail">Why it stopped early.</param>
public sealed record SendOutcome(SendKind Kind, int Sent, int Refused, string? Detail);
