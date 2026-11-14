using Proton.Core;

namespace Proton.Cli;

internal static class Program
{
    private const string Usage = """
        proton-cli - copy the camera cards and match the files to the takes the Atom logged

          proton-cli match --takes <atom-url|file> --media <dir>[=CAMERA] [--media ...] [--day YYYY-MM-DD] [--fps 25] [--out DIR]
              Reads the files, matches them to the takes and writes resolve.csv (DaVinci Resolve) and report.txt.

          proton-cli copy --media <dir>[=CAMERA] [--media ...] --to <dir> [--takes <atom-url|file>] [--day YYYY-MM-DD]
                      [--template "{day}/{session}/T{take:00}/{camera}/{file}"] [--parallel 4] [--fps 25]
              Copies with xxHash3 verification, several at the same time, and files each clip by take.
              Without --takes, clips go to _unmatched/<camera>/. Writes proton_manifest.csv next to the copies.

          --takes: the Atom address (http://atom:8080, e.g. over Tailscale) or an exported .json / .csv (USB stick).
        """;

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help") { Console.WriteLine(Usage); return args.Length == 0 ? 2 : 0; }
            if (args[0] == "version") { Console.WriteLine("proton-cli " + typeof(Program).Assembly.GetName().Version); return 0; }
            var o = Options.Parse(args.Skip(1).ToArray());
            return args[0] switch
            {
                "match" => await Match(o),
                "copy" => await Copy(o),
                _ => Fail("unknown command '" + args[0] + "'"),
            };
        }
        catch (Exception e) when (e is ArgumentException or FormatException or IOException or HttpRequestException or TaskCanceledException)
        {
            return Fail(e.Message);
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine("proton-cli: " + message);
        return 1;
    }

    private static List<MediaFile> ScanAll(Options o)
    {
        var files = new List<MediaFile>();
        foreach (var (dir, camera) in o.Media)
        {
            if (!Directory.Exists(dir)) throw new ArgumentException("folder not found: " + dir);
            var found = MediaScanner.Scan(dir, camera);
            Console.WriteLine($"{dir}{(camera != null ? " [" + camera + "]" : "")}: {found.Count} file(s)");
            files.AddRange(found);
        }
        return files;
    }

    private static async Task<int> Match(Options o)
    {
        if (o.Takes == null || o.Media.Count == 0) return Fail("match needs --takes and at least one --media");
        var export = await AtomClient.LoadAsync(o.Takes, o.Day);
        var files = ScanAll(o);
        var result = Matcher.Run(export.Takes, files, new MatchOptions { DefaultFps = o.Fps ?? export.Fps }, o.Fps ?? export.Fps);
        Directory.CreateDirectory(o.Out);
        File.WriteAllText(Path.Combine(o.Out, "resolve.csv"), ResolveCsv.Build(result.Matches));
        var report = GapReport.Build(result, export.Takes.Count);
        File.WriteAllText(Path.Combine(o.Out, "report.txt"), report);
        Console.WriteLine(report);
        Console.WriteLine("written: " + Path.GetFullPath(o.Out));
        return result.Missing.Count + result.Unmatched.Count == 0 ? 0 : 3;
    }

    private static async Task<int> Copy(Options o)
    {
        if (o.To == null || o.Media.Count == 0) return Fail("copy needs --to and at least one --media");
        var files = ScanAll(o);
        TakesExport? export = null;
        MatchResult? result = null;
        if (o.Takes != null)
        {
            export = await AtomClient.LoadAsync(o.Takes, o.Day);
            result = Matcher.Run(export.Takes, files, new MatchOptions { DefaultFps = o.Fps ?? export.Fps }, o.Fps ?? export.Fps);
        }
        var items = new List<CopyItem>();
        foreach (var f in files)
        {
            var m = result?.Matches.FirstOrDefault(x => x.File.Path == f.Path);
            var rel = m != null ? PathTemplate.Render(o.Template, m.Take, m.Camera, f) : PathTemplate.Render(PathTemplate.Unmatched, null, null, f);
            items.Add(new CopyItem(f.Path, Path.Combine(o.To, rel)));
        }
        var progress = new Progress<CopyProgress>(p =>
        {
            if (p.Done == p.Total) Console.WriteLine($"  copied {Path.GetFileName(p.File)}");
        });
        Console.WriteLine($"copying {items.Count} file(s), {o.Parallel} at a time ...");
        var results = await Copier.CopyAllAsync(items, o.Parallel, progress);
        Directory.CreateDirectory(o.To);
        Copier.WriteManifest(Path.Combine(o.To, "proton_manifest.csv"), results);
        var failed = results.Where(r => !r.Ok).ToList();
        Console.WriteLine($"done: {results.Count(r => r.Ok && !r.Skipped)} copied, {results.Count(r => r.Skipped)} already there, {failed.Count} FAILED");
        var memoryOnly = results.Count(r => r.Ok && !r.FromDisk);
        Console.WriteLine(memoryOnly == 0 ? "verified: read back from the disk"
            : $"verified: {memoryOnly} file(s) only from memory (this drive does not allow reading past the cache)");
        foreach (var r in failed) Console.WriteLine($"  FAILED {r.Item.Source}: {r.Error}");
        if (result != null && export != null)
        {
            File.WriteAllText(Path.Combine(o.To, "resolve.csv"), ResolveCsv.Build(result.Matches));
            File.WriteAllText(Path.Combine(o.To, "report.txt"), GapReport.Build(result, export.Takes.Count));
            Console.WriteLine(GapReport.Build(result, export.Takes.Count));
        }
        return failed.Count == 0 ? 0 : 4;
    }
}

internal sealed class Options
{
    public string? Takes, To, Day;
    public string Out = "proton_out";
    public string Template = PathTemplate.Default;
    public double? Fps;
    public int Parallel = 4;
    public List<(string Dir, string? Camera)> Media = new();

    public static Options Parse(string[] a)
    {
        var o = new Options();
        for (var i = 0; i < a.Length; i++)
        {
            string Next() => i + 1 < a.Length ? a[++i] : throw new ArgumentException("missing value after " + a[i]);
            switch (a[i])
            {
                case "--takes": o.Takes = Next(); break;
                case "--to": o.To = Next(); break;
                case "--out": o.Out = Next(); break;
                case "--day": o.Day = Next(); break;
                case "--template": o.Template = Next(); break;
                case "--fps": o.Fps = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--parallel": o.Parallel = Math.Clamp(int.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture), 1, 16); break;
                case "--media":
                    var v = Next();
                    var eq = v.LastIndexOf('=');
                    o.Media.Add(eq > 0 ? (v[..eq], v[(eq + 1)..]) : (v, null));
                    break;
                default: throw new ArgumentException("unknown option " + a[i]);
            }
        }
        return o;
    }
}
