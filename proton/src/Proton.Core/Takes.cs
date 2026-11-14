using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Proton.Core;

/// <summary>Uma camara numa take (uma linha do export do Atom).</summary>
public sealed record TakeCamera(string Source, string Alias, DateTime? Start, DateTime? Stop, string? TcStart, string? TcStop,
    string Clip, bool Fault)
{
    /// <summary>O nome por que se conhece a camara: o alias, ou a fonte.</summary>
    public string Label => string.IsNullOrWhiteSpace(Alias) ? Source : Alias;
}

public sealed record Take(string Day, string Session, int Number, DateTime Start, DateTime? End, bool Open, string? Verdict,
    string? TcStart, IReadOnlyList<TakeCamera> Cameras, IReadOnlyList<string> Notes)
{
    public string Id => $"{Day}/{Session}/{Number}";
}

/// <summary>O que o Atom exporta (formato atom-takes/1, ver CONTRATO.md).</summary>
public sealed record TakesExport(string Format, string Atom, double Fps, IReadOnlyList<Take> Takes);

public static class TakesLoader
{
    public const string Format = "atom-takes/";
    private const string Fmt = "yyyy-MM-dd HH:mm:ss";

    public static TakesExport FromFile(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        return path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? FromCsv(text) : FromJson(text);
    }

    public static TakesExport FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var format = Str(root, "format");
        if (!format.StartsWith(Format + "1", StringComparison.Ordinal))
            throw new FormatException($"Unknown takes format '{format}' (expected {Format}1)");
        var fps = root.TryGetProperty("fps", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetDouble() : 25;
        var takes = new List<Take>();
        foreach (var t in root.GetProperty("takes").EnumerateArray())
        {
            var cams = new List<TakeCamera>();
            foreach (var c in t.GetProperty("cameras").EnumerateArray())
                cams.Add(new TakeCamera(Str(c, "source"), Str(c, "alias"), Time(c, "start"), Time(c, "stop"), NullIfEmpty(Str(c, "tc_start")),
                    NullIfEmpty(Str(c, "tc_stop")), Str(c, "clip"), c.TryGetProperty("fault", out var fl) && fl.ValueKind == JsonValueKind.True));
            var notes = new List<string>();
            if (t.TryGetProperty("notes", out var ns))
                foreach (var n in ns.EnumerateArray())
                {
                    var txt = Str(n, "text");
                    if (txt.Length > 0) notes.Add(txt);
                }
            takes.Add(new Take(Str(t, "day"), Str(t, "session"), t.GetProperty("take").GetInt32(), Time(t, "start") ?? DateTime.MinValue,
                Time(t, "end"), t.TryGetProperty("open", out var o) && o.ValueKind == JsonValueKind.True, NullIfEmpty(Str(t, "verdict")),
                NullIfEmpty(Str(t, "tc_start")), cams, notes));
        }
        return new TakesExport(format, Str(root, "atom"), fps, takes);
    }

    /// <summary>O CSV do Atom (uma linha por take e camara): day,session,take,camera,alias,start,stop,tc_start,tc_stop,clip,fault,verdict,notes.</summary>
    public static TakesExport FromCsv(string csv)
    {
        var rows = ParseCsv(csv.TrimStart('﻿'));
        if (rows.Count == 0) throw new FormatException("Empty takes file");
        var head = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        foreach (var need in new[] { "day", "take", "camera", "start" })
            if (!head.Contains(need)) throw new FormatException($"Takes CSV without the '{need}' column");
        string Col(List<string> r, string name)
        {
            var i = head.IndexOf(name);
            return i >= 0 && i < r.Count ? r[i] : "";
        }
        var groups = new Dictionary<(string, string, int), (List<TakeCamera> Cams, string Verdict, string Notes)>();
        var order = new List<(string, string, int)>();
        foreach (var r in rows.Skip(1))
        {
            if (r.All(string.IsNullOrWhiteSpace)) continue;
            if (!int.TryParse(Col(r, "take"), NumberStyles.None, CultureInfo.InvariantCulture, out var number)) continue;
            var key = (Col(r, "day"), Col(r, "session"), number);
            if (!groups.TryGetValue(key, out var g))
            {
                g = (new List<TakeCamera>(), Col(r, "verdict"), Col(r, "notes"));
                groups[key] = g;
                order.Add(key);
            }
            g.Cams.Add(new TakeCamera(Col(r, "camera"), Col(r, "alias"), Parse(Col(r, "start")), Parse(Col(r, "stop")),
                NullIfEmpty(Col(r, "tc_start")), NullIfEmpty(Col(r, "tc_stop")), Col(r, "clip"), Col(r, "fault") == "1"));
        }
        var takes = new List<Take>();
        foreach (var key in order)
        {
            var (cams, verdict, notes) = groups[key];
            var starts = cams.Where(c => c.Start != null).Select(c => c.Start!.Value).ToList();
            var stops = cams.Where(c => c.Stop != null).Select(c => c.Stop!.Value).ToList();
            var open = cams.Any(c => c.Start != null && c.Stop == null);
            takes.Add(new Take(key.Item1, key.Item2, key.Item3, starts.Count > 0 ? starts.Min() : DateTime.MinValue,
                open || stops.Count == 0 ? null : stops.Max(), open, NullIfEmpty(verdict), cams.FirstOrDefault(c => c.TcStart != null)?.TcStart,
                cams, notes.Length > 0 ? new[] { notes } : Array.Empty<string>()));
        }
        // O fps do Atom (a mesma em todas as linhas). Um CSV de antes da coluna: 25, o que o Atom usa por omissao.
        var fps = rows.Skip(1).Select(r => Col(r, "fps"))
            .Select(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) && x > 0 && x <= 120 ? x : 0)
            .FirstOrDefault(x => x > 0);
        return new TakesExport(Format + "1", "", fps > 0 ? fps : 25, takes.OrderBy(t => t.Start).ThenBy(t => t.Number).ToList());
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static DateTime? Time(JsonElement e, string name) => Parse(Str(e, name));

    private static DateTime? Parse(string s) =>
        DateTime.TryParseExact(s, Fmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    /// <summary>CSV (RFC 4180): campos entre aspas com virgulas, aspas duplas e mudancas de linha.</summary>
    public static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString()); field.Clear();
                rows.Add(row); row = new List<string>();
            }
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows;
    }
}
