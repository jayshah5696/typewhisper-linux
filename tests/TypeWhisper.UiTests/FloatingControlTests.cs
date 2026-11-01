using System.Text.Json;
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
    public async Task LauncherAndRecordingControls_AreCompactReadableAndNamed(string language)
    {
        await ui.Session.Dispatch(() =>
        {
            Loc.Instance.CurrentLanguage = language;
            var settings = new TestSettings();
            var coordinator = new OverlayCoordinator(settings, action => action());
            var vm = new DictationOverlayViewModel(settings, action => action(), overlayCoordinator: coordinator,
                startDictation: () => Task.CompletedTask,
                stopDictation: () => Task.CompletedTask,
                cancelDictation: () => Task.CompletedTask);
            var window = new DictationOverlayWindow(vm, settings);
            var keys = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(Path.Join(UiEnvironment.LocalizationDir, language + ".json")))!.Keys.ToHashSet();
            try
            {
                window.Initialize();
                MainWindowRenderTests.Settle();
                Assert.InRange(window.ClientSize.Width, 1, 64);
                Assert.InRange(window.ClientSize.Height, 1, 40);
                Assert.False(window.FindControl<Button>("ExpandLauncherButton")!.Focusable);
                CheckAndCapture("idle");
                settings.Update(current => current with { OverlayCustomLeft = 200, OverlayCustomTop = 100 });
                MainWindowRenderTests.Settle();
                var compactCenter = window.Position.X + window.ClientSize.Width * window.RenderScaling / 2;
                vm.SetLauncherExpanded(true);
                MainWindowRenderTests.Settle();
                Assert.InRange(window.ClientSize.Width, 100, 180);
                Assert.Equal(compactCenter, window.Position.X + window.ClientSize.Width * window.RenderScaling / 2, precision: 1);
                CheckAndCapture("expanded");

                var token = coordinator.Acquire(OverlayRequester.Dictation);
                coordinator.Show(token, new DictationOverlayState
                {
                    IsOverlayVisible = true,
                    IsRecording = true,
                    StatusText = Loc.Instance["Dictation.StatusRecording"],
                });
                MainWindowRenderTests.Settle();
                Assert.True(vm.ShowRecordingControls);
                Assert.False(vm.ShowLauncher);
                CheckAndCapture("recording");
                coordinator.Show(token, new DictationOverlayState { IsOverlayVisible = true });
                MainWindowRenderTests.Settle();
                Assert.True(vm.ShowProcessingControls);
                Assert.InRange(window.ClientSize.Height, 1, 40);
                CheckAndCapture("finishing");
                coordinator.Show(token, new DictationOverlayState { ShowFeedback = true, DeliverySucceeded = true });
                MainWindowRenderTests.Settle();
                Assert.True(vm.ShowSuccessConfirmation);
                CheckAndCapture("delivered");

                coordinator.Release(token);
                MainWindowRenderTests.Settle();
                Assert.True(vm.ShowCompactLauncher);
                Assert.InRange(window.ClientSize.Width, 1, 64);

                void CheckAndCapture(string state)
                {
                    if (state is "idle" or "expanded")
                    {
                        var primary = window.FindControl<Button>("ExpandLauncherButton")!;
                        var icon = primary.GetVisualDescendants().OfType<FluentIcons.Avalonia.SymbolIcon>().Single();
                        if (state == "expanded")
                        {
                            Assert.True(icon.Bounds.Height >= icon.FontSize,
                                $"{state}: microphone needs {icon.FontSize}px but receives only {icon.Bounds.Height}px of height.");
                            Assert.True(icon.Bounds.Width >= icon.FontSize, "microphone is clipped horizontally");
                        }
                        else
                        {
                            Assert.False(icon.IsEffectivelyVisible);
                            Assert.Equal(3, window.FindControl<Border>("QuietMarker")!.Bounds.Height);
                        }
                        Assert.All(window.GetVisualDescendants().OfType<Button>(), button => Assert.Null(ToolTip.GetTip(button)));
                    }
                    Assert.Empty(UiInspector.Inspect(window, keys));
                    window.CaptureRenderedFrame()?.Save(Path.Join(UiEnvironment.SnapshotDir,
                        $"FloatingControl-{language}-{state}.png"), PngBitmapEncoderOptions.Default);
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
