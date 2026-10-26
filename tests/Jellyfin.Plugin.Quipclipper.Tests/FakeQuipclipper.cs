using System.Net;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Quipclipper.Configuration;
using Jellyfin.Plugin.Quipclipper.Server;

namespace Jellyfin.Plugin.Quipclipper.Tests;

/// <summary>
/// quipclipper's pairing, re-index and disconnect routes, in memory, answering as the real
/// server does (quipclipper's web/backend/quipclipper_web/pairing.py and app.py).
/// </summary>
internal sealed class FakeQuipclipper : HttpMessageHandler
{
    public const string Token = "qcc_fake-token";

    public List<(string Method, string Path, Dictionary<string, string> Headers, string Body)> Requests { get; } = [];

    public Dictionary<string, (string Secret, string Status)> Pairs { get; } = [];

    /// <summary>What POST /api/index/file answers, in turn; 202 when the list runs out.</summary>
    public Queue<HttpStatusCode> IndexAnswers { get; } = new();

    /// <summary>When set, every request fails as if quipclipper were down.</summary>
    public bool Down { get; set; }

    public bool Revoked { get; set; }

    /// <summary>When set, answers as a quipclipper from before scoped tokens: a full token, no scopes.</summary>
    public bool NoScopes { get; set; }

    public List<string[]> Queued { get; } = [];

    /// <summary>The stored transcripts GET /api/items/transcript serves, by Jellyfin path.</summary>
    public Dictionary<string, FakeTranscript> Transcripts { get; } = [];

    /// <summary>Each transcript request's query, parsed.</summary>
    public List<Dictionary<string, string>> TranscriptQueries { get; } = [];

    public QuipclipperClient Client() => new(new HttpClient(this, disposeHandler: false), TimeSpan.FromSeconds(5));

    public void Approve(string id) => Pairs[id] = (Pairs[id].Secret, "approved");

    public void Deny(string id) => Pairs[id] = (Pairs[id].Secret, "denied");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var path = request.RequestUri!.AbsolutePath;
        Requests.Add((request.Method.Method, path, headers, body));
        if (Down)
        {
            throw new HttpRequestException("Connection refused");
        }

        var key = headers.GetValueOrDefault("X-API-Key");
        switch (request.Method.Method, path)
        {
            case ("POST", "/api/pair"):
                if (!headers.ContainsKey("X-Quipclipper"))
                {
                    return Json(HttpStatusCode.Forbidden, new { detail = "Missing x-quipclipper header (CSRF guard)." });
                }

                var id = "req" + Pairs.Count;
                Pairs[id] = ("secret-" + id, "pending");
                return Json(HttpStatusCode.OK, new { id, code = "7F3K", poll_secret = "secret-" + id, expires_in = 600 });
            case ("GET", _) when path.StartsWith("/api/pair/", StringComparison.Ordinal):
                var rid = path["/api/pair/".Length..];
                if (!Pairs.TryGetValue(rid, out var pair) || headers.GetValueOrDefault("X-Pair-Secret") != pair.Secret)
                {
                    return Json(HttpStatusCode.NotFound, new { detail = "No such pairing request." });
                }

                if (pair.Status == "approved")
                {
                    Pairs.Remove(rid);
                    return NoScopes
                        ? Json(HttpStatusCode.OK, new { status = "approved", token = Token, client_id = "cl1" })
                        : Json(HttpStatusCode.OK, new { status = "approved", token = Token, client_id = "cl1", scopes = new[] { "index.file", "transcripts.read" } });
                }

                return Json(HttpStatusCode.OK, new { status = pair.Status });
            case ("POST", "/api/index/file"):
                if (key != Token || Revoked)
                {
                    return Json(HttpStatusCode.Unauthorized, new { detail = "This client token isn't valid: the app was disconnected (revoked) here, or never paired. Pair it again." });
                }

                using (var doc = JsonDocument.Parse(body))
                {
                    if (doc.RootElement.GetProperty("source").GetString() != "jellyfin")
                    {
                        return Json(HttpStatusCode.BadRequest, new { detail = "Unknown source" });
                    }

                    var paths = doc.RootElement.GetProperty("paths").EnumerateArray().Select(p => p.GetString()!).ToArray();
                    var answer = IndexAnswers.Count > 0 ? IndexAnswers.Dequeue() : HttpStatusCode.Accepted;
                    if (answer == HttpStatusCode.Accepted)
                    {
                        Queued.Add(paths);
                    }

                    return Json(answer, new { detail = answer == HttpStatusCode.Accepted ? null : "Nothing to re-index.", queued = paths.Length });
                }

            case ("GET", "/api/items/transcript"):
                if (key != Token || Revoked)
                {
                    return Json(HttpStatusCode.Unauthorized, new { detail = "revoked" });
                }

                var q = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
                var query = q.AllKeys.Where(k => k is not null).ToDictionary(k => k!, k => q[k]!);
                TranscriptQueries.Add(query);
                if (query.GetValueOrDefault("source") != "jellyfin" || !Transcripts.TryGetValue(query["path"], out var t))
                {
                    return Json(HttpStatusCode.NotFound, new { detail = "Not a file" });
                }

                var lines = query.GetValueOrDefault("lines") == "all" ? t.All : t.Machine;
                if (query.GetValueOrDefault("fmt") == "srt")
                {
                    if (t.Stored is null || lines.Length == 0)
                    {
                        return Json(HttpStatusCode.NotFound, new { detail = "This file has no machine transcript: transcribe it first." });
                    }

                    var srt = string.Join("\n", lines.Select((x, i) => $"{i + 1}\n00:00:{i + 1:00},000 --> 00:00:{i + 2:00},000\n{x}\n"));
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(srt, Encoding.UTF8, "application/x-subrip") };
                }

                return Json(HttpStatusCode.OK, new
                {
                    path = query["path"],
                    stored = t.Stored is null ? null : new { ranges = new[] { new[] { 0.0, t.Stored.Value.Covered } }, covered = t.Stored.Value.Covered, lines = t.All.Length, models = t.Stored.Value.Models, language = t.Stored.Value.Language, updated = 1.0, engine = "whisper.cpp" },
                    library = t.Library,
                    writable = false,
                    sidecar_name = "x.en.whisper.srt",
                    lines = lines.Select((x, i) => new { start = i + 1.0, end = i + 2.0, text = x, source = "machine" }),
                });
            case ("DELETE", "/api/clients/self"):
                if (key != Token || Revoked)
                {
                    return Json(HttpStatusCode.Unauthorized, new { detail = "revoked" });
                }

                Revoked = true;
                return Json(HttpStatusCode.OK, new { removed = true });
            default:
                return Json(HttpStatusCode.NotFound, new { detail = "Not Found" });
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object body) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };
}

/// <summary>A stored transcript as the fake serves it: <see cref="Machine"/> are the lines no subtitle has.</summary>
internal sealed record FakeTranscript((double Covered, string[] Models, string Language)? Stored, string[] All, string[] Machine, string[] Library);

/// <summary>The plugin's settings, in memory.</summary>
internal sealed class MemoryConfig : IConfigStore
{
    public event EventHandler? Changed;

    public PluginConfiguration Current { get; private set; } = new();

    public int Saves { get; private set; }

    public void Save(PluginConfiguration configuration)
    {
        Current = configuration;
        Saves++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public static MemoryConfig Connected(params string[] libraryIds)
    {
        var c = new MemoryConfig();
        c.Current.ServerUrl = "http://quipclipper.test:8090";
        c.Current.Token = FakeQuipclipper.Token;
        c.Current.LibraryIds = libraryIds;
        return c;
    }
}

/// <summary>A clock the tests move by hand.</summary>
internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
