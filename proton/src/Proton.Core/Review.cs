using System.Globalization;
using System.Text;

namespace Proton.Core;

/// <summary>Uma linha da revisao: uma camara numa take (ou um ficheiro sem take), com a prova da associacao.</summary>
public sealed record ReviewRow(string Status, string Camera, string? AtomTc, string? FileTc, string? TcDiff, string? TakeLength,
    string? FileLength, string? FileDay, string? File, string? Method, string Why, string? Path);

public sealed record ReviewTake(Take Take, string Header, IReadOnlyList<ReviewRow> Rows)
{
    public bool AllOk => Rows.All(r => r.Status == ReviewCheck.Ok);
}

public static class ReviewCheck
{
    public const string Ok = "OK", Check = "CHECK", Missing = "MISSING", NoTake = "NO TAKE";
    /// <summary>Diferenca de timecode (s) a partir da qual se pede para conferir.</summary>
    public const double TcWarn = 1.0;
    /// <summary>Diferenca de duracao (s) entre o ficheiro e a take a partir da qual se pede para conferir.</summary>
    public const double LengthWarn = 2.0;
}

/// <summary>O shoot log do Atom com os ficheiros associados e a prova de cada associacao: timecode do Atom e do ficheiro, a
/// diferenca, as duracoes, o dia, o metodo e, quando ha duvida, porque. E o que se confere antes de confiar no material.</summary>
public sealed class MatchReview
{
    public List<ReviewTake> Takes { get; } = new();
    public List<ReviewRow> Unmatched { get; } = new();

    public static MatchReview Build(TakesExport export, MatchResult result)
    {
        var review = new MatchReview();
        foreach (var take in export.Takes.OrderBy(t => t.Start).ThenBy(t => t.Number))
        {
            var rows = new List<ReviewRow>();
            foreach (var cam in take.Cameras)
            {
                var m = result.Matches.FirstOrDefault(x => ReferenceEquals(x.Take, take) && ReferenceEquals(x.Camera, cam));
                rows.Add(m == null ? MissingRow(cam) : MatchRow(m, export.Fps));
            }
            var head = $"Take {take.Number}   {take.Session}   {take.Day}   {take.Start:HH:mm:ss}" +
                       (take.End != null ? $"-{take.End:HH:mm:ss}" : "  (still open)") +
                       (take.Verdict != null ? "   " + take.Verdict.ToUpperInvariant() : "") +
                       (take.Notes.Count > 0 ? "   " + string.Join(" / ", take.Notes) : "");
            review.Takes.Add(new ReviewTake(take, head, rows));
        }
        foreach (var f in result.Unmatched)
            review.Unmatched.Add(new ReviewRow(ReviewCheck.NoTake, f.Camera ?? "-", null, FileTc(f, 0, export.Fps), null, null, Len(f.Duration),
                Day(f), f.Name, null, f.Note ?? "no take matches this file", f.Path));
        return review;
    }

    private static ReviewRow MissingRow(TakeCamera cam) =>
        new(ReviewCheck.Missing, cam.Label, cam.TcStart, null, null, Len(TakeLength(cam)), null, null, null, null,
            "no file found" + (cam.Fault ? "; the camera failed during the take" : ""), null);

    private static ReviewRow MatchRow(Match m, double atomFps)
    {
        var why = new List<string>(m.Reasons);
        var f = m.File;
        var fps = f.Fps ?? atomFps;
        var continuous = m.Offset.TotalSeconds > 2;
        string? diff = null;
        // Um ficheiro por take: o inicio do ficheiro contra o REC do Atom. Audio continuo: a take esta dentro do ficheiro (o desvio e
        // onde comeca), e a diferenca nao se mede assim.
        if (!continuous && m.Camera.TcStart != null && Timecode.TryParse(m.Camera.TcStart, out var tc) && f.StartSeconds != null)
        {
            var d = f.StartSeconds.Value - tc.ToSeconds(fps);
            diff = string.Format(CultureInfo.InvariantCulture, "{0:+0.00;-0.00;0.00} s ({1:+0;-0;0} fr)", d, Math.Round(d * fps));
            if (Math.Abs(d) > ReviewCheck.TcWarn) why.Add($"the file timecode is {Math.Abs(d):0.0} s off the Atom");
        }
        var takeLen = TakeLength(m.Camera);
        if (!continuous && takeLen != null && f.Duration != null && Math.Abs(f.Duration.Value.TotalSeconds - takeLen.Value.TotalSeconds) > ReviewCheck.LengthWarn)
            why.Add($"the file lasts {Len(f.Duration)}, the take {Len(takeLen)}");
        if (m.Camera.Fault) why.Add("the camera failed during the take");
        var status = m.Doubtful || why.Count > 0 ? ReviewCheck.Check : ReviewCheck.Ok;
        var how = m.Method.ToString().ToLowerInvariant() + (continuous ? $" (take starts at {m.Offset:hh\\:mm\\:ss} in the file)" : "");
        return new ReviewRow(status, m.Camera.Label, m.Camera.TcStart, FileTc(f, continuous ? m.Offset.TotalSeconds : 0, atomFps), diff, Len(takeLen),
            Len(f.Duration), Day(f), f.Name, how, string.Join("; ", why), f.Path);
    }

    private static TimeSpan? TakeLength(TakeCamera c) => c.Start != null && c.Stop != null ? c.Stop - c.Start : null;

    private static string? FileTc(MediaFile f, double offset, double atomFps) =>
        f.StartSeconds == null ? null : Timecode.FromSeconds(f.StartSeconds.Value + offset, f.Fps ?? atomFps).ToString();

    private static string? Len(TimeSpan? t) => t == null ? null : t.Value.TotalHours >= 1 ? t.Value.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.Value.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    private static string? Day(MediaFile f) =>
        f.UserBitsDate is DateOnly d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " (user bits)"
        : f.RecordedAt is DateTime r ? r.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " (file)"
        : f.Modified.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " (modified)";

    public string ToCsv()
    {
        var sb = new StringBuilder("take,day,session,status,camera,atom_tc,file_tc,tc_diff,take_length,file_length,file_day,file,method,why\n");
        void Row(Take? t, ReviewRow r) => sb.Append(string.Join(",", new[]
        {
            t?.Number.ToString(CultureInfo.InvariantCulture) ?? "", t?.Day ?? "", t?.Session ?? "", r.Status, r.Camera, r.AtomTc ?? "", r.FileTc ?? "", r.TcDiff ?? "",
            r.TakeLength ?? "", r.FileLength ?? "", r.FileDay ?? "", r.File ?? "", r.Method ?? "", r.Why,
        }.Select(Q))).Append('\n');
        foreach (var t in Takes) foreach (var r in t.Rows) Row(t.Take, r);
        foreach (var r in Unmatched) Row(null, r);
        return sb.ToString();
    }

    private static string Q(string s) => s.Contains(',') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
