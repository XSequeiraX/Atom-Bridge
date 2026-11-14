using System.Globalization;

namespace Proton.Core;

/// <summary>Um timecode HH:MM:SS:FF. So non-drop: o Atom escreve sempre assim (o ';' do drop-frame le-se mas conta como ':').</summary>
public readonly record struct Timecode(int Hours, int Minutes, int Seconds, int Frames)
{
    public static bool TryParse(string? text, out Timecode tc)
    {
        tc = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var p = text.Trim().Replace(';', ':').Split(':');
        if (p.Length != 4) return false;
        var n = new int[4];
        for (var i = 0; i < 4; i++)
            if (!int.TryParse(p[i], NumberStyles.None, CultureInfo.InvariantCulture, out n[i])) return false;
        if (n[1] > 59 || n[2] > 59) return false;
        tc = new Timecode(n[0], n[1], n[2], n[3]);
        return true;
    }

    /// <summary>Segundos desde a meia-noite (o fps converte as imagens).</summary>
    public double ToSeconds(double fps) => Hours * 3600 + Minutes * 60 + Seconds + (fps > 0 ? Frames / fps : 0);

    public static Timecode FromSeconds(double seconds, double fps)
    {
        var f = fps > 0 ? fps : 25;
        var frames = (long)Math.Round(seconds * f);
        var nominal = (int)Math.Round(f);
        var ff = (int)(frames % nominal);
        var total = frames / nominal;
        return new Timecode((int)(total / 3600 % 24), (int)(total / 60 % 60), (int)(total % 60), ff);
    }

    public override string ToString() => $"{Hours:00}:{Minutes:00}:{Seconds:00}:{Frames:00}";
}
