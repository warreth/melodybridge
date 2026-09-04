using AngleSharp.Dom;
using Bunit;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Server.Components.Shared;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// TrackTable and PlaylistActionsHeader component tests: sorting
/// (click, direction, aria state, stability), row action callbacks,
/// badge states for every download status and the queue overlay, the
/// optional File column, and the header's button set per run state.
/// Everything renders the real components with in-memory entities.
/// </summary>
[TestFixture]
[Category("UI")]
public class TrackTableTests
{
    private Bunit.TestContext _ctx = null!;

    public static List<TrackEntity> SampleTracks() => new()
    {
        new() { Title = "Charlie", Artist = "C Artist", Position = 2, DownloadStatus = "downloaded", Bitrate = 320, SampleRateHz = 44100, MediaType = "mp3", FileSizeBytes = 9_000_000, DurationMs = 200_000, CurrentPath = "/music/c.mp3" },
        new() { Title = "Alpha", Artist = "A Artist", Position = 0, DownloadStatus = "failed", DownloadError = "No match found through any plugin", FileSizeBytes = 0, DurationMs = 0 },
        new() { Title = "Bravo", Artist = "B Artist", Position = 1, DownloadStatus = "queued", FileSizeBytes = 2_000_000, DurationMs = 100_000 },
        new() { Title = "Delta", Artist = "D Artist", Position = 3, DownloadStatus = null, Warning = "low match confidence" },
    };

    [SetUp]
    public void Setup() => _ctx = new Bunit.TestContext();

    [TearDown]
    public void TearDown() => _ctx.Dispose();

    private IRenderedComponent<TrackTable> Render(
        IReadOnlyList<TrackEntity>? tracks = null,
        bool busy = false,
        bool fileColumn = false,
        Func<TrackEntity, string?>? queueLabel = null,
        Action<TrackEntity>? onRetry = null,
        Action<TrackEntity>? onRemove = null)
        => _ctx.Render<TrackTable>(pb =>
        {
            pb.Add(t => t.Tracks, tracks ?? SampleTracks());
            pb.Add(t => t.Busy, busy);
            pb.Add(t => t.ShowFilenameColumn, fileColumn);
            if (queueLabel is not null) pb.Add(t => t.QueueLabel, queueLabel);
            if (onRetry is not null) pb.Add(t => t.OnRetry, t => { onRetry(t); return Task.CompletedTask; });
            if (onRemove is not null) pb.Add(t => t.OnRemove, t => { onRemove(t); return Task.CompletedTask; });
        });

    private IElement ThFor(IRenderedComponent<TrackTable> cut, string label)
        => cut.FindAll("th").Single(th => th.TextContent
            .Replace("▴", "").Replace("▾", "").Trim() == label);

    private string[] TitleCells(IRenderedComponent<TrackTable> cut)
        => cut.FindAll(".cell-title strong").Select(e => e.TextContent).ToArray();

    private IElement SortButton(IRenderedComponent<TrackTable> cut, string label)
        => cut.FindAll(".th-sort").Single(b => b.TextContent
            .Replace("▴", "").Replace("▾", "").Trim() == label);

    // ── Sorting ─────────────────────────────────────────────────────

    [Test]
    public void Default_Order_Follows_Position()
    {
        var cut = Render();
        Assert.That(TitleCells(cut), Is.EqualTo(new[] { "Alpha", "Bravo", "Charlie", "Delta" }));
    }

    [Test]
    public void Sort_By_Title_Clicks_Ascending_Then_Descending()
    {
        var cut = Render();
        SortButton(cut, "Title").Click();
        Assert.That(TitleCells(cut), Is.EqualTo(new[] { "Alpha", "Bravo", "Charlie", "Delta" }));

        SortButton(cut, "Title").Click();
        Assert.That(TitleCells(cut), Is.EqualTo(new[] { "Delta", "Charlie", "Bravo", "Alpha" }));
        Assert.That(cut.Instance.SortKey, Is.EqualTo("Title"));
        Assert.That(cut.Instance.SortDescending, Is.True);
    }

    [Test]
    public void Sort_By_Duration_Nulls_First_Ascending()
    {
        // Alpha and Delta carry no duration; the tiebreak keeps their
        // playlist order, then Bravo (100s), then Charlie (200s).
        var cut = Render();
        SortButton(cut, "Duration").Click();
        Assert.That(TitleCells(cut), Is.EqualTo(new[] { "Alpha", "Delta", "Bravo", "Charlie" }));
    }

    [Test]
    public void Sort_By_Status_Groups_Failed_First()
    {
        var cut = Render();
        SortButton(cut, "Status").Click();
        // failed -> queued -> pending(null) -> downloaded
        Assert.That(TitleCells(cut), Is.EqualTo(new[] { "Alpha", "Bravo", "Delta", "Charlie" }));
    }

    [Test]
    public void Sort_Equal_Keys_Keep_Position_Tiebreak_Descending()
    {
        var tracks = new List<TrackEntity>
        {
            new() { Title = "same", Artist = "X", Position = 0 },
            new() { Title = "same", Artist = "Y", Position = 1 },
            new() { Title = "same", Artist = "Z", Position = 2 },
        };
        var cut = Render(tracks);
        SortButton(cut, "Title").Click();  // asc
        SortButton(cut, "Title").Click();  // desc: primary flips, tiebreak stays 0,1,2
        Assert.That(TitleCells(cut), Is.EqualTo(new[] { "same", "same", "same" }));
        var artists = cut.FindAll(".cell-artist").Select(e => e.TextContent).ToArray();
        Assert.That(artists, Is.EqualTo(new[] { "X", "Y", "Z" }));
    }

    [Test]
    public void NonSortable_Headers_Have_No_Button()
    {
        var cut = Render();
        var sortable = cut.FindAll(".th-sort").Select(b => b.TextContent.Replace("▴", "").Replace("▾", "").Trim()).ToList();
        Assert.That(sortable, Is.EqualTo(new[] { "#", "Title", "Artist", "Duration", "Size", "Status" }));
        Assert.That(cut.Markup, Does.Not.Contain("Sort by bitrate"), "Bitrate, Rate and Format headers stay plain");
    }

    [Test]
    public void Aria_Sort_Reflects_Active_Column_And_Direction()
    {
        var cut = Render();
        Assert.That(ThFor(cut, "Title").GetAttribute("aria-sort"), Is.EqualTo("none"));

        SortButton(cut, "Title").Click();
        Assert.That(ThFor(cut, "Title").GetAttribute("aria-sort"), Is.EqualTo("ascending"));

        SortButton(cut, "Title").Click();
        Assert.That(ThFor(cut, "Title").GetAttribute("aria-sort"), Is.EqualTo("descending"));

        Assert.That(ThFor(cut, "Artist").GetAttribute("aria-sort"), Is.EqualTo("none"));
    }

    // ── Row actions ────────────────────────────────────────────────

    [Test]
    public void Download_Button_Shown_For_Retriable_States_And_Fires_OnRetry()
    {
        TrackEntity? retryPayload = null;
        var tracks = SampleTracks();
        var cut = Render(tracks, onRetry: t => retryPayload = t);

        var row = cut.FindAll("tbody tr").Single(tr => tr.TextContent.Contains("Alpha"));
        var download = row.QuerySelector("button[title='Download this track now']");
        Assert.That(download, Is.Not.Null, "failed rows offer a retry");
        download!.Click();
        Assert.That(ReferenceEquals(retryPayload, tracks[1]), Is.True,
            "the callback receives the exact entity instance");
    }

    [Test]
    public void Remove_Button_Shown_When_Status_Set_And_Fires_OnRemove()
    {
        TrackEntity? removePayload = null;
        var tracks = SampleTracks();
        var cut = Render(tracks, onRemove: t => removePayload = t);

        var row = cut.FindAll("tbody tr").Single(tr => tr.TextContent.Contains("Charlie"));
        row.QuerySelector("button[title='Remove from playlist (deletes the file)']")!.Click();
        Assert.That(ReferenceEquals(removePayload, tracks[0]), Is.True);

        var pendingRow = cut.FindAll("tbody tr").Single(tr => tr.TextContent.Contains("Delta"));
        Assert.That(pendingRow.QuerySelector("button[title='Remove from playlist (deletes the file)']"),
            Is.Null, "never-downloaded tracks have nothing to remove");
    }

    [Test]
    public void Busy_Disables_Row_Buttons()
    {
        var cut = Render(busy: true);
        var buttons = cut.FindAll("tbody button");
        Assert.That(buttons, Is.Not.Empty);
        Assert.That(buttons.All(b => b.HasAttribute("disabled")), Is.True);
    }

    // ── Badge states ───────────────────────────────────────────────

    [Test]
    public void Status_Pills_Map_Every_State()
    {
        var cut = Render();
        var row = cut.Find("tbody tr");

        var pill = row.QuerySelector("span.pill")!;
        Assert.That(pill.ClassList, Does.Contain("err"));
        Assert.That(pill.TextContent, Is.EqualTo("failed"));
        Assert.That(pill.GetAttribute("title"), Is.EqualTo("No match found through any plugin"));

        var bang = row.QuerySelector("span.pill.pill-tight")!;
        Assert.That(bang.ClassList, Does.Contain("err"), "the failed row carries the error bang pill");
        Assert.That(bang.TextContent.Trim(), Is.EqualTo("!"));
    }

    [Test]
    public void Warning_Renders_Bang_Pill_Beside_Status()
    {
        var cut = Render();
        var row = cut.FindAll("tbody tr").Single(tr => tr.TextContent.Contains("Delta"));
        var bang = row.QuerySelector("span.pill.warn")!;
        Assert.That(bang.GetAttribute("title"), Is.EqualTo("low match confidence"));
    }

    [Test]
    public void QueueLabel_Downloading_Shows_Pulsing_Warn_Pill()
    {
        var cut = Render(queueLabel: t => t.Title == "Bravo" ? "downloading" : null);
        var row = cut.FindAll("tbody tr").Single(tr => tr.TextContent.Contains("Bravo"));
        var pill = row.QuerySelector("span.pill.warn")!;
        Assert.That(pill.ClassList, Does.Contain("pulse"));
        Assert.That(pill.TextContent, Is.EqualTo("downloading"));
    }

    [Test]
    public void QueueLabel_InQueue_Shows_Info_Pill()
    {
        var cut = Render(queueLabel: t => t.Title == "Bravo" ? "in queue · #2" : null);
        var row = cut.FindAll("tbody tr").Single(tr => tr.TextContent.Contains("Bravo"));
        Assert.That(row.QuerySelector("span.pill.info")!.TextContent, Is.EqualTo("in queue · #2"));
    }

    [Test]
    public void Queued_And_Downloaded_States_Render_Correct_Pills()
    {
        var cut = Render();
        var queued = cut.FindAll("tbody tr").Single(tr => tr.TextContent.Contains("Bravo"));
        Assert.That(queued.QuerySelector("span.pill.neutral")!.TextContent, Is.EqualTo("queued"));

        var downloaded = cut.FindAll("tbody tr").Single(tr => tr.TextContent.Contains("Charlie"));
        Assert.That(downloaded.QuerySelector("span.pill.ok")!.TextContent, Is.EqualTo("downloaded"));
    }

    // ── File column and empty state ────────────────────────────────

    [Test]
    public void Filename_Column_Optional_And_Shows_Basename()
    {
        var with = Render(fileColumn: true);
        Assert.That(with.Markup, Does.Contain(">File<"));
        Assert.That(with.Markup, Does.Contain("c.mp3"), "the cell shows the basename only");

        var without = Render();
        Assert.That(without.Markup, Does.Not.Contain(">File<"));
    }

    [Test]
    public void Empty_Tracks_Show_The_Filter_Empty_Row()
    {
        var cut = Render(tracks: new List<TrackEntity>());
        var empty = cut.Find("td.empty-row");
        Assert.That(empty.TextContent, Is.EqualTo("No tracks match the filter."));
        Assert.That(empty.GetAttribute("colspan"), Is.EqualTo("10"));
    }

    [Test]
    public void Cells_Carry_The_Clip_Classes()
    {
        var cut = Render();
        Assert.That(cut.Find(".cell-title").ClassList, Does.Contain("cell-title"));
        Assert.That(cut.Find(".cell-artist").ClassList, Does.Contain("cell-artist"));
    }

    // ── PlaylistActionsHeader ──────────────────────────────────────

    private IRenderedComponent<PlaylistActionsHeader> RenderHeader(
        bool showRefresh = false,
        bool showDownloadGroup = false,
        Application.Services.DownloadRunState state = Application.Services.DownloadRunState.Finished,
        bool busy = false)
        => _ctx.Render<PlaylistActionsHeader>(pb =>
        {
            pb.Add(h => h.Eyebrow, "Spotify playlist");
            pb.Add(h => h.Name, "Test playlist");
            pb.Add(h => h.ShowRefresh, showRefresh);
            pb.Add(h => h.ShowDownloadGroup, showDownloadGroup);
            pb.Add(h => h.DownloadState, state);
            pb.Add(h => h.Busy, busy);
        });

    private static string ButtonLabels(IRenderedComponent<PlaylistActionsHeader> cut)
        => string.Join("|", cut.FindAll(".detail-actions button").Select(b => b.TextContent.Trim()));

    [Test]
    public void Header_Shows_Idle_Button_Set()
    {
        var cut = RenderHeader();
        Assert.That(ButtonLabels(cut), Is.EqualTo("Download missing|Export CSV|Delete playlist"));
    }

    [Test]
    public void Header_Refresh_Only_With_Live_Source()
    {
        var with = RenderHeader(showRefresh: true);
        Assert.That(ButtonLabels(with), Does.StartWith("Refresh|"));

        var without = RenderHeader();
        Assert.That(ButtonLabels(without), Does.Not.Contains("Refresh"));
    }

    [Test]
    public void Header_Active_Run_Shows_Pause_And_Cancel()
    {
        var cut = RenderHeader(showDownloadGroup: true,
            state: Application.Services.DownloadRunState.Running);
        Assert.That(ButtonLabels(cut), Is.EqualTo("Pause|Cancel|Export CSV|Delete playlist"));
    }

    [Test]
    public void Header_Paused_Run_Shows_Resume_And_Cancel()
    {
        var cut = RenderHeader(showDownloadGroup: true,
            state: Application.Services.DownloadRunState.Paused);
        Assert.That(ButtonLabels(cut), Is.EqualTo("Resume|Cancel|Export CSV|Delete playlist"));
    }

    [Test]
    public void Header_Busy_Disables_Sensitive_Buttons()
    {
        var cut = RenderHeader(showRefresh: true, busy: true);
        var disabled = cut.FindAll(".detail-actions button[disabled]")
            .Select(b => b.TextContent.Trim()).ToList();
        Assert.That(disabled, Is.EqualTo(new[] { "Fetching…", "Export CSV", "Delete playlist" }));
    }
}
