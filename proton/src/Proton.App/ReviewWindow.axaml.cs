using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Proton.Core;

namespace Proton.App;

public sealed record ReviewRowVm(ReviewRow Row, IBrush Brush);
public sealed record ReviewGroupVm(string Header, IBrush HeaderBrush, IReadOnlyList<ReviewRowVm> Rows);

/// <summary>O shoot log do Atom com os ficheiros associados e a prova de cada associacao, take a take.</summary>
public sealed partial class ReviewWindow : Window
{
    private static readonly IBrush Green = Brush.Parse("#22A06B"), Yellow = Brush.Parse("#C98A00"), Red = Brush.Parse("#E5484D"), Gray = Brush.Parse("#8A8F98");
    private readonly MatchReview _review;

    public ReviewWindow() : this(new MatchReview(), "") { }        // para o designer do Avalonia

    public ReviewWindow(MatchReview review, string summary)
    {
        InitializeComponent();
        _review = review;
        var problems = review.Takes.Sum(t => t.Rows.Count(r => r.Status != ReviewCheck.Ok)) + review.Unmatched.Count;
        SummaryText.Text = summary + (problems == 0 ? "   -   everything matched and checked" : $"   -   {problems} line(s) to check");
        Fill();
    }

    public static IBrush BrushFor(string status) => status switch
    {
        ReviewCheck.Ok => Green,
        ReviewCheck.Check => Yellow,
        ReviewCheck.Missing => Red,
        _ => Gray,
    };

    public IReadOnlyList<ReviewGroupVm> Groups { get; private set; } = Array.Empty<ReviewGroupVm>();

    private void Fill()
    {
        var only = OnlyProblems.IsChecked == true;
        var groups = new List<ReviewGroupVm>();
        foreach (var t in _review.Takes)
        {
            var rows = t.Rows.Where(r => !only || r.Status != ReviewCheck.Ok).Select(r => new ReviewRowVm(r, BrushFor(r.Status))).ToList();
            if (rows.Count == 0) continue;
            groups.Add(new ReviewGroupVm(t.Header, t.AllOk ? Green : t.Rows.Any(r => r.Status == ReviewCheck.Missing) ? Red : Yellow, rows));
        }
        if (_review.Unmatched.Count > 0)
            groups.Add(new ReviewGroupVm($"Files without a take ({_review.Unmatched.Count})", Gray,
                _review.Unmatched.Select(r => new ReviewRowVm(r, Gray)).ToList()));
        Groups = groups;
        GroupList.ItemsSource = groups;
    }

    private void OnFilter(object? sender, RoutedEventArgs e) => Fill();

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save the review", SuggestedFileName = "review.csv", DefaultExtension = "csv",
        });
        var path = file?.TryGetLocalPath();
        if (path == null) return;
        File.WriteAllText(path, _review.ToCsv());
        StatusText.Text = "Saved " + path;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
