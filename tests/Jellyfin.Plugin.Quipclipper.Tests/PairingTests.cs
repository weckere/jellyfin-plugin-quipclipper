using System.Text.Json;
using Jellyfin.Plugin.Quipclipper.Server;
using Jellyfin.Plugin.Quipclipper.Subtitles;

namespace Jellyfin.Plugin.Quipclipper.Tests;

public class PairingTests
{
    private readonly FakeQuipclipper _qc = new();
    private readonly MemoryConfig _config = new();
    private readonly PairingService _pairing;

    public PairingTests()
    {
        _pairing = new PairingService(_config, _qc.Client, () => "wizfin", new ManualClock());
    }

    [Fact]
    public async Task Connect_asks_with_the_scopes_and_keeps_the_token_once_allowed()
    {
        var started = await _pairing.StartAsync("http://quipclipper.test:8090/", default);
        Assert.Equal(("pending", "7F3K"), (started.Status, started.Code));
        var ask = _qc.Requests.Single();
        Assert.Equal(("POST", "/api/pair"), (ask.Method, ask.Path));
        Assert.Equal("1", ask.Headers["X-Quipclipper"]);
        using (var body = JsonDocument.Parse(ask.Body))
        {
            Assert.Equal("Jellyfin", body.RootElement.GetProperty("name").GetString());
            Assert.Equal("wizfin", body.RootElement.GetProperty("host").GetString());
            Assert.Equal(["index.file", "transcripts.read"], body.RootElement.GetProperty("scopes").EnumerateArray().Select(e => e.GetString()));
        }

        Assert.Equal("pending", (await _pairing.PollAsync(default)).Status);
        Assert.Equal("secret-req0", _qc.Requests[^1].Headers["X-Pair-Secret"]);
        Assert.False(_config.Current.Connected);

        _qc.Approve("req0");
        Assert.Equal("connected", (await _pairing.PollAsync(default)).Status);
        Assert.True(_config.Current.Connected);
        Assert.Equal("http://quipclipper.test:8090", _config.Current.ServerUrl);
        Assert.Equal(FakeQuipclipper.Token, _config.Current.Token);
        Assert.Equal("cl1", _config.Current.ClientId);
        Assert.Equal(["index.file", "transcripts.read"], _config.Current.Scopes);
        Assert.Equal("connected", (await _pairing.PollAsync(default)).Status);  // no request left to poll
    }

    [Fact]
    public async Task A_denied_or_forgotten_request_ends_it()
    {
        await _pairing.StartAsync("http://quipclipper.test:8090", default);
        _qc.Deny("req0");
        Assert.Equal("denied", (await _pairing.PollAsync(default)).Status);
        Assert.Equal("idle", (await _pairing.PollAsync(default)).Status);

        await _pairing.StartAsync("http://quipclipper.test:8090", default);
        _qc.Pairs.Clear();  // quipclipper restarted: it no longer knows the request
        Assert.Equal("expired", (await _pairing.PollAsync(default)).Status);
        Assert.False(_config.Current.Connected);
    }

    [Fact]
    public async Task Two_open_settings_pages_polling_the_same_request_both_see_it_connected()
    {
        await _pairing.StartAsync("http://quipclipper.test:8090", default);
        _qc.Approve("req0");
        _qc.PollGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = _pairing.PollAsync(default);
        await _qc.PollEntered.Task;               // the first page's poll is at quipclipper
        var second = _pairing.PollAsync(default); // the second page's waits for it
        Assert.False(second.IsCompleted);
        _qc.PollGate.SetResult();

        Assert.Equal("connected", (await first).Status);
        Assert.Equal("connected", (await second).Status); // not "expired": the token was handed out once, to the first
        Assert.Single(_qc.Requests, r => r.Method == "GET");
    }

    [Fact]
    public async Task An_allow_given_while_no_page_was_open_is_collected_by_the_next_page()
    {
        var clock = new ManualClock();
        using var pairing = new PairingService(_config, _qc.Client, () => "wizfin", clock);
        await pairing.StartAsync("http://quipclipper.test:8090", default);
        // The page is closed; someone allows it in quipclipper; later the page opens again and asks.
        _qc.Approve("req0");
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal("connected", (await pairing.PollAsync(default)).Status);
        Assert.True(_config.Current.Connected);
    }

    [Fact]
    public async Task A_request_long_past_its_expiry_is_dropped_without_asking()
    {
        var clock = new ManualClock();
        using var pairing = new PairingService(_config, _qc.Client, () => "wizfin", clock);
        await pairing.StartAsync("http://quipclipper.test:8090", default);
        clock.Advance(TimeSpan.FromSeconds(600) + PairingService.Linger);
        Assert.Equal("expired", (await pairing.PollAsync(default)).Status);
        Assert.Equal("idle", (await pairing.PollAsync(default)).Status);
        Assert.DoesNotContain(_qc.Requests, r => r.Method == "GET");
    }

    [Theory]
    [InlineData("")]
    [InlineData("quipclipper:8090")]
    [InlineData("ftp://quipclipper")]
    [InlineData("http://quipclipper/?x=1")]
    public async Task A_url_that_isnt_one_is_refused_before_anything_is_sent(string url)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _pairing.StartAsync(url, default));
        Assert.Empty(_qc.Requests);
    }

    [Fact]
    public async Task An_unreachable_quipclipper_says_so()
    {
        _qc.Down = true;
        var ex = await Assert.ThrowsAsync<QuipclipperException>(() => _pairing.StartAsync("http://quipclipper.test:8090", default));
        Assert.Contains("unreachable", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_quipclipper_that_hangs_times_out()
    {
        var client = new QuipclipperClient(new HttpClient(new Hangs()), TimeSpan.FromMilliseconds(50));
        var ex = await Assert.ThrowsAsync<QuipclipperException>(() => client.StartPairAsync(new Uri("http://q.test"), "Jellyfin", "h", default));
        Assert.Contains("didn't answer", ex.Message, StringComparison.Ordinal);
        Assert.Null((await client.QueueReindexAsync(new Uri("http://q.test"), "t", ["/a.mkv"], default)).Status);
    }

    [Fact]
    public async Task Disconnect_revokes_the_token_and_forgets_it()
    {
        await Connect();
        Assert.Null(await _pairing.DisconnectAsync(default));
        Assert.Equal(("DELETE", "/api/clients/self", FakeQuipclipper.Token), (_qc.Requests[^1].Method, _qc.Requests[^1].Path, _qc.Requests[^1].Headers["X-API-Key"]));
        Assert.True(_qc.Revoked);
        Assert.False(_config.Current.Connected);
        Assert.Empty(_config.Current.Token);
    }

    [Fact]
    public async Task Disconnect_forgets_the_token_even_when_quipclipper_is_down()
    {
        await Connect();
        _qc.Down = true;
        var problem = await _pairing.DisconnectAsync(default);
        Assert.Contains("Connected apps", problem, StringComparison.Ordinal);
        Assert.False(_config.Current.Connected);
    }

    [Fact]
    public async Task Disconnect_after_quipclipper_revoked_it_is_fine()
    {
        await Connect();
        _qc.Revoked = true;  // revoked under quipclipper's Connected apps: 401
        Assert.Null(await _pairing.DisconnectAsync(default));
        Assert.False(_config.Current.Connected);
    }

    [Fact]
    public async Task A_full_token_from_a_quipclipper_without_scopes_is_handed_back()
    {
        _qc.NoScopes = true;
        await _pairing.StartAsync("http://quipclipper.test:8090", default);
        _qc.Approve("req0");
        var state = await _pairing.PollAsync(default);
        Assert.Equal("too_old", state.Status);
        Assert.Contains("Update quipclipper", state.Detail, StringComparison.Ordinal);
        Assert.True(_qc.Revoked);                         // DELETE /api/clients/self with it
        Assert.False(_config.Current.Connected);          // and never kept
        Assert.Equal(0, _config.Saves);
    }

    private async Task Connect()
    {
        await _pairing.StartAsync("http://quipclipper.test:8090", default);
        _qc.Approve("req0");
        await _pairing.PollAsync(default);
    }

    private sealed class Hangs : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }
}

public class ReindexSenderTests
{
    private readonly FakeQuipclipper _qc = new();
    private static readonly Uri Server = new("http://quipclipper.test:8090");

    private static SubtitleState Waiting(int n)
    {
        var s = new SubtitleState();
        for (var i = 0; i < n; i++)
        {
            s.Observe($"/shows/{i:D5}.mkv", new Dictionary<string, long> { ["x.srt"] = 1 });
        }

        return s;
    }

    [Fact]
    public async Task Files_go_in_batches_of_at_most_a_thousand()
    {
        var s = Waiting(2500);
        var outcome = await ReindexSender.SendAsync(s, _qc.Client(), Server, FakeQuipclipper.Token, default);
        Assert.Equal((SendKind.Done, 2500, 0), (outcome.Kind, outcome.Sent, outcome.Refused));
        Assert.Equal([1000, 1000, 500], _qc.Queued.Select(q => q.Length));
        Assert.Equal("/shows/00000.mkv", _qc.Queued[0][0]);
        Assert.Equal(0, s.PendingCount);
        Assert.All(_qc.Requests, r => Assert.Equal(FakeQuipclipper.Token, r.Headers["X-API-Key"]));
        Assert.All(_qc.Requests, r => Assert.Contains("\"source\":\"jellyfin\"", r.Body, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Files_quipclipper_cant_reindex_are_dropped_not_retried()
    {
        var s = Waiting(3);
        _qc.IndexAnswers.Enqueue(System.Net.HttpStatusCode.UnprocessableEntity);
        var outcome = await ReindexSender.SendAsync(s, _qc.Client(), Server, FakeQuipclipper.Token, default);
        Assert.Equal((SendKind.Done, 0, 3), (outcome.Kind, outcome.Sent, outcome.Refused));
        Assert.Equal(0, s.PendingCount);
    }

    [Fact]
    public async Task A_quipclipper_that_is_down_or_failing_keeps_them_for_later()
    {
        var s = Waiting(1500);
        _qc.IndexAnswers.Enqueue(System.Net.HttpStatusCode.Accepted);
        _qc.IndexAnswers.Enqueue(System.Net.HttpStatusCode.BadGateway);
        var outcome = await ReindexSender.SendAsync(s, _qc.Client(), Server, FakeQuipclipper.Token, default);
        Assert.Equal((SendKind.Retry, 1000), (outcome.Kind, outcome.Sent));
        Assert.Equal(500, s.PendingCount);

        _qc.Down = true;
        outcome = await ReindexSender.SendAsync(s, _qc.Client(), Server, FakeQuipclipper.Token, default);
        Assert.Equal(SendKind.Retry, outcome.Kind);
        Assert.Contains("unreachable", outcome.Detail, StringComparison.Ordinal);
        Assert.Equal(500, s.PendingCount);
    }

    [Fact]
    public async Task A_revoked_token_stops_sending_and_keeps_the_files()
    {
        var s = Waiting(2);
        _qc.Revoked = true;
        var outcome = await ReindexSender.SendAsync(s, _qc.Client(), Server, FakeQuipclipper.Token, default);
        Assert.Equal(SendKind.Stopped, outcome.Kind);
        Assert.Contains("401", outcome.Detail, StringComparison.Ordinal);
        Assert.Equal(2, s.PendingCount);
    }
}
