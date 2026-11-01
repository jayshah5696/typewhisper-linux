using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.ViewModels;
using TypeWhisper.Linux.Views;
using Xunit;

namespace TypeWhisper.UiTests;

[Collection("ui")]
public sealed class FloatingControlTests(UiSession ui)
{
    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("ru")]
    [InlineData("ja")]
    [InlineData("zh")]
    public async Task ControlsFollowEveryEdge_WithEvenSpacingAndReadableIcons(string language)
    {
        await ui.Session.Dispatch(() =>
        {
            Loc.Instance.CurrentLanguage = language;
            var settings = new TestSettings();
            var coordinator = new OverlayCoordinator(settings, action => action());
            var vm = new DictationOverlayViewModel(settings, action => action(), overlayCoordinator: coordinator,
                startDictation: () => Task.CompletedTask, stopDictation: () => Task.CompletedTask,
                cancelDictation: () => Task.CompletedTask);
            var window = new DictationOverlayWindow(vm, settings);
            var keys = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(Path.Join(UiEnvironment.LocalizationDir, language + ".json")))!.Keys.ToHashSet();
            try
            {
                window.Initialize();
                foreach (var edge in Enum.GetValues<OverlayPosition>())
                {
                    settings.Update(current => current with { OverlayPosition = edge });
                    MainWindowRenderTests.Settle();
                    var side = edge is OverlayPosition.Left or OverlayPosition.Right;
                    Assert.Equal(new Size(side ? 18 : 36, side ? 36 : 18), window.ClientSize);
                    Assert.False(window.FindControl<Button>("ExpandLauncherButton")!.Focusable);
                    Capture("idle");
                    var idleCenter = side
                        ? window.Position.Y + window.ClientSize.Height * window.RenderScaling / 2
                        : window.Position.X + window.ClientSize.Width * window.RenderScaling / 2;
                    vm.SetLauncherExpanded(true);
                    MainWindowRenderTests.Settle();
                    if (side)
                    {
                        Assert.InRange(window.ClientSize.Width, 28, 48);
                        Assert.InRange(window.ClientSize.Height, 80, 120);
                    }
                    else
                    {
                        Assert.InRange(window.ClientSize.Width, 90, 140);
                        Assert.InRange(window.ClientSize.Height, 28, 40);
                    }
                    var expandedCenter = side
                        ? window.Position.Y + window.ClientSize.Height * window.RenderScaling / 2
                        : window.Position.X + window.ClientSize.Width * window.RenderScaling / 2;
                    Assert.Equal(idleCenter, expandedCenter, precision: 1);
                    var primary = window.FindControl<Button>("ExpandLauncherButton")!;
                    var recorder = ButtonFor(vm.OpenRecorderCommand);
                    var settingsButton = ButtonFor(vm.OpenSettingsCommand);
                    Adjacent(recorder, primary);
                    Adjacent(primary, settingsButton);
                    foreach (var icon in window.GetVisualDescendants().OfType<FluentIcons.Avalonia.SymbolIcon>().Where(i => i.IsEffectivelyVisible))
                    {
                        Assert.True(icon.Bounds.Width >= icon.FontSize, "Icon is clipped horizontally");
                        Assert.True(icon.Bounds.Height >= icon.FontSize, "Icon is clipped vertically");
                    }
                    Capture("expanded");
                    var token = coordinator.Acquire(OverlayRequester.Dictation);
                    coordinator.Show(token, new DictationOverlayState
                    {
                        IsOverlayVisible = true, IsRecording = true,
                        StatusText = Loc.Instance["Dictation.StatusRecording"],
                    });
                    MainWindowRenderTests.Settle();
                    var stop = ButtonFor(vm.StopDictationCommand);
                    var cancel = ButtonFor(vm.CancelDictationCommand);
                    Adjacent(stop, cancel);
                    var cancelGlyph = Assert.IsAssignableFrom<Control>(cancel.Content);
                    Assert.InRange(cancelGlyph.Bounds.Width, 1, 14);
                    Assert.InRange(cancelGlyph.Bounds.Height, 1, 14);
                    Assert.Equal(Box(cancel).Center.X, Box(cancelGlyph).Center.X, precision: 1);
                    Assert.Equal(Box(cancel).Center.Y, Box(cancelGlyph).Center.Y, precision: 1);
                    Assert.True(vm.ShowRecordingControls);
                    if (side)
                    {
                        Assert.InRange(window.ClientSize.Width, 28, 48);
                        Assert.True(window.ClientSize.Height > window.ClientSize.Width);
                    }
                    else
                    {
                        Assert.InRange(window.ClientSize.Width, 100, 150);
                        Assert.InRange(window.ClientSize.Height, 28, 40);
                    }
                    Capture("recording");
                    coordinator.Show(token, new DictationOverlayState { IsOverlayVisible = true });
                    MainWindowRenderTests.Settle();
                    Assert.True(vm.ShowProcessingControls);
                    if (side) Assert.InRange(window.ClientSize.Width, 28, 48);
                    else Assert.InRange(window.ClientSize.Height, 28, 40);
                    Capture("finishing");
                    coordinator.Show(token, new DictationOverlayState { ShowFeedback = true, DeliverySucceeded = true });
                    MainWindowRenderTests.Settle();
                    Assert.True(vm.ShowSuccessConfirmation);
                    Capture("delivered");
                    coordinator.Release(token);
                    MainWindowRenderTests.Settle();
                    Assert.True(vm.ShowCompactLauncher);

                    Button ButtonFor(System.Windows.Input.ICommand command) => window.GetVisualDescendants()
                        .OfType<Button>().Single(b => ReferenceEquals(b.Command, command));
                    Rect Box(Control control)
                    {
                        var point = control.TranslatePoint(default, window)!.Value;
                        return new Rect(point, control.Bounds.Size);
                    }
                    void Adjacent(Control first, Control second)
                    {
                        var a = Box(first);
                        var b = Box(second);
                        if (side)
                        {
                            Assert.Equal(a.Center.X, b.Center.X, precision: 1);
                            Assert.InRange(b.Top - a.Bottom, 3.9, 6.1);
                        }
                        else
                        {
                            Assert.Equal(a.Center.Y, b.Center.Y, precision: 1);
                            Assert.InRange(b.Left - a.Right, 3.9, 6.1);
                        }
                    }
                    void Capture(string state)
                    {
                        Assert.All(window.GetVisualDescendants().OfType<Button>(), b => Assert.Null(ToolTip.GetTip(b)));
                        Assert.Empty(UiInspector.Inspect(window, keys));
                        var suffix = edge == OverlayPosition.Bottom ? "" : "-" + edge;
                        window.CaptureRenderedFrame()?.Save(Path.Join(UiEnvironment.SnapshotDir,
                            $"FloatingControl-{language}-{state}{suffix}.png"), PngBitmapEncoderOptions.Default);
                    }
                }
            }
            finally
            {
                window.Close();
                Dispatcher.UIThread.RunJobs();
                Loc.Instance.CurrentLanguage = "en";
            }
        }, CancellationToken.None);
    }

    private sealed class TestSettings : ISettingsService
    {
        public AppSettings Current { get; private set; } = AppSettings.Default with { ShowFloatingControl = true };
        public event Action<AppSettings>? SettingsChanged;
        public AppSettings Load() => Current;
        public void Save(AppSettings settings) { Current = settings; SettingsChanged?.Invoke(settings); }
        public AppSettings Update(Func<AppSettings, AppSettings> mutate) { Save(mutate(Current)); return Current; }
    }
}
