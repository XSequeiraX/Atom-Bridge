using System.Buffers.Binary;
using System.Text;

namespace Proton.Core;

/// <summary>MOV / MP4 (Canon, Sony, GoPro, telemoveis, BMPCC em ProRes): duracao, hora de gravacao e o timecode da pista `tmcd`.</summary>
public sealed class IsoBmffReader : IMediaReader
{
    private static readonly DateTime Epoch1904 = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
    public bool CanRead(string extension) => extension is ".mov" or ".mp4" or ".m4v";

    private sealed class Track
    {
        public string Handler = "";
        public bool Tmcd;
        public bool Drop;
        public double TcFps;
        public int NominalFps;
        public long FirstChunk = -1;
    }

    public MediaFile Read(string path)
    {
        var fi = new FileInfo(path);
        var basic = new MediaFile(path, MediaKind.Video, fi.Length, fi.CreationTime, fi.LastWriteTime);
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            TimeSpan? duration = null;
            DateTime? recorded = null;
            var tracks = new List<Track>();
            var found = false;
            foreach (var (type, data, end) in Boxes(fs, 0, fs.Length))
            {
                if (type != "moov") continue;
                found = true;
                foreach (var (t2, d2, e2) in Boxes(fs, data, end))
                {
                    if (t2 == "mvhd")
                    {
                        var b = ReadAt(fs, d2, 32);
                        var v = b[0];
                        long created, dur;
                        uint scale;
                        if (v == 1)
                        {
                            created = (long)BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(4));
                            scale = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(20));
                            dur = (long)BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(24));
                        }
                        else
                        {
                            created = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(4));
                            scale = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(12));
                            dur = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(16));
                        }
                        if (scale > 0) duration = TimeSpan.FromSeconds((double)dur / scale);
                        if (created > 0 && created < 4_000_000_000L) recorded = Epoch1904.AddSeconds(created);
                    }
                    else if (t2 == "trak") tracks.Add(ReadTrack(fs, d2, e2));
                }
            }
            if (!found) return basic with { Note = "not a MOV/MP4 file (no moov box)" };

            double? startSeconds = null, fps = null;
            var tm = tracks.FirstOrDefault(t => t.Tmcd && t.FirstChunk >= 0 && t.NominalFps > 0);
            if (tm != null)
            {
                var raw = ReadAt(fs, tm.FirstChunk, 4);
                var frames = BinaryPrimitives.ReadUInt32BigEndian(raw);
                fps = tm.TcFps > 0 ? tm.TcFps : tm.NominalFps;
                startSeconds = FramesToSeconds(frames, tm.NominalFps, tm.Drop);
            }
            return basic with
            {
                Duration = duration, StartSeconds = startSeconds, Fps = fps, RecordedAt = recorded,
                Note = startSeconds == null ? "no timecode track in this file" : null,
            };
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or UnauthorizedAccessException or ArgumentException)
        {
            return basic with { Note = "could not read: " + e.Message };
        }
    }

    /// <summary>O contador de imagens da pista tmcd em segundos desde a meia-noite (drop-frame de 29,97 incluido).</summary>
    public static double FramesToSeconds(uint frames, int nominalFps, bool drop)
    {
        long fn = frames;
        if (drop && nominalFps == 30)
        {
            var d = fn / 17982;
            var m = fn % 17982;
            fn += 18 * d + (m > 2 ? 2 * ((m - 2) / 1798) : 0);
        }
        var ff = fn % nominalFps;
        var total = fn / nominalFps;
        return total % 86400 + (double)ff / nominalFps;
    }

    private static Track ReadTrack(Stream s, long start, long end)
    {
        var t = new Track();
        foreach (var (t1, d1, e1) in Boxes(s, start, end))
        {
            if (t1 != "mdia") continue;
            foreach (var (t2, d2, e2) in Boxes(s, d1, e1))
            {
                if (t2 == "hdlr")
                {
                    var b = ReadAt(s, d2, 12);
                    t.Handler = Encoding.ASCII.GetString(b, 8, 4);
                    t.Tmcd = t.Handler == "tmcd";
                }
                else if (t2 == "minf")
                    foreach (var (t3, d3, e3) in Boxes(s, d2, e2))
                    {
                        if (t3 != "stbl") continue;
                        foreach (var (t4, d4, _) in Boxes(s, d3, e3))
                        {
                            if (t4 == "stsd" && t.Tmcd)
                            {
                                var b = ReadAt(s, d4, 8 + 33);
                                // 8 de versao/contagem, depois a entrada: tamanho(4) formato(4) reservado(6) ref(2) | reservado(4)
                                // flags(4) escala(4) duracao-da-imagem(4) imagens-por-segundo(1)
                                var e = 8;
                                t.Drop = (BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(e + 20)) & 1) != 0;
                                var scale = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(e + 24));
                                var fd = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(e + 28));
                                t.NominalFps = b[e + 32];
                                t.TcFps = fd > 0 ? (double)scale / fd : 0;
                            }
                            else if (t4 == "stco" && t.Tmcd)
                            {
                                var b = ReadAt(s, d4, 12);
                                if (BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(4)) > 0) t.FirstChunk = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(8));
                            }
                            else if (t4 == "co64" && t.Tmcd)
                            {
                                var b = ReadAt(s, d4, 16);
                                if (BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(4)) > 0) t.FirstChunk = (long)BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(8));
                            }
                        }
                    }
            }
        }
        return t;
    }

    private static byte[] ReadAt(Stream s, long pos, int count)
    {
        var b = new byte[count];
        s.Seek(pos, SeekOrigin.Begin);
        s.ReadExactly(b, 0, count);
        return b;
    }

    /// <summary>As caixas de um intervalo: (tipo, inicio do conteudo, fim da caixa).</summary>
    private static IEnumerable<(string Type, long Data, long End)> Boxes(Stream s, long start, long end)
    {
        var pos = start;
        var hdr = new byte[16];
        while (pos + 8 <= end)
        {
            s.Seek(pos, SeekOrigin.Begin);
            if (s.Read(hdr, 0, 8) < 8) yield break;
            long size = BinaryPrimitives.ReadUInt32BigEndian(hdr);
            var type = Encoding.Latin1.GetString(hdr, 4, 4);
            long header = 8;
            if (size == 1)
            {
                if (s.Read(hdr, 8, 8) < 8) yield break;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(hdr.AsSpan(8));
                header = 16;
            }
            else if (size == 0) size = end - pos;
            if (size < header || pos + size > end) yield break;
            yield return (type, pos + header, pos + size);
            pos += size;
        }
    }
}
