using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Proton.App;
using Proton.Core;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(Proton.App.Tests.TestApp))]

namespace Proton.App.Tests;

public sealed class TestApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Proton.App.App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });
}

/// <summary>A janela abre, tem os controlos dos quatro passos, e carrega as takes de um ficheiro: o suficiente para apanhar uma
/// janela que nao arranca em Windows ou Mac.</summary>
public class WindowTests
{
    private static string Fx() => Path.Combine(AppContext.BaseDirectory, "Fixtures", "atom-takes.json");

    [AvaloniaFact]
    public void TheWindowOpensWithAllTheControls()
    {
        var w = new MainWindow();
        w.Show();
        foreach (var name in new[] { "AddressBox", "DayBox", "LoadButton", "TakesInfo", "DestBox", "TemplateBox", "ParallelBox", "SummaryText",
                                     "ResultList", "Bar", "StatusText", "MatchButton", "CopyButton", "CancelButton", "ReviewButton" })
            Assert.NotNull(w.FindControl<Control>(name));
        Assert.Equal(PathTemplate.Default, w.FindControl<TextBox>("TemplateBox")!.Text);
        Assert.False(w.FindControl<Button>("CancelButton")!.IsEnabled);
        w.Close();
    }

    [AvaloniaFact]
    public async Task LoadingTheTakesFillsTheCameraList()
    {
        var w = new MainWindow();
        w.Show();
        w.FindControl<TextBox>("AddressBox")!.Text = Fx();
        w.FindControl<Button>("LoadButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        for (var i = 0; i < 100 && w.CameraNames.Count == 0; i++) { await Task.Delay(50); Dispatcher.UIThread.RunJobs(); }
        Assert.Equal(new[] { "A", "B", "F6" }, w.CameraNames.OrderBy(x => x));
        Assert.Contains("2 take(s)", w.FindControl<TextBlock>("TakesInfo")!.Text);
        w.Close();
    }

    [AvaloniaFact]
    public void MatchWithoutTakesTellsWhatToDo()
    {
        var w = new MainWindow();
        w.Show();
        w.FindControl<Button>("MatchButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Contains("Load the takes first", w.FindControl<TextBlock>("StatusText")!.Text);
        w.Close();
    }

    [AvaloniaFact]
    public void ReviewWithoutMatchTellsWhatToDo()
    {
        var w = new MainWindow();
        w.Show();
        Assert.False(w.FindControl<Button>("ReviewButton")!.IsEnabled);
        w.Close();
    }

    [AvaloniaFact]
    public void TheReviewWindowShowsTakesAndFilters()
    {
        var take = new Take("2026-11-09", "S", 1, new DateTime(2026, 11, 9, 10, 0, 0), null, false, null, "10:00:00:00", Array.Empty<TakeCamera>(), Array.Empty<string>());
        var review = new MatchReview();
        review.Takes.Add(new ReviewTake(take, "Take 1", new[]
        {
            new ReviewRow(ReviewCheck.Ok, "A", "10:00:00:00", "10:00:00:00", "+0.00 s", "60 s", "60 s", "2026-11-09 (file)", "A001.braw", "timecode", "", "/x/A001.braw"),
            new ReviewRow(ReviewCheck.Check, "B", "10:00:00:00", "10:00:03:00", "+3.00 s", "60 s", "60 s", "2026-11-09 (file)", "MVI_1.MP4", "timecode", "timecode 3 s off", "/x/MVI_1.MP4"),
        }));
        review.Unmatched.Add(new ReviewRow(ReviewCheck.NoTake, "A", null, null, null, null, "5 s", null, "A002.braw", null, "no take at this time", "/x/A002.braw"));
        var w = new ReviewWindow(review, "1 take");
        w.Show();
        Assert.Equal(2, w.Groups.Count);
        Assert.Equal(2, w.Groups[0].Rows.Count);
        Assert.Contains("2 line(s) to check", w.FindControl<TextBlock>("SummaryText")!.Text);
        w.FindControl<CheckBox>("OnlyProblems")!.IsChecked = true;
        Assert.Single(w.Groups[0].Rows);
        Assert.Equal("B", w.Groups[0].Rows[0].Row.Camera);
        w.Close();
    }
}
