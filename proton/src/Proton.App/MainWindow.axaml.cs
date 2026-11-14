using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Proton.Core;

namespace Proton.App;

public sealed partial class MainWindow : Window
{
    private static readonly IBrush Green = Brush.Parse("#22A06B"), Yellow = Brush.Parse("#C98A00"), Red = Brush.Parse("#E5484D"), Gray = Brush.Parse("#8A8F98");

    private readonly ProtonSession _s = new();
    private readonly ProtonSettings _cfg = ProtonSettings.Load();
    private CancellationTokenSource? _cts;

    public ObservableCollection<CardVm> Cards { get; } = new();
    public ObservableCollection<string> CameraNames { get; } = new();
    public ObservableCollection<ResultLine> Lines { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        AddressBox.Text = _cfg.AtomAddress;
        DestBox.Text = _cfg.Destination;
        TemplateBox.Text = string.IsNullOrWhiteSpace(_cfg.Template) ? PathTemplate.Default : _cfg.Template;
        ParallelBox.Value = _cfg.Parallel;
        ResultList.ItemsSource = Lines;
        Closing += (_, _) => SaveSettings();
    }

    private void SaveSettings()
    {
        _cfg.AtomAddress = AddressBox.Text?.Trim() ?? "";
        _cfg.Destination = DestBox.Text?.Trim() ?? "";
        _cfg.Template = TemplateBox.Text?.Trim() ?? PathTemplate.Default;
        _cfg.Parallel = (int)(ParallelBox.Value ?? 4);
        _cfg.Save();
    }

    // ---- ajudas -----------------------------------------------------------------------------------------------------
    private void Status(string text, bool error = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = error ? Red : null;
    }

    private void Busy(bool on)
    {
        foreach (var b in new Button[] { LoadButton, MatchButton, CopyButton }) b.IsEnabled = !on;
        CancelButton.IsEnabled = on && _cts != null;
    }

    private static string? LocalPath(IStorageItem? item) => item?.TryGetLocalPath();

    private async Task<string?> PickFolder(string title)
    {
        var r = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return r.Count > 0 ? LocalPath(r[0]) : null;
    }

    private void SyncCameraNames()
    {
        var names = _s.CameraNames;
        foreach (var n in names) if (!CameraNames.Contains(n)) CameraNames.Add(n);          // sem limpar a lista: os cartoes mantem a escolha
        foreach (var n in CameraNames.Where(x => !names.Contains(x)).ToList()) CameraNames.Remove(n);
    }

    // ---- 1  takes ---------------------------------------------------------------------------------------------------
    private async Task LoadTakes(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) { Status("Type the Atom address or open an exported file", true); return; }
        Busy(true);
        try
        {
            Status("Loading the takes ...");
            await _s.LoadTakesAsync(source, string.IsNullOrWhiteSpace(DayBox.Text) ? null : DayBox.Text.Trim());
            SyncCameraNames();
            var n = _s.Export!.Takes.Count;
            TakesInfo.Text = $"{n} take(s), {CameraNames.Count} camera(s)  -  {(source.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? "from the Atom" : Path.GetFileName(source))}";
            Status(n == 0 ? "The Atom has no takes for that day" : "Takes loaded");
        }
        catch (Exception e)
        {
            Status("Could not load the takes: " + e.Message, true);
        }
        finally { Busy(false); }
    }

    private async void OnLoad(object? sender, RoutedEventArgs e) => await LoadTakes(AddressBox.Text?.Trim() ?? "");

    private async void OnOpenFile(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Takes exported by the Atom",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Atom takes") { Patterns = new[] { "*.json", "*.csv" } } },
        });
        var path = files.Count > 0 ? LocalPath(files[0]) : null;
        if (path == null) return;
        AddressBox.Text = path;
        await LoadTakes(path);
    }

    private async void OnFindUsb(object? sender, RoutedEventArgs e)
    {
        var found = await Task.Run(() => UsbDiscovery.Find());
        if (found.Count == 0) { Status("No AtomBridge/atom-takes-*.json found on the connected drives", true); return; }
        AddressBox.Text = found[0];
        await LoadTakes(found[0]);
    }

    // ---- 2  cartoes -------------------------------------------------------------------------------------------------
    private async void OnAddCard(object? sender, RoutedEventArgs e)
    {
        var path = await PickFolder("Card or folder with the camera files");
        if (path == null) return;
        Status("Reading " + path + " ...");
        var card = await Task.Run(() => _s.AddCard(path));
        Cards.Add(new CardVm(card));
        Status(card.Files.Count == 0 ? "No media files found there" : $"{card.Files.Count} file(s) found. Now choose the camera of this card.", card.Files.Count == 0);
    }

    private void OnRemoveCard(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not CardVm vm) return;
        _s.Cards.Remove(vm.Source);
        Cards.Remove(vm);
    }

    private async void OnRescan(object? sender, RoutedEventArgs e)
    {
        Status("Reading the cards ...");
        await Task.Run(() => { foreach (var c in _s.Cards) _s.Rescan(c); });
        foreach (var c in Cards) c.Refresh();
        Status("Cards read again");
    }

    // ---- 3/4  cruzar e copiar ---------------------------------------------------------------------------------------
    private bool Ready(bool needDestination)
    {
        if (_s.Export == null) { Status("Load the takes first (step 1)", true); return false; }
        if (Cards.Count == 0) { Status("Add at least one card (step 2)", true); return false; }
        if (_s.CardsWithoutCamera.Any()) { Status("Choose the camera of every card (step 2)", true); return false; }
        if (needDestination && string.IsNullOrWhiteSpace(DestBox.Text)) { Status("Choose the destination folder (step 3)", true); return false; }
        return true;
    }

    private void ApplyOptions()
    {
        _s.Destination = DestBox.Text?.Trim() ?? "";
        _s.Template = string.IsNullOrWhiteSpace(TemplateBox.Text) ? PathTemplate.Default : TemplateBox.Text.Trim();
        _s.Parallel = (int)(ParallelBox.Value ?? 4);
    }

    private void ShowResult()
    {
        var r = _s.Result;
        Lines.Clear();
        if (r == null) return;
        SummaryText.Text = _s.Summary();
        ReviewButton.IsEnabled = true;
        foreach (var m in r.Matches.OrderBy(x => x.Take.Start).ThenBy(x => x.Camera.Label))
        {
            var where = m.Offset.TotalSeconds > 2 ? $"  (starts at {m.Offset:hh\\:mm\\:ss})" : "";
            Lines.Add(new ResultLine($"{(m.Doubtful ? "CHECK  " : "OK     ")}take {m.Take.Number,-3} {m.Camera.Label,-10} {m.File.Name}  [{m.Method.ToString().ToLowerInvariant()}]{where}",
                m.Doubtful ? Yellow : Green));
        }
        foreach (var (t, c) in r.Missing.OrderBy(x => x.Take.Start))
            Lines.Add(new ResultLine($"MISSING take {t.Number,-3} {c.Label,-10} no file{(c.Fault ? " (the camera failed)" : "")}", Red));
        foreach (var f in r.Unmatched)
            Lines.Add(new ResultLine($"NO TAKE {f.Name}  {f.Camera}", Gray));
    }

    private async void OnMatch(object? sender, RoutedEventArgs e)
    {
        if (!Ready(false)) return;
        Busy(true);
        try
        {
            Status("Matching ...");
            await Task.Run(() => _s.Match());
            foreach (var c in Cards) c.Refresh();
            ShowResult();
            Status("Done. Check the lines marked CHECK, MISSING and NO TAKE.");
        }
        catch (Exception ex) { Status(ex.Message, true); }
        finally { Busy(false); }
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (!Ready(true)) return;
        ApplyOptions();
        SaveSettings();
        _cts = new CancellationTokenSource();
        Busy(true);
        try
        {
            Status("Reading the cards and matching ...");
            await Task.Run(() => _s.Match());
            ShowResult();
            var plan = _s.PlanCopy();
            var total = Math.Max(1, plan.Sum(i => new FileInfo(i.Source).Length));
            var done = new Dictionary<string, long>();
            Bar.Value = 0;
            var progress = new Progress<CopyProgress>(p =>
            {
                done[p.File] = p.Done;
                Bar.Value = Math.Min(1.0, done.Values.Sum() / (double)total);
                Status($"Copying {plan.Count} file(s), {_s.Parallel} at a time: {Bar.Value:P0}");
            });
            var results = await _s.CopyAsync(progress, _cts.Token);
            Bar.Value = 1;
            var failed = results.Where(x => !x.Ok).ToList();
            foreach (var f in failed) Lines.Insert(0, new ResultLine($"FAILED  {Path.GetFileName(f.Item.Source)}: {f.Error}", Red));
            Status(failed.Count == 0
                ? $"Copied {results.Count(x => x.Ok && !x.Skipped)} file(s), {results.Count(x => x.Skipped)} already there. " +
                  (results.All(x => x.FromDisk) ? "All verified, read back from the disk. "
                   : $"All verified, {results.Count(x => !x.FromDisk)} only from memory (this drive does not allow reading past the cache). ") +
                  "Resolve CSV and report are in the destination folder."
                : $"{failed.Count} file(s) FAILED. The others are copied and verified.", failed.Count > 0);
        }
        catch (Exception ex) { Status(ex.Message, true); }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            Busy(false);
        }
    }

    private void OnReview(object? sender, RoutedEventArgs e)
    {
        if (_s.Result == null) { Status("Press Match first", true); return; }
        new ReviewWindow(_s.Review(), _s.Summary()).Show(this);
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Status("Cancelling ...");
    }

    private async void OnBrowseDest(object? sender, RoutedEventArgs e)
    {
        var path = await PickFolder("Destination folder");
        if (path != null) DestBox.Text = path;
    }

    private async void OnSaveOutputs(object? sender, RoutedEventArgs e)
    {
        if (_s.Result == null) { Status("Press Match first", true); return; }
        var path = await PickFolder("Where to save resolve.csv and report.txt");
        if (path == null) return;
        _s.WriteOutputs(path);
        Status("Saved resolve.csv and report.txt in " + path);
    }
}
