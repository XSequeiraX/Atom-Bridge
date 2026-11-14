using System.IO.Hashing;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Proton.Core;

/// <summary>Ler um ficheiro do disco e nao da memoria. Depois de copiar, o sistema guarda os bytes que acabaram de ser escritos na
/// cache: relidos assim, so provam que a copia saiu certa da memoria, nao que o disco de destino a gravou bem. Aqui a releitura
/// vai ao disco:
///   Windows  o ficheiro abre-se sem cache (FILE_FLAG_NO_BUFFERING), com leituras alinhadas ao setor;
///   macOS    F_NOCACHE na escrita e na leitura (as paginas nunca ficam na cache) e F_FULLFSYNC no fim da escrita;
///   Linux    fsync e POSIX_FADV_DONTNEED (o sistema larga as paginas) antes de reler.
/// Se o destino nao deixa (uma unidade de rede, um sistema de ficheiros que recusa), le-se como antes e diz-se que foi da memoria.</summary>
public static class DiskRead
{
    private const int Chunk = 4 * 1024 * 1024;              // multiplo de 4096: serve setores de 512 e de 4K
    private const int Align = 4096;
    private const FileOptions NoBuffering = (FileOptions)0x20000000;

    /// <summary>O xxHash3 do ficheiro e se a leitura foi mesmo do disco.</summary>
    public static (string Hash, bool FromDisk) Hash(string path, CancellationToken ct = default)
    {
        if (OperatingSystem.IsWindows())
        {
            try { return (HashUnbufferedWindows(path, ct), true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
            return (HashCached(path, ct, null), false);
        }
        var fromDisk = false;
        var hash = HashCached(path, ct, h =>
        {
            if (OperatingSystem.IsMacOS()) fromDisk = Mac.NoCache(h);
            else if (OperatingSystem.IsLinux()) fromDisk = Linux.DropCache(h);
        });
        return (hash, fromDisk);
    }

    /// <summary>Chama-se no ficheiro de destino logo depois de o abrir para escrita: no macOS as paginas escritas nao ficam na cache.</summary>
    public static void PrepareWrite(SafeFileHandle h)
    {
        if (OperatingSystem.IsMacOS()) Mac.NoCache(h);
    }

    /// <summary>Chama-se depois de escrever e do Flush(true): no macOS o fsync nao obriga o disco a gravar (F_FULLFSYNC obriga).</summary>
    public static void FinishWrite(SafeFileHandle h)
    {
        if (OperatingSystem.IsMacOS()) Mac.FullSync(h);
    }

    /// <summary>Se este sistema consegue ler do disco sem a cache (para os testes e para o que se diz ao operador).</summary>
    public static bool Supported => OperatingSystem.IsWindows() || (OperatingSystem.IsMacOS() && Mac.Works) || OperatingSystem.IsLinux();

    private static string HashCached(string path, CancellationToken ct, Action<SafeFileHandle>? prepare)
    {
        var hash = new XxHash3();
        var buf = new byte[Chunk];
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        prepare?.Invoke(s.SafeFileHandle);
        int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            hash.Append(buf.AsSpan(0, n));
        }
        return Convert.ToHexString(hash.GetCurrentHash());
    }

    private static string HashUnbufferedWindows(string path, CancellationToken ct)
    {
        // Sem cache, o Windows exige o endereco da memoria, o tamanho de cada leitura e a posicao alinhados ao setor. Um array fixo
        // (pinned) com folga para alinhar o inicio; le-se sempre Chunk inteiro e o fim do ficheiro devolve menos.
        var raw = GC.AllocateArray<byte>(Chunk + Align, pinned: true);
        var addr = Marshal.UnsafeAddrOfPinnedArrayElement(raw, 0).ToInt64();
        var skip = (int)((Align - addr % Align) % Align);
        var buf = raw.AsMemory(skip, Chunk);
        var hash = new XxHash3();
        using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, NoBuffering | FileOptions.SequentialScan);
        long pos = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var n = RandomAccess.Read(h, buf.Span, pos);
            if (n <= 0) break;
            hash.Append(buf.Span[..n]);
            pos += n;
            if (n < Chunk) break;
        }
        return Convert.ToHexString(hash.GetCurrentHash());
    }

    private static class Linux
    {
        [DllImport("libc", SetLastError = true)]
        private static extern int posix_fadvise(int fd, long offset, long len, int advice);

        [DllImport("libc", SetLastError = true)]
        private static extern int fdatasync(int fd);

        private const int DontNeed = 4;

        public static bool DropCache(SafeFileHandle h)
        {
            try
            {
                var fd = (int)h.DangerousGetHandle();
                fdatasync(fd);                                    // so se larga o que ja esta no disco
                return posix_fadvise(fd, 0, 0, DontNeed) == 0;
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return false; }
        }
    }

    private static class Mac
    {
        private const int F_DUPFD = 0, F_NOCACHE = 48, F_FULLFSYNC = 51;

        // O fcntl e variadico. No Apple Silicon os argumentos variadicos vao na pilha (nao nos registos): o terceiro argumento so
        // chega certo depois de seis de enchimento (os registos x2..x7). No Intel vao nos registos, como uma funcao normal.
        [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
        private static extern int fcntl_x64(int fd, int cmd, int arg);

        [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
        private static extern int fcntl_arm64(int fd, int cmd, long p2, long p3, long p4, long p5, long p6, long p7, long arg);

        [DllImport("libc", SetLastError = true)]
        private static extern int close(int fd);

        private static int Fcntl(int fd, int cmd, int arg) =>
            RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? fcntl_arm64(fd, cmd, 0, 0, 0, 0, 0, 0, arg) : fcntl_x64(fd, cmd, arg);

        /// <summary>Prova, uma vez, que o argumento chega ao fcntl: F_DUPFD com o minimo 100 tem de devolver um descritor &gt;= 100.
        /// Se nao chega, nada aqui se usa (e diz-se que a verificacao foi da memoria).</summary>
        public static readonly bool Works = Probe();

        private static bool Probe()
        {
            if (!OperatingSystem.IsMacOS()) return false;
            try
            {
                var tmp = System.IO.Path.GetTempFileName();
                try
                {
                    using var s = new FileStream(tmp, FileMode.Open, FileAccess.Read);
                    var dup = Fcntl((int)s.SafeFileHandle.DangerousGetHandle(), F_DUPFD, 100);
                    if (dup < 0) return false;
                    close(dup);
                    return dup >= 100;
                }
                finally { File.Delete(tmp); }
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or IOException or UnauthorizedAccessException) { return false; }
        }

        public static bool NoCache(SafeFileHandle h) => Works && Fcntl((int)h.DangerousGetHandle(), F_NOCACHE, 1) == 0;

        public static void FullSync(SafeFileHandle h)
        {
            if (Works) Fcntl((int)h.DangerousGetHandle(), F_FULLFSYNC, 0);
        }
    }
}
