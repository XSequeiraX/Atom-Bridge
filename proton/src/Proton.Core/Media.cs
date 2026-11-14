namespace Proton.Core;

public enum MediaKind { Video, Audio }

/// <summary>Um ficheiro de media copiado (ou num cartao) e o que se consegue ler dele sem o abrir por inteiro.</summary>
public sealed record MediaFile(string Path, MediaKind Kind, long Size, DateTime Created, DateTime Modified)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Stem => System.IO.Path.GetFileNameWithoutExtension(Path);
    /// <summary>Duracao, se o formato a diz.</summary>
    public TimeSpan? Duration { get; init; }
    /// <summary>Timecode de inicio gravado no ficheiro, em segundos desde a meia-noite (precisa do Fps para virar HH:MM:SS:FF).</summary>
    public double? StartSeconds { get; init; }
    public double? Fps { get; init; }
    /// <summary>A hora a que o ficheiro diz ter sido gravado (relogio da camara), se a diz.</summary>
    public DateTime? RecordedAt { get; init; }
    /// <summary>Os user bits do timecode, se o ficheiro os traz (iXML UBITS nos WAV), e a data que levam (SMPTE 309M).</summary>
    public string? UserBits { get; init; }
    public DateOnly? UserBitsDate { get; init; }
    public string? Scene { get; init; }
    public int? TakeNo { get; init; }
    /// <summary>A camara a que o operador disse que pertence o cartao ou a pasta.</summary>
    public string? Camera { get; init; }
    /// <summary>O que nao se conseguiu ler e porque (por exemplo BRAW sem leitor de timecode).</summary>
    public string? Note { get; init; }
}

public interface IMediaReader
{
    bool CanRead(string extension);
    MediaFile Read(string path);
}
