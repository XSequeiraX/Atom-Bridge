using System.IO.Hashing;
using System.Text;

namespace Proton.Core;

public sealed record CopyItem(string Source, string Dest);
public sealed record CopyResult(CopyItem Item, bool Ok, bool Skipped, long Bytes, string? Hash, string? Error)
{
    /// <summary>A verificacao releu a copia do disco (e nao so da memoria do sistema): ver DiskRead.</summary>
    public bool FromDisk { get; init; }
}
public sealed record CopyProgress(string File, long Done, long Total);

/// <summary>Copia varios ficheiros ao mesmo tempo com verificacao: calcula o xxHash3 enquanto le a origem, escreve para um ficheiro
/// temporario, volta a ler o destino do disco (sem a cache do sistema: DiskRead) e so se os dois forem iguais e que o ficheiro ganha o
/// nome final. Mantem as datas (o cruzamento pela hora precisa delas).</summary>
public static class Copier
{
    public const string PartSuffix = ".proton-part";
    private const int Buffer = 4 * 1024 * 1024;

    public static async Task<List<CopyResult>> CopyAllAsync(IReadOnlyList<CopyItem> items, int parallel = 4, IProgress<CopyProgress>? progress = null,
        CancellationToken ct = default)
    {
        var results = new CopyResult[items.Count];
        using var gate = new SemaphoreSlim(Math.Max(1, parallel));
        var tasks = items.Select(async (item, i) =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try { results[i] = await Task.Run(() => CopyOne(item, progress, ct), ct).ConfigureAwait(false); }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.ToList();
    }

    public static CopyResult CopyOne(CopyItem item, IProgress<CopyProgress>? progress = null, CancellationToken ct = default)
    {
        var part = item.Dest + PartSuffix;
        try
        {
            var src = new FileInfo(item.Source);
            if (!src.Exists) return new CopyResult(item, false, false, 0, null, "source not found");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(item.Dest))!);

            if (File.Exists(item.Dest))
            {
                if (new FileInfo(item.Dest).Length == src.Length)
                {
                    var (there, disk) = DiskRead.Hash(item.Dest, ct);
                    if (there == HashFile(item.Source, ct))
                        return new CopyResult(item, true, true, src.Length, there, null) { FromDisk = disk };     // ja copiado e igual
                }
                return new CopyResult(item, false, false, 0, null, "the destination already has a different file");
            }

            var hash = new XxHash3();
            var buf = new byte[Buffer];
            long done = 0;
            using (var input = new FileStream(item.Source, FileMode.Open, FileAccess.Read, FileShare.Read, Buffer, FileOptions.SequentialScan))
            using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, Buffer, FileOptions.SequentialScan))
            {
                DiskRead.PrepareWrite(output.SafeFileHandle);
                int n;
                while ((n = input.Read(buf, 0, buf.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    hash.Append(buf.AsSpan(0, n));
                    output.Write(buf, 0, n);
                    done += n;
                    progress?.Report(new CopyProgress(item.Source, done, src.Length));
                }
                output.Flush(true);                              // para o disco, nao so para a cache
                DiskRead.FinishWrite(output.SafeFileHandle);
            }
            var written = Convert.ToHexString(hash.GetCurrentHash());
            var (readBack, fromDisk) = DiskRead.Hash(part, ct);
            if (readBack != written)
            {
                File.Delete(part);
                return new CopyResult(item, false, false, done, written, "verification failed: the copy differs from the source") { FromDisk = fromDisk };
            }
            File.SetCreationTime(part, src.CreationTime);
            File.SetLastWriteTime(part, src.LastWriteTime);
            File.Move(part, item.Dest);
            return new CopyResult(item, true, false, done, written, null) { FromDisk = fromDisk };
        }
        catch (OperationCanceledException)
        {
            TryDelete(part);
            return new CopyResult(item, false, false, 0, null, "cancelled");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TryDelete(part);
            return new CopyResult(item, false, false, 0, null, e.Message);
        }
    }

    public static string HashFile(string path, CancellationToken ct = default)
    {
        var hash = new XxHash3();
        var buf = new byte[Buffer];
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, Buffer, FileOptions.SequentialScan);
        int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            hash.Append(buf.AsSpan(0, n));
        }
        return Convert.ToHexString(hash.GetCurrentHash());
    }

    /// <summary>O manifesto da copia (o que se copiou e o hash de cada ficheiro), para conferir mais tarde.</summary>
    public static void WriteManifest(string path, IEnumerable<CopyResult> results)
    {
        var sb = new StringBuilder("source,dest,bytes,xxh3,status,verified\n");
        foreach (var r in results)
            sb.Append('"').Append(r.Item.Source.Replace("\"", "\"\"")).Append("\",\"").Append(r.Item.Dest.Replace("\"", "\"\"")).Append("\",")
              .Append(r.Bytes).Append(',').Append(r.Hash ?? "").Append(',').Append(r.Ok ? (r.Skipped ? "already-copied" : "ok") : "FAILED: " + r.Error)
              .Append(',').Append(r.Hash == null ? "" : r.FromDisk ? "disk" : "memory").Append('\n');
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static void TryDelete(string p)
    {
        try { if (File.Exists(p)) File.Delete(p); } catch (IOException) { }
    }
}
