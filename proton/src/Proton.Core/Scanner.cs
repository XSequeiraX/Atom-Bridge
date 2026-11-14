namespace Proton.Core;

/// <summary>Percorre uma pasta (ou um cartao) e le o que cada ficheiro de media diz de si.</summary>
public static class MediaScanner
{
    private static readonly string[] VideoExt = { ".mov", ".mp4", ".m4v", ".mxf", ".braw", ".r3d", ".crm", ".mts", ".avi" };
    private static readonly string[] AudioExt = { ".wav", ".bwf" };

    public static IReadOnlyList<IMediaReader> DefaultReaders { get; } = new IMediaReader[] { new WavReader(), new IsoBmffReader() };

    /// <param name="folder">A pasta ou o cartao.</param>
    /// <param name="camera">A camara a que pertence (o que o operador disse); fica em cada ficheiro.</param>
    public static List<MediaFile> Scan(string folder, string? camera = null, IEnumerable<IMediaReader>? readers = null)
    {
        var rs = (readers ?? DefaultReaders).ToList();
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
        var list = new List<MediaFile>();
        foreach (var path in Directory.EnumerateFiles(folder, "*", opts).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (Path.GetFileName(path).StartsWith("._", StringComparison.Ordinal)) continue;       // lixo do macOS em exFAT
            var reader = rs.FirstOrDefault(r => r.CanRead(ext));
            MediaFile f;
            if (reader != null)
            {
                // Um ficheiro estragado no cartao nao pode parar a leitura dos outros: os leitores tratam os casos conhecidos e
                // isto apanha o resto (fica na lista, sem metadados, com o motivo).
                try { f = reader.Read(path); }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    var fi = new FileInfo(path);
                    f = new MediaFile(path, AudioExt.Contains(ext) ? MediaKind.Audio : MediaKind.Video, fi.Length, fi.CreationTime, fi.LastWriteTime)
                    {
                        Note = "could not read: " + e.Message,
                    };
                }
            }
            else if (VideoExt.Contains(ext) || AudioExt.Contains(ext))
            {
                var fi = new FileInfo(path);
                f = new MediaFile(path, AudioExt.Contains(ext) ? MediaKind.Audio : MediaKind.Video, fi.Length, fi.CreationTime, fi.LastWriteTime)
                {
                    Note = ext == ".braw" ? "BRAW: timecode not read yet (matched by name or file time)" : "no reader for " + ext + " (matched by name or file time)",
                };
            }
            else continue;
            list.Add(camera == null ? f : f with { Camera = camera });
        }
        return list;
    }
}
