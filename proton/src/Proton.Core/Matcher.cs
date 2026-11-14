namespace Proton.Core;

public enum MatchMethod { Name, Timecode, Time }

/// <summary>Um ficheiro associado a uma camara numa take. Offset: onde a take comeca dentro do ficheiro (audio continuo, como o DJI Mic).</summary>
public sealed record Match(Take Take, TakeCamera Camera, MediaFile File, MatchMethod Method, double Confidence, TimeSpan Offset)
{
    public bool Doubtful => Confidence < 0.8;
    /// <summary>Porque e duvidosa (vazio se nao e): o que o operador deve conferir.</summary>
    public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();
}

public sealed class MatchResult
{
    public List<Match> Matches { get; } = new();
    /// <summary>Camaras de uma take sem nenhum ficheiro.</summary>
    public List<(Take Take, TakeCamera Camera)> Missing { get; } = new();
    /// <summary>Ficheiros que nao pertencem a nenhuma take.</summary>
    public List<MediaFile> Unmatched { get; } = new();
}

public sealed record MatchOptions
{
    /// <summary>Folga do timecode (segundos): a camara pode arrancar um pouco antes ou depois do Atom registar o REC.</summary>
    public double TimecodeTolerance { get; init; } = 2;
    /// <summary>Folga da hora do relogio (segundos), mais larga porque os relogios das camaras nao sao perfeitos.</summary>
    public double TimeTolerance { get; init; } = 15;
    /// <summary>Um ficheiro com a take a mais de TimecodeTolerance do inicio e um ficheiro continuo (audio) e serve varias takes.</summary>
    public double DefaultFps { get; init; } = 25;
    /// <summary>Diferenca maxima (horas) entre a data do ficheiro e a take para serem do mesmo dia. Mais de 12 h porque ha camaras que
    /// escrevem a hora local num campo que devia ser UTC; menos de 24 h para um clip de ontem com o mesmo timecode nao passar.</summary>
    public double SameDayHours { get; init; } = 20;
}

/// <summary>Associa os ficheiros copiados as takes do Atom: pelo nome do ficheiro, depois pelo timecode, por fim pela hora.</summary>
public static class Matcher
{
    public static MatchResult Run(IEnumerable<Take> takes, IReadOnlyList<MediaFile> files, MatchOptions? options = null, double atomFps = 25)
    {
        var o = options ?? new MatchOptions();
        var result = new MatchResult();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var take in takes)
        {
            foreach (var cam in take.Cameras)
            {
                if (cam.Start == null) continue;
                var m = Find(take, cam, files, used, o, atomFps);
                if (m == null) { result.Missing.Add((take, cam)); continue; }
                result.Matches.Add(m);
                if (m.Offset.TotalSeconds <= o.TimecodeTolerance) used.Add(m.File.Path);          // um ficheiro de uma take so serve essa take
            }
        }
        foreach (var f in files)
            if (!result.Matches.Any(m => string.Equals(m.File.Path, f.Path, StringComparison.OrdinalIgnoreCase))) result.Unmatched.Add(f);
        return result;
    }

    private static bool SameCamera(TakeCamera cam, MediaFile f) =>
        f.Camera == null || string.Equals(f.Camera, cam.Source, StringComparison.OrdinalIgnoreCase)
        || string.Equals(f.Camera, cam.Alias, StringComparison.OrdinalIgnoreCase);

    /// <summary>A hora a que o ficheiro foi gravado: a data dos user bits do timecode (vem do gerador, como a do Atom) com a hora do
    /// timecode, se o ficheiro a traz; senao o que o ficheiro diz (relogio da camara) ou a data de modificacao.</summary>
    private static DateTime FileTime(MediaFile f) =>
        f.UserBitsDate is DateOnly d ? d.ToDateTime(TimeOnly.MinValue).AddSeconds(f.StartSeconds ?? 43200) : f.RecordedAt ?? f.Modified;

    private static double HoursApart(TakeCamera cam, MediaFile f) =>
        cam.Start == null ? 0 : Math.Abs((FileTime(f) - cam.Start.Value).TotalHours);

    /// <summary>Os candidatos do mesmo dia que a take, se houver algum; senao todos (uma camara com o relogio errado), marcados como
    /// duvidosos. Um cartao com clips de varios dias repete nomes e timecodes.</summary>
    private static (List<MediaFile> Files, bool DateDoubt) SameDay(IEnumerable<MediaFile> cands, TakeCamera cam, MatchOptions o)
    {
        var all = cands.ToList();
        var good = all.Where(f => HoursApart(cam, f) <= o.SameDayHours).ToList();
        return good.Count > 0 ? (good, false) : (all, all.Count > 0);
    }

    private static Match? Find(Take take, TakeCamera cam, IReadOnlyList<MediaFile> files, HashSet<string> used, MatchOptions o, double atomFps)
    {
        var pool = files.Where(f => SameCamera(cam, f) && !used.Contains(f.Path)).ToList();
        if (pool.Count == 0) return null;

        // 1. o nome do ficheiro que o Atom anotou no STOP
        if (cam.Clip.Length > 0)
        {
            var clip = System.IO.Path.GetFileNameWithoutExtension(cam.Clip);
            var (byName, doubt) = SameDay(pool.Where(f => string.Equals(f.Stem, clip, StringComparison.OrdinalIgnoreCase)), cam, o);
            if (byName.Count >= 1)
            {
                var best = byName.OrderBy(f => HoursApart(cam, f)).First();
                var why = new List<string>();
                if (doubt) why.Add("the file is from another day than the take");
                else if (byName.Count > 1) why.Add($"{byName.Count} files with this name on the same day");
                return new Match(take, cam, best, MatchMethod.Name, doubt ? 0.6 : byName.Count == 1 ? 1.0 : 0.7, TimeSpan.Zero) { Reasons = why };
            }
        }

        // 2. o timecode: o inicio da take (e o fim) dentro do intervalo do ficheiro
        if (cam.TcStart != null && Timecode.TryParse(cam.TcStart, out var tcStart))
        {
            var cands = new List<(MediaFile File, double Offset)>();
            var (dayPool, dayDoubt) = SameDay(pool.Where(f => f.StartSeconds != null), cam, o);
            foreach (var f in dayPool)
            {
                var fps = f.Fps ?? atomFps;
                var from = tcStart.ToSeconds(fps);
                var length = cam.Stop != null && cam.Start != null ? (cam.Stop.Value - cam.Start.Value).TotalSeconds : 0;
                if (f.StartSeconds is not double start) continue;
                var offset = from - start;
                // O timecode volta a zero a meia-noite: um ficheiro continuo (audio) que comecou antes e uma take depois.
                if (offset < -o.TimecodeTolerance && f.Duration != null && offset + 86400 <= f.Duration.Value.TotalSeconds) offset += 86400;
                if (offset < -o.TimecodeTolerance) continue;
                if (f.Duration != null && offset + length > f.Duration.Value.TotalSeconds + o.TimecodeTolerance) continue;
                cands.Add((f, Math.Max(0, offset)));
            }
            if (cands.Count > 0)
            {
                var best = cands.OrderBy(c => c.Offset).First();
                var unique = cands.Count == 1 || cands.Count(c => c.Offset <= o.TimecodeTolerance) == 1;
                var why = new List<string>();
                if (dayDoubt) why.Add("the file is from another day than the take");
                if (!unique) why.Add($"{cands.Count} files contain this timecode");
                return new Match(take, cam, best.File, MatchMethod.Timecode, dayDoubt ? 0.6 : unique ? 0.9 : 0.6, TimeSpan.FromSeconds(best.Offset)) { Reasons = why };
            }
        }

        // 3. a hora do relogio: o fim do ficheiro (hora de modificacao) perto do STOP, ou a hora de gravacao perto do REC
        if (cam.Start != null)
        {
            var cands = new List<(MediaFile File, double Error)>();
            foreach (var f in pool)
            {
                double? err = null;
                if (f.RecordedAt != null) err = Math.Abs((f.RecordedAt.Value - cam.Start.Value).TotalSeconds);
                else if (cam.Stop != null) err = Math.Abs((f.Modified - cam.Stop.Value).TotalSeconds);
                if (err != null && err <= o.TimeTolerance) cands.Add((f, err.Value));
            }
            if (cands.Count > 0)
            {
                var best = cands.OrderBy(c => c.Error).First();
                var why = new List<string> { $"matched by the file clock only ({best.Error:0} s from the take)" };
                if (cands.Count > 1) why.Add($"{cands.Count} files within {o.TimeTolerance:0} s");
                return new Match(take, cam, best.File, MatchMethod.Time, cands.Count == 1 ? 0.5 : 0.3, TimeSpan.Zero) { Reasons = why };
            }
        }
        return null;
    }
}
