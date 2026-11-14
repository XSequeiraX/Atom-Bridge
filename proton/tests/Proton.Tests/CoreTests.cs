using Proton.Core;
using Xunit;

namespace Proton.Tests;

public class TimecodeTests
{
    [Theory]
    [InlineData("10:00:01:12", 36001.48, 25)]
    [InlineData("00:00:00:00", 0, 25)]
    [InlineData("10:01:05;00", 36065, 25)]
    public void ParsesAndConverts(string text, double seconds, double fps)
    {
        Assert.True(Timecode.TryParse(text, out var tc));
        Assert.Equal(seconds, tc.ToSeconds(fps), 2);
    }

    [Theory]
    [InlineData("")]
    [InlineData("10:00:01")]
    [InlineData("10:61:00:00")]
    [InlineData("a:b:c:d")]
    public void RejectsBadText(string text) => Assert.False(Timecode.TryParse(text, out _));

    [Fact]
    public void RoundTrips() => Assert.Equal("10:00:01:12", Timecode.FromSeconds(36001.48, 25).ToString());
}

public class TakesLoaderTests
{
    private static string Fx(string n) => Path.Combine(AppContext.BaseDirectory, "Fixtures", n);

    [Fact]
    public void ReadsTheAtomJson()
    {
        var e = TakesLoader.FromFile(Fx("atom-takes.json"));
        Assert.Equal(25, e.Fps);
        Assert.Equal(2, e.Takes.Count);
        var t1 = e.Takes[0];
        Assert.Equal(1, t1.Number);
        Assert.Equal("good", t1.Verdict);
        Assert.Equal(new DateTime(2026, 11, 9, 10, 0, 0), t1.Start);
        Assert.False(t1.Open);
        Assert.Equal(new[] { "BMPCC", "R5", "F6" }, t1.Cameras.Select(c => c.Source));
        Assert.Equal("A001_C001.braw", t1.Cameras[0].Clip);
        Assert.Equal("10:00:01:00", t1.Cameras[1].TcStart);
        Assert.True(e.Takes[1].Open);
    }

    [Fact]
    public void ReadsTheAtomCsvToTheSameTakes()
    {
        var j = TakesLoader.FromFile(Fx("atom-takes.json"));
        var c = TakesLoader.FromFile(Fx("atom-takes.csv"));
        Assert.Equal(j.Takes.Select(t => t.Id), c.Takes.Select(t => t.Id));
        Assert.Equal(j.Takes.Select(t => t.Cameras.Count), c.Takes.Select(t => t.Cameras.Count));
        Assert.Equal("good", c.Takes[0].Verdict);
        Assert.Equal("MVI_0001.MP4", c.Takes[0].Cameras.First(x => x.Source == "R5").Clip);
    }

    [Fact]
    public void TheCsvCarriesTheAtomFps()
    {
        Assert.Equal(25, TakesLoader.FromFile(Fx("atom-takes.csv")).Fps);
        const string head = "day,session,take,camera,alias,start,stop,tc_start,tc_stop,clip,fault,verdict,notes";
        const string row = "2026-11-09,S,1,A,,2026-11-09 10:00:00,,10:00:00:15,,,,,";
        Assert.Equal(30, TakesLoader.FromCsv(head + ",fps\n" + row + ",30\n").Fps);
        Assert.Equal(25, TakesLoader.FromCsv(head + "\n" + row + "\n").Fps);                  // um CSV de antes da coluna
    }

    [Fact]
    public void CsvHandlesQuotesAndCommas()
    {
        var rows = TakesLoader.ParseCsv("a,b\n\"x,y\",\"say \"\"hi\"\"\"\n");
        Assert.Equal(new[] { "x,y", "say \"hi\"" }, rows[1]);
    }

    [Fact]
    public void RefusesAnUnknownFormat() =>
        Assert.Throws<FormatException>(() => TakesLoader.FromJson("{\"format\":\"other/1\",\"takes\":[]}"));
}

public class ReaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("proton-").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void ReadsBwfTimecodeDurationAndIxml()
    {
        var ixml = "<BWFXML><SCENE>3A</SCENE><TAKE>7</TAKE><SPEED><TIMECODE_RATE>25/1</TIMECODE_RATE></SPEED></BWFXML>";
        var path = Path.Combine(_dir, "ZOOM0001.WAV");
        File.WriteAllBytes(path, Builders.Wav(48000, 2, 48000UL * 36001, ixml));
        var f = new WavReader().Read(path);
        Assert.Equal(MediaKind.Audio, f.Kind);
        Assert.Equal(36001, f.StartSeconds!.Value, 3);
        Assert.Equal(2, f.Duration!.Value.TotalSeconds, 2);
        Assert.Equal("3A", f.Scene);
        Assert.Equal(7, f.TakeNo);
        Assert.Equal(25, f.Fps);
        Assert.Equal(new DateTime(2026, 11, 9, 10, 0, 1), f.RecordedAt);
        Assert.Null(f.Note);
    }

    [Fact]
    public void BwfWithoutTimecodeSaysSo()
    {
        var path = Path.Combine(_dir, "MIC.wav");
        File.WriteAllBytes(path, Builders.Wav(48000, 1, 0));
        var f = new WavReader().Read(path);
        Assert.Null(f.StartSeconds);
        Assert.Contains("no timecode", f.Note);
    }

    [Fact]
    public void ReadsMovTimecodeTrackAndDuration()
    {
        var path = Path.Combine(_dir, "MVI_0001.MOV");
        File.WriteAllBytes(path, Builders.Mov(900025, 25, 60, 3_000_000_000));
        var f = new IsoBmffReader().Read(path);
        Assert.Equal(36001, f.StartSeconds!.Value, 3);
        Assert.Equal(25, f.Fps);
        Assert.Equal(60, f.Duration!.Value.TotalSeconds, 2);
        Assert.NotNull(f.RecordedAt);
    }

    [Fact]
    public void DropFrameCounterConverts()
    {
        // 1 hora de drop-frame a 29,97 sao 107892 imagens e valem 01:00:00 de timecode
        Assert.Equal(3600, IsoBmffReader.FramesToSeconds(107892, 30, true), 3);
    }

    [Fact]
    public void ScannerSkipsJunkAndTagsTheCamera()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "DCIM"));
        File.WriteAllBytes(Path.Combine(_dir, "DCIM", "A.MOV"), Builders.Mov(900025, 25, 10, 3_000_000_000));
        File.WriteAllBytes(Path.Combine(_dir, "DCIM", "B.braw"), new byte[10]);
        File.WriteAllBytes(Path.Combine(_dir, "DCIM", "._A.MOV"), new byte[10]);
        File.WriteAllText(Path.Combine(_dir, "DCIM", "notes.txt"), "x");
        var files = MediaScanner.Scan(_dir, "A-cam");
        Assert.Equal(new[] { "A.MOV", "B.braw" }, files.Select(f => f.Name));
        Assert.All(files, f => Assert.Equal("A-cam", f.Camera));
        Assert.Contains("BRAW", files[1].Note);
    }
}

public class MatcherTests
{
    private static readonly DateTime T0 = new(2026, 11, 9, 10, 0, 0);

    private static Take MakeTake(int n, DateTime start, int seconds, params TakeCamera[] cams) =>
        new("2026-11-09", "Dia 1", n, start, start.AddSeconds(seconds), false, "good", cams[0].TcStart, cams, Array.Empty<string>());

    private static TakeCamera Cam(string source, DateTime start, int seconds, string? tc, string clip = "") =>
        new(source, "", start, start.AddSeconds(seconds), tc, null, clip, false);

    private static MediaFile File_(string name, double? start, double? dur, string? camera = null, DateTime? modified = null) =>
        new("/c/" + name, MediaKind.Video, 1000, modified ?? T0, modified ?? T0)
        { StartSeconds = start, Duration = dur == null ? null : TimeSpan.FromSeconds(dur.Value), Camera = camera, Fps = 25 };

    [Fact]
    public void MatchesByTheClipName()
    {
        var take = MakeTake(1, T0, 60, Cam("R5", T0, 60, "10:00:00:00", "MVI_0001.MP4"));
        var r = Matcher.Run(new[] { take }, new[] { File_("MVI_0001.MP4", null, null), File_("MVI_0002.MP4", null, null) });
        var m = Assert.Single(r.Matches);
        Assert.Equal(MatchMethod.Name, m.Method);
        Assert.Equal("MVI_0001.MP4", m.File.Name);
        Assert.Single(r.Unmatched);
    }

    [Fact]
    public void MatchesByTimecodeAndPicksTheRightTakeFile()
    {
        var t1 = MakeTake(1, T0, 60, Cam("A", T0, 60, "10:00:00:00"));
        var t2 = MakeTake(2, T0.AddMinutes(5), 60, Cam("A", T0.AddMinutes(5), 60, "10:05:00:00"));
        var files = new[] { File_("C002.mov", 36300, 60), File_("C001.mov", 36000, 60) };
        var r = Matcher.Run(new[] { t1, t2 }, files);
        Assert.Equal(new[] { "C001.mov", "C002.mov" }, r.Matches.Select(m => m.File.Name));
        Assert.All(r.Matches, m => Assert.Equal(MatchMethod.Timecode, m.Method));
        Assert.Empty(r.Missing);
        Assert.Empty(r.Unmatched);
    }

    [Fact]
    public void AContinuousAudioFileServesSeveralTakesWithAnOffset()
    {
        var t1 = MakeTake(1, T0.AddMinutes(5), 60, Cam("DJI Mic 3", T0.AddMinutes(5), 60, "10:05:00:00"));
        var t2 = MakeTake(2, T0.AddMinutes(10), 60, Cam("DJI Mic 3", T0.AddMinutes(10), 60, "10:10:00:00"));
        var audio = File_("MIC_001.WAV", 36000, 1800) with { Kind = MediaKind.Audio };
        var r = Matcher.Run(new[] { t1, t2 }, new[] { audio });
        Assert.Equal(2, r.Matches.Count);
        Assert.Equal(300, r.Matches[0].Offset.TotalSeconds, 1);
        Assert.Equal(600, r.Matches[1].Offset.TotalSeconds, 1);
        Assert.Empty(r.Unmatched);
    }

    [Fact]
    public void AContinuousAudioFileServesATakeAfterMidnight()
    {
        // o gravador arrancou as 23:50 (o ficheiro dura 1 h); a take e a 00:05 do dia seguinte
        var start = new DateTime(2026, 11, 10, 0, 5, 0);
        var take = new Take("2026-11-10", "Noite", 1, start, start.AddSeconds(60), false, null, "00:05:00:00",
            new[] { Cam("F6", start, 60, "00:05:00:00") }, Array.Empty<string>());
        var audio = File_("ZOOM0001.WAV", 23 * 3600 + 50 * 60, 3600, modified: new DateTime(2026, 11, 10, 0, 50, 0)) with { Kind = MediaKind.Audio };
        var m = Assert.Single(Matcher.Run(new[] { take }, new[] { audio }).Matches);
        Assert.Equal(MatchMethod.Timecode, m.Method);
        Assert.Equal(15 * 60, m.Offset.TotalSeconds, 1);
    }

    [Fact]
    public void FallsBackToTheFileTimeWithLowerConfidence()
    {
        var take = MakeTake(1, T0, 60, Cam("BMPCC", T0, 60, null));
        var braw = File_("A001.braw", null, null, modified: T0.AddSeconds(63));
        var r = Matcher.Run(new[] { take }, new[] { braw });
        var m = Assert.Single(r.Matches);
        Assert.Equal(MatchMethod.Time, m.Method);
        Assert.True(m.Doubtful);
    }

    [Fact]
    public void DoesNotMatchAFileOfAnotherCamera()
    {
        var take = MakeTake(1, T0, 60, Cam("A", T0, 60, "10:00:00:00"));
        var r = Matcher.Run(new[] { take }, new[] { File_("X.mov", 36000, 60, camera: "B") });
        Assert.Empty(r.Matches);
        Assert.Single(r.Missing);
        Assert.Single(r.Unmatched);
    }

    [Fact]
    public void AFileOfOneTakeIsNotReusedForTheNext()
    {
        var t1 = MakeTake(1, T0, 60, Cam("A", T0, 60, "10:00:00:00"));
        var t2 = MakeTake(2, T0.AddSeconds(1), 60, Cam("A", T0.AddSeconds(1), 60, "10:00:01:00"));
        var r = Matcher.Run(new[] { t1, t2 }, new[] { File_("ONLY.mov", 36000, 60) });
        Assert.Single(r.Matches);
        Assert.Single(r.Missing);
    }
}

public class OutputTests
{
    private static Take SampleTake(string? verdict = "good") => new("2026-11-09", "Dia 1", 7, new DateTime(2026, 11, 9, 10, 0, 0), null, false,
        verdict, null, Array.Empty<TakeCamera>(), new[] { "boa, mas com ruido" });

    [Fact]
    public void RendersTheFolderTemplateSafely()
    {
        var f = new MediaFile("/card/A001:1.mov", MediaKind.Video, 1, DateTime.Now, DateTime.Now);
        var cam = new TakeCamera("BMPCC", "A-cam", null, null, null, null, "", false);
        var p = PathTemplate.Render(PathTemplate.Default, SampleTake(), cam, f);
        Assert.Equal(string.Join(Path.DirectorySeparatorChar, "2026-11-09", "Dia 1", "T07", "A-cam", "A001_1.mov"), p);
    }

    [Theory]
    [InlineData("a:b?c.", "a_b_c")]
    [InlineData("..", "_")]
    [InlineData("  x  ", "x")]
    public void SafeNames(string input, string expected) => Assert.Equal(expected, PathTemplate.Safe(input));

    [Fact]
    public void ResolveCsvHasTheMetadataColumns()
    {
        var cam = new TakeCamera("R5", "B", null, null, null, null, "", false);
        var m = new Match(SampleTake(), cam, new MediaFile("/x/MVI_0001.MP4", MediaKind.Video, 1, DateTime.Now, DateTime.Now), MatchMethod.Name, 1, TimeSpan.Zero);
        var lines = ResolveCsv.Build(new[] { m }).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("File Name,Scene,Shot,Take,Good Take,Camera #,Comments,Description", lines[0]);
        Assert.Equal("MVI_0001.MP4,Dia 1,2026-11-09,7,true,B,\"boa, mas com ruido\",", lines[1]);
    }

    [Fact]
    public void GapReportListsWhatIsMissing()
    {
        var r = new MatchResult();
        var cam = new TakeCamera("R5", "B", null, null, "10:00:00:00", null, "", false);
        r.Missing.Add((SampleTake(), cam));
        r.Unmatched.Add(new MediaFile("/x/ORPHAN.mov", MediaKind.Video, 1, DateTime.Now, DateTime.Now) { Camera = "B" });
        var text = GapReport.Build(r, 1);
        Assert.Contains("TAKES WITHOUT A FILE", text);
        Assert.Contains("take 7", text);
        Assert.Contains("FILES WITHOUT A TAKE", text);
        Assert.Contains("ORPHAN.mov", text);
    }
}

public class CopierTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("proton-copy-").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    private string Make(string rel, int size, int seed)
    {
        var p = Path.Combine(_dir, "src", rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        var b = new byte[size];
        new Random(seed).NextBytes(b);
        File.WriteAllBytes(p, b);
        File.SetLastWriteTime(p, new DateTime(2026, 11, 9, 10, 1, 0));
        return p;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4095)]
    [InlineData(4096)]
    [InlineData(4097)]
    [InlineData(4 * 1024 * 1024 - 1)]
    [InlineData(4 * 1024 * 1024 + 4097)]
    public void ReadingFromTheDiskGivesTheSameHash(int size)
    {
        var p = Make($"sizes/f{size}.bin", size, size);
        var (hash, fromDisk) = DiskRead.Hash(p);
        Assert.Equal(Copier.HashFile(p), hash);
        Assert.Equal(DiskRead.Supported, fromDisk);
    }

    [Fact]
    public async Task TheCopyIsReadBackFromTheDisk()
    {
        // Windows (sem buffer), macOS (F_NOCACHE: prova que o argumento chega ao fcntl no Apple Silicon) e Linux (fadvise).
        Assert.True(DiskRead.Supported, "this system cannot read past the cache");
        var it = new CopyItem(Make("card/C001.mov", 5_000_123, 7), Path.Combine(_dir, "dst", "C001.mov"));
        var r = Assert.Single(await Copier.CopyAllAsync(new[] { it }));
        Assert.True(r.Ok, r.Error);
        Assert.True(r.FromDisk);
        var again = Assert.Single(await Copier.CopyAllAsync(new[] { it }));                 // ja copiado: confere do disco tambem
        Assert.True(again.Skipped && again.FromDisk);
        var manifest = Path.Combine(_dir, "manifest.csv");
        Copier.WriteManifest(manifest, new[] { r });
        Assert.EndsWith(",ok,disk", File.ReadAllLines(manifest)[1]);
    }

    [Fact]
    public async Task CopiesInParallelVerifiesAndKeepsTheDates()
    {
        var items = new List<CopyItem>();
        for (var i = 0; i < 4; i++)
            items.Add(new CopyItem(Make($"card{i}/C00{i}.mov", 9_000_000 + i, i), Path.Combine(_dir, "dst", $"cam{i}", $"C00{i}.mov")));
        var results = await Copier.CopyAllAsync(items, parallel: 3);
        Assert.All(results, r => Assert.True(r.Ok, r.Error));
        foreach (var it in items)
        {
            Assert.Equal(File.ReadAllBytes(it.Source), File.ReadAllBytes(it.Dest));
            Assert.Equal(new DateTime(2026, 11, 9, 10, 1, 0), File.GetLastWriteTime(it.Dest));
            Assert.False(File.Exists(it.Dest + Copier.PartSuffix));
        }
        Assert.Equal(4, results.Select(r => r.Hash).Distinct().Count());
    }

    [Fact]
    public void AnAlreadyCopiedFileIsSkippedAndADifferentOneIsRefused()
    {
        var src = Make("a.mov", 1_000_000, 1);
        var dst = Path.Combine(_dir, "dst", "a.mov");
        Assert.True(Copier.CopyOne(new CopyItem(src, dst)).Ok);
        var again = Copier.CopyOne(new CopyItem(src, dst));
        Assert.True(again.Ok && again.Skipped);
        File.WriteAllBytes(dst, new byte[1_000_000]);
        var diff = Copier.CopyOne(new CopyItem(src, dst));
        Assert.False(diff.Ok);
        Assert.Contains("different", diff.Error);
    }

    [Fact]
    public void ManifestListsEveryFile()
    {
        var src = Make("a.mov", 1000, 2);
        var r = Copier.CopyOne(new CopyItem(src, Path.Combine(_dir, "dst", "a.mov")));
        var man = Path.Combine(_dir, "manifest.csv");
        Copier.WriteManifest(man, new[] { r });
        var text = File.ReadAllText(man);
        Assert.Contains("source,dest,bytes,xxh3,status", text);
        Assert.Contains(r.Hash!, text);
        Assert.Contains(",ok", text);
    }

    [Fact]
    public void ASourceThatIsMissingFailsCleanly()
    {
        var r = Copier.CopyOne(new CopyItem(Path.Combine(_dir, "nope.mov"), Path.Combine(_dir, "dst", "nope.mov")));
        Assert.False(r.Ok);
        Assert.Contains("not found", r.Error);
    }
}

public class SessionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("proton-session-").FullName;
    public void Dispose() => Directory.Delete(_dir, true);
    private static string Fx(string n) => Path.Combine(AppContext.BaseDirectory, "Fixtures", n);

    [Fact]
    public async Task LoadsTakesAndOffersTheCameraNames()
    {
        var s = new ProtonSession();
        await s.LoadTakesAsync(Fx("atom-takes.json"));
        Assert.Equal(new[] { "A", "B", "F6" }, s.CameraNames);
    }

    [Fact]
    public async Task WarnsAboutCardsWithoutCameraAndRunsTheWholeFlow()
    {
        var card = Path.Combine(_dir, "card"); Directory.CreateDirectory(card);
        File.WriteAllBytes(Path.Combine(card, "MVI_0001.MP4"), Builders.Mov(900025, 25, 60, 3_000_000_000));
        var s = new ProtonSession { Destination = Path.Combine(_dir, "dst") };
        await s.LoadTakesAsync(Fx("atom-takes.json"));
        var c = s.AddCard(card);
        Assert.Single(s.CardsWithoutCamera);
        c.Camera = "B";
        Assert.Empty(s.CardsWithoutCamera);
        var r = s.Match();
        Assert.Single(r.Matches);
        Assert.Contains("1 matched", s.Summary());
        var results = await s.CopyAsync();
        Assert.All(results, x => Assert.True(x.Ok, x.Error));
        Assert.True(File.Exists(Path.Combine(s.Destination, "2026-11-09", "Dia 1", "T01", "B", "MVI_0001.MP4")));
        Assert.True(File.Exists(Path.Combine(s.Destination, "resolve.csv")));
        Assert.True(File.Exists(Path.Combine(s.Destination, "report.txt")));
        Assert.True(File.Exists(Path.Combine(s.Destination, "proton_manifest.csv")));
    }

    [Fact]
    public void CopyNeedsADestinationAndTakes()
    {
        var s = new ProtonSession();
        Assert.Throws<InvalidOperationException>(() => s.Match());
        Assert.Throws<InvalidOperationException>(() => s.PlanCopy());
    }

    [Fact]
    public void FindsTheNewestExportOnAStick()
    {
        var stick = Path.Combine(_dir, "stick", "AtomBridge"); Directory.CreateDirectory(stick);
        var old = Path.Combine(stick, "atom-takes-2026-11-08-100000.json");
        var cur = Path.Combine(stick, "atom-takes-2026-11-09-100000.json");
        File.WriteAllText(old, "{}"); File.WriteAllText(cur, "{}");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-1));
        var found = UsbDiscovery.Find(new[] { Path.Combine(_dir, "stick"), Path.Combine(_dir, "empty") });
        Assert.Equal(new[] { cur, old }, found);
    }

    [Fact]
    public void SettingsSurviveARoundTripAndABrokenFile()
    {
        var p = Path.Combine(_dir, "cfg", "settings.json");
        new ProtonSettings { AtomAddress = "http://atom:8080", Parallel = 6 }.Save(p);
        var back = ProtonSettings.Load(p);
        Assert.Equal("http://atom:8080", back.AtomAddress);
        Assert.Equal(6, back.Parallel);
        File.WriteAllText(p, "{nope");
        Assert.Equal(4, ProtonSettings.Load(p).Parallel);
    }
}

public class RoutineCheckTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("proton-rc-").FullName;
    public void Dispose() => Directory.Delete(_dir, true);
    private static readonly DateTime Today = new(2026, 11, 9, 10, 0, 0);

    [Fact]
    public void AClipFromAnotherDayWithTheSameTimecodeIsNotMatched()
    {
        var cam = new TakeCamera("A", "", Today, Today.AddSeconds(60), "10:00:00:00", null, "", false);
        var take = new Take("2026-11-09", "Dia 2", 1, Today, Today.AddSeconds(60), false, null, "10:00:00:00", new[] { cam }, Array.Empty<string>());
        var yesterday = new MediaFile("/c/C001.mov", MediaKind.Video, 1, Today.AddDays(-1), Today.AddDays(-1).AddSeconds(60))
        { StartSeconds = 36000, Duration = TimeSpan.FromSeconds(60), RecordedAt = Today.AddDays(-1) };
        var today = new MediaFile("/c/C002.mov", MediaKind.Video, 1, Today, Today.AddSeconds(60))
        { StartSeconds = 36000, Duration = TimeSpan.FromSeconds(60), RecordedAt = Today };
        var r = Matcher.Run(new[] { take }, new[] { yesterday, today });
        Assert.Equal("C002.mov", Assert.Single(r.Matches).File.Name);
        Assert.Equal("C001.mov", Assert.Single(r.Unmatched).Name);
    }

    [Fact]
    public void AClipNameRepeatedOnAnotherDayPicksTodaysFile()
    {
        var cam = new TakeCamera("R5", "", Today, Today.AddSeconds(60), null, null, "MVI_0001.MP4", false);
        var take = new Take("2026-11-09", "Dia 2", 1, Today, Today.AddSeconds(60), false, null, null, new[] { cam }, Array.Empty<string>());
        var old = new MediaFile("/card1/MVI_0001.MP4", MediaKind.Video, 1, Today.AddDays(-2), Today.AddDays(-2));
        var cur = new MediaFile("/card2/MVI_0001.MP4", MediaKind.Video, 1, Today, Today.AddSeconds(61));
        var r = Matcher.Run(new[] { take }, new[] { old, cur });
        Assert.Equal("/card2/MVI_0001.MP4", Assert.Single(r.Matches).File.Path);
    }

    [Fact]
    public async Task TwoFilesWithTheSameDestinationBothGetCopied()
    {
        var c1 = Path.Combine(_dir, "card1"); var c2 = Path.Combine(_dir, "card2");
        Directory.CreateDirectory(c1); Directory.CreateDirectory(c2);
        File.WriteAllBytes(Path.Combine(c1, "TEST.MP4"), new byte[] { 1, 2, 3 });          // os dois sem take: _unmatched/B/TEST.MP4
        File.WriteAllBytes(Path.Combine(c2, "TEST.MP4"), new byte[] { 4, 5, 6 });
        var s = new ProtonSession { Destination = Path.Combine(_dir, "dst") };
        await s.LoadTakesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "atom-takes.json"));
        s.AddCard(c1, "B"); s.AddCard(c2, "B");
        s.Match();
        var plan = s.PlanCopy();
        Assert.Equal(2, plan.Select(p => p.Dest).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var results = await s.CopyAsync();
        Assert.All(results, x => Assert.True(x.Ok, x.Error));
    }
}

public class UserBitsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("proton-ub-").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    [Theory]
    [InlineData("00629070", 2026, 9, 7)]           // como o TC-1 manda (o Atom confirmou-o)
    [InlineData("07092600", 2026, 9, 7)]           // a mesma data pela ordem do ecra
    [InlineData("00 62 90 70", 2026, 9, 7)]
    public void ReadsTheDateInBothOrders(string ub, int y, int m, int d) => Assert.Equal(new DateOnly(y, m, d), UserBits.Date(ub));

    [Theory]
    [InlineData("00000000")]
    [InlineData("FFFFFFFF")]
    [InlineData("12345")]
    [InlineData("0A629070")]
    [InlineData("")]
    public void NotADate(string ub) => Assert.Null(UserBits.Date(ub));

    [Fact]
    public void WhenBothOrdersAreDatesTheFileDateDecides()
    {
        // "05111020" = 2011-01-02 pela ordem do LTC e 2010-11-05 pela ordem do ecra. Sem pista nao se escolhe.
        Assert.Null(UserBits.Date("05111020"));
        Assert.Equal(new DateOnly(2011, 1, 2), UserBits.Date("05111020", new DateTime(2011, 1, 2, 9, 0, 0)));
        Assert.Equal(new DateOnly(2010, 11, 5), UserBits.Date("05111020", new DateTime(2010, 11, 6)));
    }

    [Fact]
    public void TheWavReaderReadsUbitsFromIxml()
    {
        var path = Path.Combine(_dir, "F6.WAV");
        File.WriteAllBytes(path, Builders.Wav(48000, 1, 48000UL * 36000, "<BWFXML><UBITS>00629070</UBITS></BWFXML>", date: "2026-09-07"));
        var f = new WavReader().Read(path);
        Assert.Equal("00629070", f.UserBits);
        Assert.Equal(new DateOnly(2026, 9, 7), f.UserBitsDate);
    }

    [Fact]
    public void TheUserBitsDateBeatsAWrongCameraClock()
    {
        // O relogio do gravador diz que e de ontem; os user bits (do gerador) dizem que e de hoje: conta a de hoje.
        var today = new DateTime(2026, 9, 7, 10, 0, 0);
        var cam = new TakeCamera("F6", "", today, today.AddSeconds(60), "10:00:00:00", null, "", false);
        var take = new Take("2026-09-07", "Dia 1", 1, today, today.AddSeconds(60), false, null, "10:00:00:00", new[] { cam }, Array.Empty<string>());
        var wrongClock = new MediaFile("/a/F6.WAV", MediaKind.Audio, 1, today.AddDays(-1), today.AddDays(-1))
        { StartSeconds = 36000, Duration = TimeSpan.FromSeconds(60), RecordedAt = today.AddDays(-1), UserBits = "00629070", UserBitsDate = new DateOnly(2026, 9, 7) };
        var m = Assert.Single(Matcher.Run(new[] { take }, new[] { wrongClock }).Matches);
        Assert.False(m.Doubtful);
    }
}

public class ReviewTests
{
    private static readonly DateTime T0 = new(2026, 11, 9, 10, 0, 0);

    private static (TakesExport, MatchResult) Scenario(double fileStart, double fileLength, bool fault = false)
    {
        var cam = new TakeCamera("A", "A-cam", T0, T0.AddSeconds(60), "10:00:00:00", "10:01:00:00", "", fault);
        var miss = new TakeCamera("B", "", T0, T0.AddSeconds(60), "10:00:00:00", null, "", false);
        var take = new Take("2026-11-09", "Dia 1", 3, T0, T0.AddSeconds(60), false, "good", "10:00:00:00", new[] { cam, miss }, new[] { "boa" });
        var file = new MediaFile("/c/C001.mov", MediaKind.Video, 1, T0, T0.AddSeconds(60))
        { StartSeconds = fileStart, Duration = TimeSpan.FromSeconds(fileLength), Fps = 25, RecordedAt = T0, Camera = "A-cam" };
        var orphan = new MediaFile("/c/C099.mov", MediaKind.Video, 1, T0, T0) { Camera = "A-cam", Note = "no timecode track in this file" };
        var export = new TakesExport("atom-takes/1", "atom", 25, new[] { take });
        return (export, Matcher.Run(export.Takes, new[] { file, orphan }));
    }

    [Fact]
    public void AGoodMatchShowsItsEvidence()
    {
        var (e, r) = Scenario(36000.4, 60);
        var rev = MatchReview.Build(e, r);
        var t = Assert.Single(rev.Takes);
        Assert.Contains("Take 3", t.Header);
        Assert.Contains("GOOD", t.Header);
        var a = t.Rows[0];
        Assert.Equal(ReviewCheck.Ok, a.Status);
        Assert.Equal("10:00:00:00", a.AtomTc);
        Assert.Equal("10:00:00:10", a.FileTc);
        Assert.Equal("+0.40 s (+10 fr)", a.TcDiff);
        Assert.Equal("1:00", a.TakeLength);
        Assert.Equal("1:00", a.FileLength);
        Assert.Equal("2026-11-09 (file)", a.FileDay);
        Assert.Equal(ReviewCheck.Missing, t.Rows[1].Status);
        Assert.False(t.AllOk);
        var orphan = Assert.Single(rev.Unmatched);
        Assert.Equal(ReviewCheck.NoTake, orphan.Status);
        Assert.Contains("no timecode", orphan.Why);
    }

    [Fact]
    public void ALengthMismatchOrAFaultAsksToCheck()
    {
        var (e, r) = Scenario(36000, 40, fault: true);
        var a = MatchReview.Build(e, r).Takes[0].Rows[0];
        Assert.Equal(ReviewCheck.Check, a.Status);
        Assert.Contains("the file lasts 0:40, the take 1:00", a.Why);
        Assert.Contains("the camera failed", a.Why);
    }

    [Fact]
    public void ATimecodeFarFromTheAtomAsksToCheck()
    {
        var (e, r) = Scenario(35998.5, 62);
        var a = MatchReview.Build(e, r).Takes[0].Rows[0];
        Assert.Equal(ReviewCheck.Check, a.Status);
        Assert.Contains("1.5 s off the Atom", a.Why);
    }

    [Fact]
    public void TheCsvHasOneLinePerCameraAndOrphan()
    {
        var (e, r) = Scenario(36000.4, 60);
        var lines = MatchReview.Build(e, r).ToCsv().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);
        Assert.StartsWith("take,day,session,status,camera", lines[0]);
        Assert.StartsWith("3,2026-11-09,Dia 1,OK,A-cam,10:00:00:00,10:00:00:10", lines[1]);
        Assert.Contains(",NO TAKE,", lines[3]);
    }
}

/// <summary>Ficheiros estragados (cortados, bytes trocados): os leitores nunca rebentam, dizem so que nao conseguiram ler.</summary>
public class CorruptFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("proton-fuzz-").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    [Theory]
    [InlineData("mov")]
    [InlineData("wav")]
    public void ReadersSurviveDamagedFiles(string kind)
    {
        var good = kind == "mov"
            ? Builders.Mov(900025, 25, 60, 3_000_000_000)
            : Builders.Wav(48000, 2, 48000UL * 36002, "<BWFXML><TAKE>1</TAKE><UBITS>00621190</UBITS></BWFXML>", date: "2026-11-09", time: "10:00:02");
        IMediaReader reader = kind == "mov" ? new IsoBmffReader() : new WavReader();
        var rnd = new Random(1234);
        var path = Path.Combine(_dir, "x." + kind);
        for (var i = 0; i < 1500; i++)
        {
            var b = (byte[])good.Clone();
            if (i % 3 == 0) b = b[..rnd.Next(0, b.Length)];                                       // cortado
            var flips = 1 + rnd.Next(8);
            for (var k = 0; k < flips && b.Length > 0; k++) b[rnd.Next(b.Length)] = (byte)rnd.Next(256);   // bytes trocados
            File.WriteAllBytes(path, b);
            var ex = Record.Exception(() => reader.Read(path));
            Assert.True(ex == null, $"case {i}: {ex?.GetType().Name}: {ex?.Message}");
        }
    }
}

