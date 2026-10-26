using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
            return Directory.EnumerateFiles(dir, "*." + SidecarToken + ".*").Any(f => own.IsMatch(Path.GetFileName(f)));
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
            if (info is null || stored is null || stored.Lines == 0 || info.Library.Count > 0
                || !LanguageMatches(stored.Language, request))
            {
                return [];
            }

            return Results(request, path, stored, info.Lines.Count);
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
        var (lines, language, path) = ParseId(id)
            ?? throw new ArgumentException("Not a quipclipper machine transcript id.", nameof(id));
        if (HasMachineSidecar(path))
        {
            throw new InvalidOperationException("A machine transcript is beside this file already.");
        }

        var config = _config.Current;
        var server = config.Connected ? QuipclipperClient.ParseServer(config.ServerUrl) : null;
        if (server is null)
        {
            throw new InvalidOperationException("The quipclipper plugin isn't connected to quipclipper.");
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

    /// <summary>A result's id: which lines, the transcript's language and the media path (base64url, so it is safe in Jellyfin's URL).</summary>
    /// <param name="lines"><c>machine</c> or <c>all</c>.</param>
    /// <param name="language">Two letters.</param>
    /// <param name="mediaPath">The media file.</param>
    /// <returns>The id.</returns>
    public static string MakeId(string lines, string language, string mediaPath) =>
        (lines == "all" ? "a" : "m") + "." + language + "." + Convert.ToBase64String(Encoding.UTF8.GetBytes(mediaPath))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Reads an id <see cref="MakeId"/> made.</summary>
    /// <param name="id">The id.</param>
    /// <returns>Its lines, language and path; null when it isn't one.</returns>
    public static (string Lines, string Language, string Path)? ParseId(string? id)
    {
        var m = IdPattern().Match(id ?? string.Empty);
        if (!m.Success)
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
                : (m.Groups[1].Value == "a" ? "all" : "machine", m.Groups[2].Value, path);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static bool LanguageMatches(string language, SubtitleSearchRequest request) =>
        string.Equals(request.TwoLetterISOLanguageName, language, StringComparison.OrdinalIgnoreCase);

    private static List<RemoteSubtitleInfo> Results(SubtitleSearchRequest request, string path, StoredTranscript stored, int machineLines)
    {
        var lang = Regex.Replace(stored.Language.ToLowerInvariant(), "[^a-z]", string.Empty);
        lang = lang.Length is >= 2 and <= 3 ? lang : "en";
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
            Id = MakeId(lines, lang, path),
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

    [GeneratedRegex("^([ma])\\.([a-z]{2,3})\\.([A-Za-z0-9_-]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
}
