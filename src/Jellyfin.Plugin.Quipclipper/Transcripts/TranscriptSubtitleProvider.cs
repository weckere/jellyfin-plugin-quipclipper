using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Quipclipper.Server;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Quipclipper.Transcripts;

/// <summary>
/// Offers quipclipper's stored machine transcript of a movie or episode in Jellyfin's
/// "Search subtitles" dialog (quipclipper M22, Phase 3). Manual searches only: Jellyfin's
/// automatic downloads (the "Download missing subtitles" task, scan-time downloads) get
/// nothing, it never starts a transcription, and an item that already has a machine
/// sidecar gets nothing. What Jellyfin saves is named <c>&lt;stem&gt;.en.whisper.srt</c>:
/// Jellyfin puts the response's language into the file name as given, and quipclipper
/// reads that name as machine lines, never as the file's subtitles.
/// </summary>
public sealed partial class TranscriptSubtitleProvider : ISubtitleProvider
{
    /// <summary>The provider's name in Jellyfin (its subtitle dialog and the libraries' "Subtitle downloaders").</summary>
    public const string ProviderName = "quipclipper (machine transcript)";

    /// <summary>The token quipclipper's machine sidecars carry: <c>&lt;stem&gt;.&lt;lang&gt;.whisper.srt</c>.</summary>
    public const string SidecarToken = "whisper";

    /// <summary>How long one call to quipclipper may take: a file whose subtitles aren't cached yet has them extracted first.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static readonly EnumerationOptions AnyCase = new() { MatchCasing = MatchCasing.CaseInsensitive, IgnoreInaccessible = true };
    private static readonly VideoContentType[] MediaTypes = [VideoContentType.Movie, VideoContentType.Episode];
    private readonly IConfigStore _config;
    private readonly Func<QuipclipperClient> _client;
    private readonly ILogger<TranscriptSubtitleProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TranscriptSubtitleProvider"/> class.
    /// </summary>
    /// <param name="config">The plugin's settings (quipclipper's URL and token).</param>
    /// <param name="client">Makes a quipclipper client.</param>
    /// <param name="logger">The logger.</param>
    public TranscriptSubtitleProvider(IConfigStore config, Func<QuipclipperClient> client, ILogger<TranscriptSubtitleProvider> logger)
    {
        _config = config;
        _client = client;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => ProviderName;

    /// <inheritdoc />
    public IEnumerable<VideoContentType> SupportedMediaTypes => MediaTypes;

    /// <summary>The name Jellyfin saves a response under: <c>&lt;stem&gt;.&lt;language&gt;.srt</c>, as its SubtitleManager builds it (12.0–12.3).</summary>
    /// <param name="mediaPath">The media file.</param>
    /// <param name="language">The response's language.</param>
    /// <returns>The file name.</returns>
    public static string SavedName(string mediaPath, string language) =>
        Path.GetFileNameWithoutExtension(mediaPath) + "." + language.ToLowerInvariant() + ".srt";

    /// <summary>Whether a machine sidecar is beside the media file already (<c>&lt;stem&gt;[.lang].whisper.srt/.vtt</c>, as quipclipper finds them, or Jellyfin's <c>.whisper.0.srt</c> for a second copy).</summary>
    /// <param name="mediaPath">The media file.</param>
    /// <returns>True when one is.</returns>
    public static bool HasMachineSidecar(string mediaPath)
    {
        var dir = Path.GetDirectoryName(mediaPath);
        if (string.IsNullOrEmpty(dir))
        {
            return false;
        }

        var own = new Regex(
            "^" + Regex.Escape(Path.GetFileNameWithoutExtension(mediaPath)) + @"(\.[a-z]{1,3})?\." + SidecarToken + @"(\.\d+)?\.(srt|vtt)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        try
        {
            return Directory.EnumerateFiles(dir, "*." + SidecarToken + ".*", AnyCase).Any(f => own.IsMatch(Path.GetFileName(f)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteSubtitleInfo>> Search(SubtitleSearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var config = _config.Current;
        var path = request.MediaPath;
        // Manual only (D-037): the automatic downloads take the first result of the first
        // provider that answers, which would put machine text in as if it were subtitles.
        if (request.IsAutomated || !config.Connected || string.IsNullOrEmpty(path) || HasMachineSidecar(path))
        {
            return [];
        }

        try
        {
            var server = QuipclipperClient.ParseServer(config.ServerUrl);
            if (server is null)
            {
                return [];
            }

            var info = await _client().GetTranscriptAsync(server, config.Token, path, "machine", cancellationToken).ConfigureAwait(false);
            var stored = info?.Stored;
            var lang = Language(stored?.Language);
            if (info is null || stored is null || stored.Lines == 0 || info.Library.Count > 0
                || lang is null || !string.Equals(request.TwoLetterISOLanguageName, lang, StringComparison.OrdinalIgnoreCase))
            {
                return [];
            }

            return Results(request, path, stored, lang, info.Lines.Count, config.Token);
        }
        catch (QuipclipperException ex)
        {
            Log.SearchFailed(_logger, path, ex.Message);
            return [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Log.SearchCrashed(_logger, ex, path);
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<SubtitleResponse> GetSubtitles(string id, CancellationToken cancellationToken)
    {
        // Jellyfin shows the message of what this throws: a download that can't be served
        // fails rather than saving an empty or misnamed file.
        var config = _config.Current;
        var server = config.Connected ? QuipclipperClient.ParseServer(config.ServerUrl) : null;
        if (server is null)
        {
            throw new InvalidOperationException("The quipclipper plugin isn't connected to quipclipper.");
        }

        // Signed with the token: an id made up by hand can't name another file's path.
        var (lines, language, path) = ParseId(id, config.Token)
            ?? throw new ArgumentException("Not a quipclipper machine transcript id from this server.", nameof(id));
        if (HasMachineSidecar(path))
        {
            throw new InvalidOperationException("A machine transcript is beside this file already.");
        }

        var srt = await _client().GetTranscriptSrtAsync(server, config.Token, path, lines, cancellationToken).ConfigureAwait(false);
        Log.Fetched(_logger, lines, path);
        return new SubtitleResponse
        {
            Language = language + "." + SidecarToken,
            Format = "srt",
            IsForced = false,
            IsHearingImpaired = false,
            Stream = new MemoryStream(Encoding.UTF8.GetBytes(srt)),
        };
    }

    /// <summary>A result's id: which lines, the transcript's language and the media path (base64url, so it is safe in Jellyfin's URL), signed with <paramref name="key"/>.</summary>
    /// <param name="lines"><c>machine</c> or <c>all</c>.</param>
    /// <param name="language">Two or three letters.</param>
    /// <param name="mediaPath">The media file.</param>
    /// <param name="key">The signing key (the client token).</param>
    /// <returns>The id.</returns>
    public static string MakeId(string lines, string language, string mediaPath, string key)
    {
        var head = (lines == "all" ? "a" : "m") + "." + language + "." + Base64Url(Encoding.UTF8.GetBytes(mediaPath));
        return head + "." + Signature(head, key);
    }

    /// <summary>Reads an id <see cref="MakeId"/> made with the same key.</summary>
    /// <param name="id">The id.</param>
    /// <param name="key">The signing key (the client token).</param>
    /// <returns>Its lines, language and path; null when it isn't one, or wasn't signed with this key.</returns>
    public static (string Lines, string Language, string Path)? ParseId(string? id, string key)
    {
        var m = IdPattern().Match(id ?? string.Empty);
        if (!m.Success || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(m.Groups[4].Value), Encoding.ASCII.GetBytes(Signature(m.Groups[1].Value, key))))
        {
            return null;
        }

        var b64 = m.Groups[3].Value.Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + ((4 - (b64.Length % 4)) % 4), '=');
        try
        {
            var path = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            return path.Length == 0 || path.Contains('\0', StringComparison.Ordinal)
                ? null
                : (m.Groups[2].Value == "a" ? "all" : "machine", m.Groups[1].Value.Split('.')[1], path);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>A transcript's language as two or three letters (<c>en-US</c> → <c>en</c>); null when it names none (<c>auto</c>, missing).</summary>
    /// <param name="language">quipclipper's <c>language</c>.</param>
    /// <returns>The letters, or null.</returns>
    public static string? Language(string? language)
    {
        var head = (language ?? string.Empty).Split('-', '_')[0].ToLowerInvariant();
        return head.Length is >= 2 and <= 3 && head.All(c => c is >= 'a' and <= 'z') ? head : null;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Signature(string head, string key) =>
        Base64Url(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(head))[..16]);

    private static List<RemoteSubtitleInfo> Results(SubtitleSearchRequest request, string path, StoredTranscript stored, string lang, int machineLines, string key)
    {
        var heard = Heard(stored.Covered, request.RuntimeTicks);
        var models = stored.Models.Count > 0 ? string.Join(", ", stored.Models) : "whisper";
        var comment = "Machine transcript (" + models + "), not subtitles: saved as "
                      + SavedName(path, lang + "." + SidecarToken) + ".";
        var results = new List<RemoteSubtitleInfo>();
        // The lines no subtitle has: what quipclipper's Copy to library writes. With no
        // subtitles they are the whole transcript, so only that one is offered.
        if (machineLines > 0 && machineLines < stored.Lines)
        {
            results.Add(Result("machine", "Machine lines only", machineLines));
        }

        results.Add(Result("all", "Full machine transcript", stored.Lines));
        return results;

        RemoteSubtitleInfo Result(string lines, string title, int count) => new()
        {
            Id = MakeId(lines, lang, path, key),
            ProviderName = ProviderName,
            Name = string.Create(CultureInfo.InvariantCulture, $"{title} ({models}, {heard}, {count} lines)"),
            ThreeLetterISOLanguageName = request.Language,
            Format = "srt",
            Author = "quipclipper",
            Comment = comment,
            IsHashMatch = true,
            Forced = false,
            HearingImpaired = false,
        };
    }

    /// <summary>"41:20 of 44:02 heard", or "41:20 heard" when Jellyfin doesn't know the runtime.</summary>
    private static string Heard(double covered, long? runtimeTicks)
    {
        var done = Clock(covered);
        return runtimeTicks is > 0 ? $"{done} of {Clock(runtimeTicks.Value / (double)TimeSpan.TicksPerSecond)} heard" : done + " heard";
    }

    private static string Clock(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(seconds)));
        return t.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{t.Minutes}:{t.Seconds:00}");
    }

    // Group 1: the signed head; 2: lines; 3: the path, base64url; 4: the signature.
    [GeneratedRegex("^(([ma])\\.[a-z]{2,3}\\.([A-Za-z0-9_-]+))\\.([A-Za-z0-9_-]{22})$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
}
