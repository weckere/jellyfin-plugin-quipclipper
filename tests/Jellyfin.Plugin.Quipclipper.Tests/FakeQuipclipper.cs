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

    public List<string[]> Queued { get; } = [];

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
                    return Json(HttpStatusCode.OK, new { status = "approved", token = Token, client_id = "cl1", scopes = new[] { "index.file", "transcripts.read" } });
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
