using System.Buffers.Binary;
using System.Text;

namespace Proton.Tests;

/// <summary>Ficheiros sinteticos para os leitores: um BWF com bext e iXML, e um MOV com pista de timecode.</summary>
public static class Builders
{
    public static byte[] Wav(uint rate, double seconds, ulong timeRef, string? ixml = null, string date = "2026-11-09", string time = "10:00:01")
    {
        var data = new byte[(int)(rate * 2 * seconds)];
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(0u); w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16u);
        w.Write((ushort)1); w.Write((ushort)1); w.Write(rate); w.Write(rate * 2); w.Write((ushort)2); w.Write((ushort)16);
        var bext = new byte[346];
        Encoding.ASCII.GetBytes(date).CopyTo(bext, 320);
        Encoding.ASCII.GetBytes(time).CopyTo(bext, 330);
        BitConverter.GetBytes((uint)(timeRef & 0xFFFFFFFF)).CopyTo(bext, 338);
        BitConverter.GetBytes((uint)(timeRef >> 32)).CopyTo(bext, 342);
        w.Write(Encoding.ASCII.GetBytes("bext")); w.Write((uint)bext.Length); w.Write(bext);
        if (ixml != null)
        {
            var b = Encoding.UTF8.GetBytes(ixml);
            w.Write(Encoding.ASCII.GetBytes("iXML")); w.Write((uint)b.Length); w.Write(b);
            if (b.Length % 2 == 1) w.Write((byte)0);
        }
        w.Write(Encoding.ASCII.GetBytes("data")); w.Write((uint)data.Length); w.Write(data);
        w.Flush();
        var all = ms.ToArray();
        BitConverter.GetBytes((uint)(all.Length - 8)).CopyTo(all, 4);
        return all;
    }

    private static byte[] Box(string type, params byte[][] parts)
    {
        var payload = parts.SelectMany(p => p).ToArray();
        var b = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)b.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(b, 4);
        payload.CopyTo(b, 8);
        return b;
    }

    private static byte[] U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); return b; }

    /// <summary>Um MOV minimo: mdat com o contador do timecode, moov com mvhd e uma pista tmcd.</summary>
    public static byte[] Mov(uint timecodeFrames, int fps, uint durationSeconds, uint createdSince1904, bool drop = false)
    {
        var ftyp = Box("ftyp", Encoding.ASCII.GetBytes("qt  "), U32(0), Encoding.ASCII.GetBytes("qt  "));
        var mdat = Box("mdat", U32(timecodeFrames));
        var counterOffset = (uint)(ftyp.Length + 8);
        var mvhd = Box("mvhd", new byte[] { 0, 0, 0, 0 }, U32(createdSince1904), U32(createdSince1904), U32(1000), U32(durationSeconds * 1000), new byte[80]);
        var hdlr = Box("hdlr", new byte[8], Encoding.ASCII.GetBytes("tmcd"), new byte[13]);
        var entry = new byte[34];
        BinaryPrimitives.WriteUInt32BigEndian(entry, 34);
        Encoding.ASCII.GetBytes("tmcd").CopyTo(entry, 4);
        entry[15] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(entry.AsSpan(20), drop ? 1u : 0u);
        BinaryPrimitives.WriteUInt32BigEndian(entry.AsSpan(24), (uint)(drop ? 30000 : fps));
        BinaryPrimitives.WriteUInt32BigEndian(entry.AsSpan(28), (uint)(drop ? 1001 : 1));
        entry[32] = (byte)fps;
        var stsd = Box("stsd", new byte[4], U32(1), entry);
        var stco = Box("stco", new byte[4], U32(1), U32(counterOffset));
        var trak = Box("trak", Box("mdia", hdlr, Box("minf", Box("stbl", stsd, stco))));
        return ftyp.Concat(mdat).Concat(Box("moov", mvhd, trak)).ToArray();
    }
}
