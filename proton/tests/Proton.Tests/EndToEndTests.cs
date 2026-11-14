using Proton.Core;
using Xunit;

namespace Proton.Tests;

/// <summary>Dois cartoes e um gravador de audio sinteticos, as takes do Atom (o fixture do contrato), e o caminho todo:
/// ler, cruzar, copiar por take e gerar o CSV do Resolve e o relatorio.</summary>
public class EndToEndTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("proton-e2e-").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public async Task CardsToTakes()
    {
        var fx = TakesLoader.FromFile(Path.Combine(AppContext.BaseDirectory, "Fixtures", "atom-takes.json"));
        // R5 (cartao B): o clip da take 1 tem o nome que o Atom anotou; a take 2 ficou aberta
        var r5 = Path.Combine(_dir, "cardB"); Directory.CreateDirectory(r5);
        File.WriteAllBytes(Path.Combine(r5, "MVI_0001.MP4"), Builders.Mov(900025, 25, 60, 3_000_000_000));
        // BMPCC (cartao A): BRAW, sem leitor de timecode, encontra-se pelo nome que o Atom anotou
        var bm = Path.Combine(_dir, "ssdA"); Directory.CreateDirectory(bm);
        File.WriteAllBytes(Path.Combine(bm, "A001_C001.braw"), new byte[100]);
        File.WriteAllBytes(Path.Combine(bm, "A001_C099.braw"), new byte[100]);          // um clip que nenhuma take reclama
        // F6: WAV com o timecode da take 1 a +2 s
        var f6 = Path.Combine(_dir, "f6"); Directory.CreateDirectory(f6);
        File.WriteAllBytes(Path.Combine(f6, "ZOOM0001.WAV"), Builders.Wav(48000, 60, 48000UL * 36002, "<BWFXML><SCENE>1</SCENE><TAKE>1</TAKE></BWFXML>"));

        var files = MediaScanner.Scan(r5, "R5").Concat(MediaScanner.Scan(bm, "BMPCC")).Concat(MediaScanner.Scan(f6, "F6")).ToList();
        var result = Matcher.Run(fx.Takes, files, atomFps: fx.Fps);

        Assert.Equal(3, result.Matches.Count);
        var byCam = result.Matches.ToDictionary(m => m.Camera.Source);
        Assert.Equal(MatchMethod.Name, byCam["R5"].Method);
        Assert.Equal(MatchMethod.Name, byCam["BMPCC"].Method);
        Assert.Equal(MatchMethod.Timecode, byCam["F6"].Method);
        Assert.Equal("A001_C099.braw", Assert.Single(result.Unmatched).Name);
        Assert.Equal(2, result.Missing.Count);                                       // a take 2: BMPCC e R5 sem ficheiro

        // copiar por take
        var dst = Path.Combine(_dir, "dst");
        var items = files.Select(f =>
        {
            var m = result.Matches.FirstOrDefault(x => x.File.Path == f.Path);
            var rel = m != null ? PathTemplate.Render(PathTemplate.Default, m.Take, m.Camera, f) : PathTemplate.Render(PathTemplate.Unmatched, null, null, f);
            return new CopyItem(f.Path, Path.Combine(dst, rel));
        }).ToList();
        var copied = await Copier.CopyAllAsync(items, 3);
        Assert.All(copied, c => Assert.True(c.Ok, c.Error));
        Assert.True(File.Exists(Path.Combine(dst, "2026-11-09", "Dia 1", "T01", "B", "MVI_0001.MP4")));
        Assert.True(File.Exists(Path.Combine(dst, "2026-11-09", "Dia 1", "T01", "A", "A001_C001.braw")));
        Assert.True(File.Exists(Path.Combine(dst, "2026-11-09", "Dia 1", "T01", "F6", "ZOOM0001.WAV")));
        Assert.True(File.Exists(Path.Combine(dst, "_unmatched", "BMPCC", "A001_C099.braw")));

        var csv = ResolveCsv.Build(result.Matches);
        Assert.Contains("MVI_0001.MP4,Dia 1,2026-11-09,1,true,B,", csv);
        Assert.Contains("ZOOM0001.WAV", csv);
        var report = GapReport.Build(result, fx.Takes.Count);
        Assert.Contains("A001_C099.braw", report);
        Assert.Contains("take 2", report);
    }
}
