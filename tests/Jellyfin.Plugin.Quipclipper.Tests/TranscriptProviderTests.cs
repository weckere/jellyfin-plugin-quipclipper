using Jellyfin.Plugin.Quipclipper.Server;
using Jellyfin.Plugin.Quipclipper.Transcripts;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Quipclipper.Tests;

/// <summary>The machine transcript subtitle provider (Phase 3), against the fake quipclipper.</summary>
public sealed class TranscriptProviderTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeQuipclipper _qc = new();
    private readonly MemoryConfig _config = MemoryConfig.Connected();
    private readonly TranscriptSubtitleProvider _provider;
    private readonly string _episode;

    public TranscriptProviderTests()
    {
        _provider = new TranscriptSubtitleProvider(_config, _qc.Client, NullLogger<TranscriptSubtitleProvider>.Instance);
        _episode = _dir.File("Darkplace/Season 01/Darkplace - S01E06.mkv", string.Empty);
        _dir.File("Darkplace/Season 01/Darkplace - S01E06.en.srt", "1\n00:00:01,000 --> 00:00:02,000\nhi\n");
        _qc.Transcripts[_episode] = new FakeTranscript((1325.5, ["small.en"], "en"), ["hi", "Garth Marenghi.", "Dagless."], ["Garth Marenghi.", "Dagless."], []);
    }

    public void Dispose() => _dir.Dispose();

    private SubtitleSearchRequest Manual(string? path = null) => new()
    {
        ContentType = VideoContentType.Episode,
        MediaPath = path ?? _episode,
        Language = "eng",
        TwoLetterISOLanguageName = "en",
        RuntimeTicks = TimeSpan.FromMinutes(24).Ticks + TimeSpan.FromSeconds(2).Ticks,
        IsAutomated = false,
    };

    [Fact]
    public void It_is_named_and_covers_movies_and_episodes()
    {
        Assert.Equal("quipclipper (machine transcript)", _provider.Name);
        Assert.Equal([VideoContentType.Movie, VideoContentType.Episode], _provider.SupportedMediaTypes);
    }

    [Fact]
    public async Task A_manual_search_offers_the_machine_lines_and_the_full_transcript()
    {
        var found = (await _provider.Search(Manual(), default)).ToList();
        Assert.Equal(
            ["Machine lines only (small.en, 22:05 of 24:02 heard, 2 lines)", "Full machine transcript (small.en, 22:05 of 24:02 heard, 3 lines)"],
            found.Select(r => r.Name));
        Assert.All(found, r =>
        {
            Assert.Equal("quipclipper (machine transcript)", r.ProviderName);
            Assert.Equal("eng", r.ThreeLetterISOLanguageName);
            Assert.Equal("srt", r.Format);
            Assert.Equal("Machine transcript (small.en), not subtitles: saved as Darkplace - S01E06.en.whisper.srt.", r.Comment);
        });
        var q = Assert.Single(_qc.TranscriptQueries);
        Assert.Equal(("jellyfin", "machine", "json", _episode), (q["source"], q["lines"], q["fmt"], q["path"]));
        Assert.Equal(FakeQuipclipper.Token, _qc.Requests.Single().Headers["X-API-Key"]);
    }

    [Fact]
    public async Task Picking_one_hands_Jellyfin_srt_under_the_machine_name()
    {
        var found = (await _provider.Search(Manual(), default)).ToList();
        var response = await _provider.GetSubtitles(found[0].Id, default);
        Assert.Equal(("en.whisper", "srt", false, false), (response.Language, response.Format, response.IsForced, response.IsHearingImpaired));
        using var reader = new StreamReader(response.Stream);
        var srt = await reader.ReadToEndAsync();
        Assert.Contains("Garth Marenghi.", srt, StringComparison.Ordinal);
        Assert.DoesNotContain("hi\n", srt, StringComparison.Ordinal);
        Assert.Equal(("machine", "srt"), (_qc.TranscriptQueries[^1]["lines"], _qc.TranscriptQueries[^1]["fmt"]));
        // Jellyfin 12.x saves <stem>.<Language lowercased>.srt beside the media (SubtitleManager.TrySaveSubtitle).
        Assert.Equal("Darkplace - S01E06.en.whisper.srt", TranscriptSubtitleProvider.SavedName(_episode, response.Language));

        var full = await _provider.GetSubtitles(found[1].Id, default);
        Assert.Equal("all", _qc.TranscriptQueries[^1]["lines"]);
        Assert.Equal("en.whisper", full.Language);
    }

    [Fact]
    public async Task An_automated_search_gets_nothing_and_asks_nothing()
    {
        var request = Manual();
        request.IsAutomated = true;
        Assert.Empty(await _provider.Search(request, default));
        Assert.Empty(_qc.Requests);
    }

    [Fact]
    public async Task An_item_with_no_stored_transcript_gets_nothing()
    {
        var other = _dir.File("Darkplace/Season 01/Darkplace - S01E05.mkv", string.Empty);
        Assert.Empty(await _provider.Search(Manual(other), default)); // quipclipper: 404
        _qc.Transcripts[other] = new FakeTranscript(null, [], [], []);
        Assert.Empty(await _provider.Search(Manual(other), default)); // known, never transcribed
        Assert.All(_qc.TranscriptQueries, q => Assert.Equal("json", q["fmt"])); // nothing else is asked: no transcription starts
        Assert.All(_qc.Requests, r => Assert.Equal("GET", r.Method));
    }

    [Theory]
    [InlineData("Darkplace - S01E06.en.whisper.srt")]
    [InlineData("Darkplace - S01E06.whisper.vtt")]
    [InlineData("Darkplace - S01E06.en.whisper.0.srt")]
    public async Task An_item_with_a_machine_sidecar_gets_nothing(string sidecar)
    {
        _dir.File("Darkplace/Season 01/" + sidecar, "1\n00:00:01,000 --> 00:00:02,000\nx\n");
        Assert.Empty(await _provider.Search(Manual(), default));
        Assert.Empty(_qc.Requests);
    }

    [Fact]
    public async Task Another_files_sidecar_doesnt_count()
    {
        // Darkplace - S01E06.Part2.en.whisper.srt is Part2's, as quipclipper reads it.
        _dir.File("Darkplace/Season 01/Darkplace - S01E06.Part2.en.whisper.srt", "x");
        Assert.Equal(2, (await _provider.Search(Manual(), default)).Count());
    }

    [Fact]
    public async Task A_copy_quipclipper_sees_in_the_library_means_nothing_either()
    {
        _qc.Transcripts[_episode] = _qc.Transcripts[_episode] with { Library = ["Darkplace - S01E06.en.whisper.srt"] };
        Assert.Empty(await _provider.Search(Manual(), default));
    }

    [Fact]
    public async Task With_no_subtitles_only_the_full_transcript_is_offered()
    {
        _qc.Transcripts[_episode] = new FakeTranscript((90, ["small.en", "medium.en"], "en"), ["a", "b"], ["a", "b"], []);
        var request = Manual();
        request.RuntimeTicks = null;
        var only = Assert.Single(await _provider.Search(request, default));
        Assert.Equal("Full machine transcript (small.en, medium.en, 1:30 heard, 2 lines)", only.Name);
    }

    [Fact]
    public async Task Another_language_gets_nothing()
    {
        var request = Manual();
        (request.Language, request.TwoLetterISOLanguageName) = ("fre", "fr");
        Assert.Empty(await _provider.Search(request, default));
    }

    [Fact]
    public async Task Not_connected_or_quipclipper_down_gets_nothing_and_throws_nothing()
    {
        var unpaired = new TranscriptSubtitleProvider(new MemoryConfig(), _qc.Client, NullLogger<TranscriptSubtitleProvider>.Instance);
        Assert.Empty(await unpaired.Search(Manual(), default));
        Assert.Empty(_qc.Requests);

        _qc.Down = true;
        Assert.Empty(await _provider.Search(Manual(), default));
        _qc.Down = false;
        _qc.Revoked = true;
        Assert.Empty(await _provider.Search(Manual(), default));
    }

    [Fact]
    public async Task A_download_that_cant_be_served_fails_rather_than_saving_something_wrong()
    {
        var id = (await _provider.Search(Manual(), default)).First().Id;
        await Assert.ThrowsAsync<ArgumentException>(() => _provider.GetSubtitles("m.en.!!", default));
        await Assert.ThrowsAsync<ArgumentException>(() => _provider.GetSubtitles("x.en.YQ", default));

        _dir.File("Darkplace/Season 01/Darkplace - S01E06.en.whisper.srt", "x"); // saved meanwhile
        await Assert.ThrowsAsync<InvalidOperationException>(() => _provider.GetSubtitles(id, default));
        File.Delete(Path.Combine(_dir.Path, "Darkplace/Season 01/Darkplace - S01E06.en.whisper.srt"));

        _qc.Transcripts[_episode] = new FakeTranscript(null, [], [], []); // deleted meanwhile
        var gone = await Assert.ThrowsAsync<QuipclipperException>(() => _provider.GetSubtitles(id, default));
        Assert.Equal(404, gone.Status);
    }

    [Theory]
    [InlineData("/shows/Darkplace/Season 01/Darkplace - S01E06.mkv")]
    [InlineData("/movies/Amélie (2001)/Amélie ?+&=#%.mkv")]
    public void An_id_round_trips_and_is_safe_in_a_url(string path)
    {
        var id = TranscriptSubtitleProvider.MakeId("all", "en", path);
        Assert.Matches("^[A-Za-z0-9._-]+$", id);
        Assert.Equal(("all", "en", path), TranscriptSubtitleProvider.ParseId(id));
        Assert.Equal(("machine", "en", path), TranscriptSubtitleProvider.ParseId(TranscriptSubtitleProvider.MakeId("machine", "en", path)));
        Assert.Null(TranscriptSubtitleProvider.ParseId(null));
        Assert.Null(TranscriptSubtitleProvider.ParseId("m.english.YQ"));
    }
}
