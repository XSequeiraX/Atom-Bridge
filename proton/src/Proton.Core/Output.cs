using System.Text;

namespace Proton.Core;

/// <summary>Onde cada ficheiro vai: um modelo de caminho editavel. {day} {session} {take} {take:00} {camera} {alias} {file} {stem} {ext}.</summary>
public static class PathTemplate
{
    public const string Default = "{day}/{session}/T{take:00}/{camera}/{file}";
    public const string Unmatched = "_unmatched/{camera}/{file}";

    public static string Render(string template, Take? take, TakeCamera? cam, MediaFile file, string? cameraName = null)
    {
        var camera = cam?.Label ?? cameraName ?? file.Camera ?? "camera";
        var segs = new List<string>();
        foreach (var raw in template.Split('/', '\\'))
        {
            if (raw.Length == 0) continue;
            var seg = System.Text.RegularExpressions.Regex.Replace(raw, @"\{(\w+)(?::(\w+))?\}", m =>
            {
                var key = m.Groups[1].Value.ToLowerInvariant();
                var fmt = m.Groups[2].Value;
                return key switch
                {
                    "day" => take?.Day ?? file.Modified.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    "session" => take?.Session ?? "",
                    "take" => take == null ? "" : fmt.Length > 0 ? take.Number.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture) : take.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "camera" => camera,
                    "alias" => cam?.Alias ?? "",
                    "file" => file.Name,
                    "stem" => file.Stem,
                    "ext" => System.IO.Path.GetExtension(file.Path).TrimStart('.'),
                    _ => m.Value,
                };
            });
            segs.Add(Safe(seg));
        }
        return string.Join(System.IO.Path.DirectorySeparatorChar, segs.Where(s => s.Length > 0));
    }

    /// <summary>Um nome de pasta ou ficheiro que serve no Windows e no Mac (exFAT): sem caracteres proibidos, sem ponto ou espaco no fim.</summary>
    public static string Safe(string name)
    {
        var bad = new HashSet<char>(System.IO.Path.GetInvalidFileNameChars()) { '<', '>', ':', '"', '|', '?', '*' };
        var sb = new StringBuilder();
        foreach (var c in name.Trim()) sb.Append(bad.Contains(c) ? '_' : c);
        var raw = sb.ToString();
        if (raw.Length > 0 && raw.All(c => c == '.')) return "_";               // "." e ".." nunca saem da pasta
        return raw.TrimEnd('.', ' ');
    }
}

/// <summary>O CSV para o DaVinci Resolve (Media Pool, Import Metadata): uma linha por ficheiro associado.</summary>
public static class ResolveCsv
{
    public static readonly string[] Columns = { "File Name", "Scene", "Shot", "Take", "Good Take", "Camera #", "Comments", "Description" };

    public static string Build(IEnumerable<Match> matches)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(",", Columns.Select(Quote))).Append('\n');             // sempre \n: igual no Windows, no Mac e no Atom
        foreach (var m in matches.OrderBy(x => x.Take.Start).ThenBy(x => x.Camera.Label))
        {
            var good = m.Take.Verdict switch { "good" => "true", "no-good" => "false", _ => "" };
            var comments = string.Join(" / ", m.Take.Notes);
            var desc = m.Offset.TotalSeconds > 2 ? $"take starts at {m.Offset:hh\\:mm\\:ss} in this file" : "";
            sb.Append(string.Join(",", new[]
            {
                m.File.Name, m.Take.Session, m.Take.Day, m.Take.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), good, m.Camera.Label, comments, desc,
            }.Select(Quote))).Append('\n');
        }
        return sb.ToString();
    }

    private static string Quote(string s) => s.Contains(',') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}

/// <summary>O relatorio do que falta: takes sem ficheiro, ficheiros sem take e correspondencias duvidosas.</summary>
public static class GapReport
{
    public static string Build(MatchResult r, int takeCount)
    {
        var sb = new StringBuilder();
        var matched = r.Matches.Count;
        sb.AppendLine($"Takes: {takeCount}   Matched files: {matched}   Missing: {r.Missing.Count}   Unmatched files: {r.Unmatched.Count}");
        var doubtful = r.Matches.Where(m => m.Doubtful).ToList();
        sb.AppendLine($"Doubtful matches: {doubtful.Count}");
        if (r.Missing.Count > 0)
        {
            sb.AppendLine().AppendLine("TAKES WITHOUT A FILE");
            foreach (var (t, c) in r.Missing.OrderBy(x => x.Take.Start))
                sb.AppendLine($"  {t.Day} take {t.Number} ({t.Session})  {c.Label}  {c.TcStart ?? "-"}{(c.Fault ? "  (the camera failed)" : "")}");
        }
        if (r.Unmatched.Count > 0)
        {
            sb.AppendLine().AppendLine("FILES WITHOUT A TAKE");
            foreach (var f in r.Unmatched) sb.AppendLine($"  {f.Name}  {f.Camera ?? "-"}  {f.Note ?? ""}".TrimEnd());
        }
        if (doubtful.Count > 0)
        {
            sb.AppendLine().AppendLine("DOUBTFUL (check these)");
            foreach (var m in doubtful)
                sb.AppendLine($"  {m.File.Name}  ->  {m.Take.Day} take {m.Take.Number}  {m.Camera.Label}  by {m.Method.ToString().ToLowerInvariant()}");
        }
        return sb.ToString();
    }
}
