using System.Net.Http;

namespace Proton.Core;

/// <summary>Vai buscar as takes ao Atom (GET /api/log/takes) ou le um ficheiro exportado: a pen exFAT, quando nao ha rede.</summary>
public static class AtomClient
{
    /// <param name="source">http://atom:8080 (o endereco do Atom, ex. pelo Tailscale) ou o caminho de um .json / .csv exportado.</param>
    public static async Task<TakesExport> LoadAsync(string source, string? day = null, CancellationToken ct = default)
    {
        if (!source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return TakesLoader.FromFile(source);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var url = source.TrimEnd('/') + "/api/log/takes" + (string.IsNullOrEmpty(day) ? "" : "?day=" + Uri.EscapeDataString(day));
        var json = await http.GetStringAsync(url, ct).ConfigureAwait(false);
        return TakesLoader.FromJson(json);
    }
}
