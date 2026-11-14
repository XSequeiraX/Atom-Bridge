using System.Text.Json;

namespace Proton.Core;

/// <summary>Um cartao (ou pasta) e a camara a que pertence.</summary>
public sealed class CardSource
{
    public CardSource(string path) { Path = path; }
    public string Path { get; }
    /// <summary>A camara (o que o operador escolheu); null enquanto nao escolheu.</summary>
    public string? Camera { get; set; }
    public IReadOnlyList<MediaFile> Files { get; set; } = Array.Empty<MediaFile>();
}

/// <summary>As definicoes que ficam de um dia para o outro (%AppData%/Proton ou ~/.config/Proton).</summary>
public sealed class ProtonSettings
{
    public string AtomAddress { get; set; } = "";
    public string Destination { get; set; } = "";
    public string Template { get; set; } = PathTemplate.Default;
    public int Parallel { get; set; } = 4;

    public static string DefaultPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Proton", "settings.json");

    public static ProtonSettings Load(string? path = null)
    {
        try { return JsonSerializer.Deserialize<ProtonSettings>(File.ReadAllText(path ?? DefaultPath)) ?? new ProtonSettings(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new ProtonSettings(); }
    }

    public void Save(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* as definicoes nao sao essenciais */ }
    }
}

/// <summary>Procura o export do Atom numa pen (AtomBridge/atom-takes-*.json), no Windows e no Mac.</summary>
public static class UsbDiscovery
{
    public static IEnumerable<string> SystemRoots()
    {
        var roots = new List<string>();
        // Sem as unidades de rede: uma desligada no Windows pode deixar o IsReady a espera.
        try { roots.AddRange(DriveInfo.GetDrives().Where(d => d.DriveType != DriveType.Network && d.IsReady).Select(d => d.RootDirectory.FullName)); }
        catch (IOException) { }
        foreach (var parent in new[] { "/Volumes", "/media", "/run/media" })
        {
            if (!Directory.Exists(parent)) continue;
            try { roots.AddRange(Directory.EnumerateDirectories(parent)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return roots.Distinct();
    }

    /// <summary>Os exports encontrados, o mais recente primeiro.</summary>
    public static List<string> Find(IEnumerable<string>? roots = null)
    {
        var found = new List<string>();
        foreach (var root in roots ?? SystemRoots())
        {
            var dir = System.IO.Path.Combine(root, "AtomBridge");
            if (!Directory.Exists(dir)) continue;
            try { found.AddRange(Directory.EnumerateFiles(dir, "atom-takes-*.json")); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return found.OrderByDescending(f => File.GetLastWriteTimeUtc(f)).ToList();
    }
}

/// <summary>O estado de uma sessao do Proton: as takes, os cartoes, o destino, e os passos (cruzar, copiar, escrever os resultados).
/// A janela so mostra isto; a linha de comandos faz o mesmo sem janela.</summary>
public sealed class ProtonSession
{
    public TakesExport? Export { get; private set; }
    public string? TakesSource { get; private set; }
    public List<CardSource> Cards { get; } = new();
    public string Destination { get; set; } = "";
    public string Template { get; set; } = PathTemplate.Default;
    public int Parallel { get; set; } = 4;
    public MatchResult? Result { get; private set; }
    public IReadOnlyList<CopyResult> Copied { get; private set; } = Array.Empty<CopyResult>();

    /// <summary>As camaras de que as takes falam (para o operador escolher de que camara e cada cartao).</summary>
    public IReadOnlyList<string> CameraNames =>
        Export == null ? Array.Empty<string>()
            : Export.Takes.SelectMany(t => t.Cameras).Select(c => c.Label).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();

    public async Task LoadTakesAsync(string source, string? day = null, CancellationToken ct = default)
    {
        Export = await AtomClient.LoadAsync(source, day, ct).ConfigureAwait(false);
        TakesSource = source;
        Result = null;
    }

    public CardSource AddCard(string folder, string? camera = null)
    {
        var card = new CardSource(folder) { Camera = camera };
        Cards.Add(card);
        Rescan(card);
        return card;
    }

    public void Rescan(CardSource card)
    {
        card.Files = Directory.Exists(card.Path) ? MediaScanner.Scan(card.Path, card.Camera) : Array.Empty<MediaFile>();
    }

    /// <summary>Um cartao sem camara escolhida: avisa-se antes de cruzar (um ficheiro sem camara serve a qualquer uma).</summary>
    public IEnumerable<CardSource> CardsWithoutCamera => Cards.Where(c => string.IsNullOrWhiteSpace(c.Camera));

    public MatchResult Match()
    {
        if (Export == null) throw new InvalidOperationException("Load the takes first");
        foreach (var c in Cards) Rescan(c);                       // a camara pode ter mudado
        var files = Cards.SelectMany(c => c.Files).ToList();
        Result = Matcher.Run(Export.Takes, files, new MatchOptions { DefaultFps = Export.Fps }, Export.Fps);
        return Result;
    }

    /// <summary>O destino de cada ficheiro: por take, ou em _unmatched/ se nao se reconhece.</summary>
    public List<CopyItem> PlanCopy()
    {
        if (string.IsNullOrWhiteSpace(Destination)) throw new InvalidOperationException("Choose the destination folder");
        var result = Result ?? Match();
        var items = new List<CopyItem>();
        var planned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);        // o Windows e o Mac nao distinguem maiusculas
        foreach (var f in Cards.SelectMany(c => c.Files))
        {
            var m = result.Matches.FirstOrDefault(x => string.Equals(x.File.Path, f.Path, StringComparison.OrdinalIgnoreCase));
            var rel = m != null ? PathTemplate.Render(Template, m.Take, m.Camera, f) : PathTemplate.Render(PathTemplate.Unmatched, null, null, f);
            var dest = System.IO.Path.Combine(Destination, rel);
            // Dois ficheiros com o mesmo destino (dois MVI_0001.MP4 de cartoes diferentes): o segundo ganha _2, _3...
            var stem = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(dest)!, System.IO.Path.GetFileNameWithoutExtension(dest));
            var ext = System.IO.Path.GetExtension(dest);
            for (var n = 2; !planned.Add(dest); n++) dest = $"{stem}_{n}{ext}";
            items.Add(new CopyItem(f.Path, dest));
        }
        return items;
    }

    public async Task<IReadOnlyList<CopyResult>> CopyAsync(IProgress<CopyProgress>? progress = null, CancellationToken ct = default)
    {
        var plan = PlanCopy();
        Copied = await Copier.CopyAllAsync(plan, Parallel, progress, ct).ConfigureAwait(false);
        Directory.CreateDirectory(Destination);
        Copier.WriteManifest(System.IO.Path.Combine(Destination, "proton_manifest.csv"), Copied);
        WriteOutputs(Destination);
        return Copied;
    }

    /// <summary>resolve.csv e report.txt para uma pasta.</summary>
    public void WriteOutputs(string folder)
    {
        if (Result == null || Export == null) return;
        Directory.CreateDirectory(folder);
        File.WriteAllText(System.IO.Path.Combine(folder, "resolve.csv"), ResolveCsv.Build(Result.Matches));
        File.WriteAllText(System.IO.Path.Combine(folder, "report.txt"), GapReport.Build(Result, Export.Takes.Count));
        File.WriteAllText(System.IO.Path.Combine(folder, "review.csv"), Review().ToCsv());
    }

    /// <summary>O shoot log com os ficheiros associados e a prova de cada um (a janela Review e o review.csv).</summary>
    public MatchReview Review()
    {
        if (Export == null || Result == null) throw new InvalidOperationException("Press Match first");
        return MatchReview.Build(Export, Result);
    }

    public string Summary() =>
        Result == null || Export == null ? "" :
        $"{Export.Takes.Count} takes   {Result.Matches.Count} matched   {Result.Missing.Count} missing   {Result.Unmatched.Count} unmatched   " +
        $"{Result.Matches.Count(m => m.Doubtful)} doubtful";
}
