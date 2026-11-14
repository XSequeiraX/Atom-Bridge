namespace Proton.Core;

/// <summary>A data nos user bits do timecode (SMPTE 309M), lida como o Atom a le (ltc_audio.date_from_user_bits).
/// Oito digitos hex = quatro bytes BCD: fuso, ano, mes, dia, com as unidades antes das dezenas (o LTC manda o bit menos
/// significativo primeiro). Confirmado contra o Deity TC-1: "00629070" = 2026-09-07.
/// Um gravador pode escrever os oito digitos pela ordem do LTC ou pela ordem do ecra (a inversa): tentam-se as duas.</summary>
public static class UserBits
{
    /// <summary>A data, ou null. `hint` (a data que o ficheiro diz ter) desempata quando as duas ordens dao datas validas e diferentes;
    /// sem ela, nesse caso, nao se adivinha.</summary>
    public static DateOnly? Date(string? userBits, DateTime? hint = null)
    {
        var ub = Normalize(userBits);
        if (ub == null) return null;
        var asSent = Decode(ub);
        var asShown = Decode(new string(ub.Reverse().ToArray()));
        if (asSent == null) return asShown;
        if (asShown == null || asShown == asSent) return asSent;
        if (hint == null) return null;                                              // as duas sao datas: sem pista, nao se escolhe
        var h = DateOnly.FromDateTime(hint.Value);
        var dSent = Math.Abs(asSent.Value.DayNumber - h.DayNumber);
        var dShown = Math.Abs(asShown.Value.DayNumber - h.DayNumber);
        return dSent == dShown ? null : dSent < dShown ? asSent : asShown;
    }

    /// <summary>Oito digitos hex (sem espacos, pontos ou dois pontos), ou null. "00000000" e "FFFFFFFF" nao sao uma data.</summary>
    public static string? Normalize(string? userBits)
    {
        if (string.IsNullOrWhiteSpace(userBits)) return null;
        var s = new string(userBits.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        return s.Length == 8 && s != "00000000" && s != "FFFFFFFF" ? s : null;
    }

    private static DateOnly? Decode(string ub)
    {
        var n = ub.Select(c => Convert.ToInt32(c.ToString(), 16)).ToArray();
        if (n.Any(d => d > 9)) return null;                                          // BCD nao tem digitos acima de 9
        int Byte(int i) => n[i * 2 + 1] * 10 + n[i * 2];
        int yy = Byte(1), mm = Byte(2), dd = Byte(3);
        if (mm < 1 || mm > 12 || dd < 1 || dd > DateTime.DaysInMonth(2000 + yy, mm)) return null;
        return new DateOnly(2000 + yy, mm, dd);
    }
}
