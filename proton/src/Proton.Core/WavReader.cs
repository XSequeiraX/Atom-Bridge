using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Proton.Core;

/// <summary>WAV e BWF (F6, DJI Mic...): duracao, timecode de inicio (bext TimeReference ou iXML), hora de gravacao, cena e take (iXML).</summary>
public sealed class WavReader : IMediaReader
{
    public bool CanRead(string extension) => extension is ".wav" or ".bwf";

    public MediaFile Read(string path)
    {
        var fi = new FileInfo(path);
        var basic = new MediaFile(path, MediaKind.Audio, fi.Length, fi.CreationTime, fi.LastWriteTime);
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var head = new byte[12];
            if (fs.Read(head, 0, 12) < 12) return basic with { Note = "not a WAV file" };
            var riff = Encoding.ASCII.GetString(head, 0, 4);
            if ((riff != "RIFF" && riff != "RF64") || Encoding.ASCII.GetString(head, 8, 4) != "WAVE")
                return basic with { Note = "not a WAV file" };

            uint rate = 0, byteRate = 0;
            long dataSize = -1, ds64Data = -1, dataStart = 0;
            ulong timeRef = 0;
            DateTime? recorded = null;
            string? ixml = null;
            var hdr = new byte[8];
            while (fs.Position + 8 <= fs.Length && fs.Read(hdr, 0, 8) == 8)
            {
                var id = Encoding.ASCII.GetString(hdr, 0, 4);
                var size = BitConverter.ToUInt32(hdr, 4);
                var start = fs.Position;
                long len = size;
                if (id == "ds64" && size >= 24)
                {
                    var b = new byte[24];
                    fs.ReadExactly(b, 0, 24);
                    ds64Data = (long)BitConverter.ToUInt64(b, 8);
                }
                else if (id == "fmt " && size >= 16)
                {
                    var b = new byte[16];
                    fs.ReadExactly(b, 0, 16);
                    rate = BitConverter.ToUInt32(b, 4);
                    byteRate = BitConverter.ToUInt32(b, 8);
                }
                else if (id == "bext" && size >= 346)
                {
                    var b = new byte[346];
                    fs.ReadExactly(b, 0, 346);
                    var date = Encoding.ASCII.GetString(b, 320, 10).Trim('\0', ' ');
                    var time = Encoding.ASCII.GetString(b, 330, 8).Trim('\0', ' ');
                    if (DateTime.TryParseExact(date + " " + time.Replace('-', ':'), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out var d)) recorded = d;
                    timeRef = BitConverter.ToUInt32(b, 338) | ((ulong)BitConverter.ToUInt32(b, 342) << 32);
                }
                else if (id == "iXML")
                {
                    var n = (int)Math.Min(size, 1_000_000);
                    var b = new byte[n];
                    fs.ReadExactly(b, 0, n);
                    ixml = Encoding.UTF8.GetString(b).Trim('\0', ' ', '\r', '\n');
                }
                else if (id == "data")
                {
                    dataStart = start;
                    dataSize = size == 0xFFFFFFFF && ds64Data >= 0 ? ds64Data : size;
                    len = dataSize;
                }
                var next = start + len + (len & 1);
                if (next <= start || next > fs.Length + 1) break;
                fs.Seek(Math.Min(next, fs.Length), SeekOrigin.Begin);
            }

            if (dataSize >= 0 && dataStart + dataSize > fs.Length) dataSize = fs.Length - dataStart;   // ainda a gravar, ou cortado
            TimeSpan? duration = dataSize >= 0 && byteRate > 0 ? TimeSpan.FromSeconds((double)dataSize / byteRate) : null;
            double? fps = null;
            string? scene = null, ubits = null;
            int? take = null;
            if (ixml != null)
            {
                scene = Tag(ixml, "SCENE");
                ubits = UserBits.Normalize(Tag(ixml, "UBITS"));
                if (int.TryParse(Tag(ixml, "TAKE"), NumberStyles.None, CultureInfo.InvariantCulture, out var tk)) take = tk;
                var tcRate = Tag(ixml, "TIMECODE_RATE");
                if (tcRate != null)
                {
                    var parts = tcRate.Split('/');
                    if (double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
                        fps = parts.Length == 2 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den) && den > 0 ? num / den : num;
                }
                if (timeRef == 0)
                {
                    var lo = Tag(ixml, "TIMESTAMP_SAMPLES_SINCE_MIDNIGHT_LO");
                    var hi = Tag(ixml, "TIMESTAMP_SAMPLES_SINCE_MIDNIGHT_HI");
                    if (uint.TryParse(lo, NumberStyles.None, CultureInfo.InvariantCulture, out var l))
                        timeRef = l | ((ulong)(uint.TryParse(hi, NumberStyles.None, CultureInfo.InvariantCulture, out var h) ? h : 0) << 32);
                }
            }
            double? startSeconds = timeRef > 0 && rate > 0 ? (double)timeRef / rate : null;
            return basic with
            {
                Duration = duration, StartSeconds = startSeconds, Fps = fps, RecordedAt = recorded, Scene = scene, TakeNo = take,
                UserBits = ubits, UserBitsDate = UserBits.Date(ubits, recorded),
                Note = startSeconds == null ? "no timecode in this file" : null,
            };
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or UnauthorizedAccessException)
        {
            return basic with { Note = "could not read: " + e.Message };
        }
    }

    private static string? Tag(string xml, string name)
    {
        var m = Regex.Match(xml, $"<{name}>\\s*([^<]*?)\\s*</{name}>", RegexOptions.IgnoreCase);
        return m.Success && m.Groups[1].Length > 0 ? m.Groups[1].Value : null;
    }
}
