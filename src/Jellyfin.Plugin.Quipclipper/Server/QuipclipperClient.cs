using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Quipclipper.Server;

/// <summary>
/// The few quipclipper routes the plugin calls: pairing (<c>quipclipper.pair/1</c>), the
/// re-index queue, reading a stored transcript and disconnecting. Every call has a short timeout of its own, so a
/// quipclipper that hangs can't hold anything of Jellyfin's.
/// </summary>
public sealed class QuipclipperClient
{
    /// <summary>The scopes the plugin asks for: queueing re-indexes, and reading transcripts for the subtitle provider.</summary>
    public static readonly string[] Scopes = ["index.file", "transcripts.read"];

    /// <summary>The most paths quipclipper takes in one re-index request.</summary>
    public const int MaxPaths = 1000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    /// <summary>
    /// Initializes a new instance of the <see cref="QuipclipperClient"/> class.
    /// </summary>
    /// <param name="http">The HTTP client (its own timeout is not relied on).</param>
    /// <param name="timeout">How long one call may take; 10 s when not given.</param>
    public QuipclipperClient(HttpClient http, TimeSpan? timeout = null)
    {
        _http = http;
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
    }

    /// <summary>Asks for access: quipclipper shows the code in its banner until a person answers.</summary>
    /// <param name="server">quipclipper's URL.</param>
    /// <param name="name">Who is asking, e.g. "Jellyfin".</param>
    /// <param name="host">The Jellyfin server's name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The request's id, code and poll secret.</returns>
    /// <exception cref="QuipclipperException">quipclipper refused or couldn't be reached.</exception>
    public async Task<PairStart> StartPairAsync(Uri server, string name, string host, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Url(server, "api/pair"))
        {
            Content = JsonContent.Create(new { name, host, scopes = Scopes }, options: Json),
        };
        request.Headers.Add("X-Quipclipper", "1"); // no token yet: the CSRF header, like a browser
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureAsync(response, "asking for access", cancellationToken).ConfigureAwait(false);
        return await ReadAsync<PairStart>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Asks how a pairing request stands; an approval carries the token, once.</summary>
    /// <param name="server">quipclipper's URL.</param>
    /// <param name="id">The request's id.</param>
    /// <param name="pollSecret">The request's poll secret.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Its status; <c>gone</c> when quipclipper no longer knows it (restarted, or collected already).</returns>
    /// <exception cref="QuipclipperException">quipclipper couldn't be reached or answered an error.</exception>
    public async Task<PairPoll> PollPairAsync(Uri server, string id, string pollSecret, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url(server, "api/pair/" + Uri.EscapeDataString(id)));
        request.Headers.Add("X-Pair-Secret", pollSecret);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new PairPoll { Status = "gone" };
        }

        await EnsureAsync(response, "checking the pairing request", cancellationToken).ConfigureAwait(false);
        return await ReadAsync<PairPoll>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Queues files for re-indexing, in Jellyfin's own paths (quipclipper maps them with its Jellyfin path map).</summary>
    /// <param name="server">quipclipper's URL.</param>
    /// <param name="token">The client token.</param>
    /// <param name="paths">At most <see cref="MaxPaths"/> media file paths.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>How quipclipper answered: its HTTP status, or none when it couldn't be reached.</returns>
    public async Task<SendResult> QueueReindexAsync(Uri server, string token, IReadOnlyCollection<string> paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count > MaxPaths)
        {
            throw new ArgumentException($"At most {MaxPaths} paths at a time.", nameof(paths));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, Url(server, "api/index/file"))
        {
            Content = JsonContent.Create(new { paths, source = "jellyfin" }, options: Json),
        };
        request.Headers.Add("X-API-Key", token);
        try
        {
            using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new SendResult((int)response.StatusCode, Detail(body));
        }
        catch (QuipclipperException ex)
        {
            return new SendResult(null, ex.Message);
        }
    }

    /// <summary>A media file's stored machine transcript, in Jellyfin's own path (<c>source=jellyfin</c>).</summary>
    /// <param name="server">quipclipper's URL.</param>
    /// <param name="token">The client token.</param>
    /// <param name="mediaPath">The media file, as Jellyfin knows it.</param>
    /// <param name="lines"><c>machine</c> (the lines no subtitle has) or <c>all</c>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What quipclipper holds; null when it doesn't know the file (404).</returns>
    /// <exception cref="QuipclipperException">quipclipper couldn't be reached or answered another error.</exception>
    public async Task<TranscriptInfo?> GetTranscriptAsync(Uri server, string token, string mediaPath, string lines, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, TranscriptUrl(server, mediaPath, lines, "json"));
        request.Headers.Add("X-API-Key", token);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureAsync(response, "reading the transcript", cancellationToken).ConfigureAwait(false);
        return await ReadAsync<TranscriptInfo>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A media file's stored machine lines as SubRip, in Jellyfin's own path (<c>source=jellyfin</c>).</summary>
    /// <param name="server">quipclipper's URL.</param>
    /// <param name="token">The client token.</param>
    /// <param name="mediaPath">The media file, as Jellyfin knows it.</param>
    /// <param name="lines"><c>machine</c> (the lines no subtitle has) or <c>all</c>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The SRT text.</returns>
    /// <exception cref="QuipclipperException">quipclipper couldn't be reached, or has no such lines (404).</exception>
    public async Task<string> GetTranscriptSrtAsync(Uri server, string token, string mediaPath, string lines, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, TranscriptUrl(server, mediaPath, lines, "srt"));
        request.Headers.Add("X-API-Key", token);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureAsync(response, "reading the transcript", cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Revokes this server's own token (Disconnect).</summary>
    /// <param name="server">quipclipper's URL.</param>
    /// <param name="token">The client token.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True when quipclipper removed it; false when it no longer knew it (401: revoked already).</returns>
    /// <exception cref="QuipclipperException">quipclipper couldn't be reached or answered another error.</exception>
    public async Task<bool> DisconnectAsync(Uri server, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, Url(server, "api/clients/self"));
        request.Headers.Add("X-API-Key", token);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return false;
        }

        await EnsureAsync(response, "disconnecting", cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>quipclipper's base URL, checked: http or https, absolute, with no query.</summary>
    /// <param name="value">What was typed.</param>
    /// <returns>The URL, or null when it isn't one.</returns>
    public static Uri? ParseServer(string? value)
    {
        var text = (value ?? string.Empty).Trim().TrimEnd('/');
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            return null;
        }

        return uri;
    }

    private static Uri Url(Uri server, string path) => new(server.AbsoluteUri.TrimEnd('/') + "/" + path);

    private static Uri TranscriptUrl(Uri server, string mediaPath, string lines, string fmt) => Url(
        server,
        "api/items/transcript?source=jellyfin&lines=" + Uri.EscapeDataString(lines) + "&fmt=" + fmt
        + "&path=" + Uri.EscapeDataString(mediaPath));

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new QuipclipperException($"quipclipper didn't answer within {_timeout.TotalSeconds:0} s");
        }
        catch (HttpRequestException ex)
        {
            throw new QuipclipperException("quipclipper is unreachable: " + ex.Message, ex);
        }
    }

    private static async Task EnsureAsync(HttpResponseMessage response, string doing, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var detail = Detail(body);
        throw new QuipclipperException(
            $"quipclipper answered {(int)response.StatusCode} {doing}" + (detail is null ? string.Empty : ": " + detail),
            (int)response.StatusCode);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken).ConfigureAwait(false)
                   ?? throw new QuipclipperException("quipclipper sent an empty reply");
        }
        catch (JsonException ex)
        {
            throw new QuipclipperException("quipclipper sent a reply that isn't what the plugin expects", ex);
        }
    }

    /// <summary>The <c>detail</c> of a FastAPI error body, if it has a plain one.</summary>
    private static string? Detail(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>A new pairing request.</summary>
public sealed class PairStart
{
    /// <summary>Gets or sets the request's id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the 4-character code quipclipper's banner shows.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Gets or sets the secret the request is polled with.</summary>
    [JsonPropertyName("poll_secret")]
    public string PollSecret { get; set; } = string.Empty;

    /// <summary>Gets or sets how many seconds the request waits for a person.</summary>
    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }
}

/// <summary>How a pairing request stands.</summary>
public sealed class PairPoll
{
    /// <summary>Gets or sets <c>pending</c>, <c>approved</c>, <c>denied</c>, <c>expired</c>, or <c>gone</c> (quipclipper no longer knows it).</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Gets or sets the client token, on the one poll that collects an approval.</summary>
    public string? Token { get; set; }

    /// <summary>Gets or sets the client's id, with the token.</summary>
    [JsonPropertyName("client_id")]
    public string? ClientId { get; set; }

    /// <summary>Gets or sets the scopes granted, with the token (null from a quipclipper without scopes: a full token).</summary>
    public IReadOnlyList<string>? Scopes { get; set; }
}

/// <summary>A file's stored machine transcript (<c>GET /api/items/transcript</c>, json).</summary>
public sealed class TranscriptInfo
{
    /// <summary>Gets or sets what was transcribed; null when nothing was.</summary>
    public StoredTranscript? Stored { get; set; }

    /// <summary>Gets or sets the names of the copies already beside the file (<c>….whisper.srt</c>).</summary>
    public IReadOnlyList<string> Library { get; set; } = [];

    /// <summary>Gets or sets the lines asked for.</summary>
    public IReadOnlyList<TranscriptLine> Lines { get; set; } = [];
}

/// <summary>What a stored transcript holds.</summary>
public sealed class StoredTranscript
{
    /// <summary>Gets or sets the seconds transcribed.</summary>
    public double Covered { get; set; }

    /// <summary>Gets or sets how many lines it has in all.</summary>
    public int Lines { get; set; }

    /// <summary>Gets or sets the whisper models it was made with.</summary>
    public IReadOnlyList<string> Models { get; set; } = [];

    /// <summary>Gets or sets its language (two letters, e.g. <c>en</c>).</summary>
    public string Language { get; set; } = "en";
}

/// <summary>One machine line.</summary>
public sealed class TranscriptLine
{
    /// <summary>Gets or sets where it starts, in seconds.</summary>
    public double Start { get; set; }

    /// <summary>Gets or sets where it ends, in seconds.</summary>
    public double End { get; set; }

    /// <summary>Gets or sets what was heard.</summary>
    public string Text { get; set; } = string.Empty;
}

/// <summary>How quipclipper answered a re-index request.</summary>
/// <param name="Status">The HTTP status, or null when quipclipper couldn't be reached.</param>
/// <param name="Detail">quipclipper's <c>detail</c>, or why it couldn't be reached.</param>
public sealed record SendResult(int? Status, string? Detail);

/// <summary>quipclipper couldn't be reached or answered an error.</summary>
public sealed class QuipclipperException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="QuipclipperException"/> class.
    /// </summary>
    public QuipclipperException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="QuipclipperException"/> class.
    /// </summary>
    /// <param name="message">What went wrong.</param>
    public QuipclipperException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="QuipclipperException"/> class.
    /// </summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="status">quipclipper's HTTP status.</param>
    public QuipclipperException(string message, int status)
        : base(message)
    {
        Status = status;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="QuipclipperException"/> class.
    /// </summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The cause.</param>
    public QuipclipperException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Gets quipclipper's HTTP status, when it answered.</summary>
    public int? Status { get; }
}
