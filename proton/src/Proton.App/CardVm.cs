using System.ComponentModel;
using System.Runtime.CompilerServices;
using Proton.Core;

namespace Proton.App;

/// <summary>Uma linha da lista de cartoes: a pasta, a camara escolhida, e o que se leu.</summary>
public sealed class CardVm : INotifyPropertyChanged
{
    public CardVm(CardSource source) { Source = source; }
    public CardSource Source { get; }
    public string Path => Source.Path;

    public string? Camera
    {
        get => Source.Camera;
        set
        {
            if (Source.Camera == value) return;
            Source.Camera = value;
            Raise();
        }
    }

    public string Info
    {
        get
        {
            var n = Source.Files.Count;
            var noTc = Source.Files.Count(f => f.StartSeconds == null);
            return n == 0 ? "no media files" : $"{n} file(s)" + (noTc > 0 ? $", {noTc} without timecode" : "");
        }
    }

    public void Refresh() => Raise(nameof(Info));

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>Uma linha do resultado, com a cor do estado.</summary>
public sealed record ResultLine(string Text, Avalonia.Media.IBrush Brush);
