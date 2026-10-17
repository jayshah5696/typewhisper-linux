using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.ViewModels;
using TypeWhisper.Linux.Views;
using Xunit;

namespace TypeWhisper.UiTests;

[Collection("ui")]
public sealed class RecoveryPaletteInputTests(UiSession ui)
{
    [Fact]
    public async Task EscapeClosesPaletteAndCancelsPendingCleanup()
    {
        await ui.Session.Dispatch(() =>
        {
            var response = new TaskCompletionSource<CleanupResult>();
            CancellationToken requestToken = default;
            var entry = new RecentTranscriptionEntry("fixture", "Previous result", DateTime.UtcNow,
                null, null, RecentTranscriptionSource.Session) { RawText = "Original transcript" };
            var vm = new RecentTranscriptionsPaletteViewModel([entry], _ => { },
                (_, token) => { requestToken = token; return response.Task; },
                _ => Task.FromResult(InsertionResult.CopiedToClipboard));
            var window = new RecentTranscriptionsPaletteWindow(vm);
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                window.FindControl<TextBox>("SearchBox")!.Focus();
                _ = vm.CleanAgainCommand.ExecuteAsync(null);
                Assert.True(vm.IsRecovering);

                window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                Dispatcher.UIThread.RunJobs();

                Assert.False(window.IsVisible);
                Assert.True(requestToken.IsCancellationRequested);
            }
            finally
            {
                window.Close();
                response.TrySetResult(new CleanupResult("Late result", new CleanupOutcome(CleanupLevel.Medium, CleanupLevel.Medium)));
                Dispatcher.UIThread.RunJobs();
            }
        }, CancellationToken.None);
    }
}
