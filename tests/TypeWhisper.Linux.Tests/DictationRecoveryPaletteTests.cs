using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.ViewModels;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class DictationRecoveryPaletteTests
{
    [Fact]
    public void SessionAndHistoryEntries_KeepOriginalAndOutcomeWithoutRequiringSavedAudio()
    {
        var store = new RecentTranscriptionStore();
        var outcome = new CleanupOutcome(CleanupLevel.High, CleanupLevel.Light, CleanupFallbackReason.TimedOut);
        store.RecordTranscription("session", "final", DateTime.UtcNow, null, null,
            rawText: "original", language: "en", cleanupOutcome: outcome);
        var saved = new TranscriptionRecord
        {
            Id = "saved", Timestamp = DateTime.UtcNow.AddMinutes(-1), RawText = "saved original",
            FinalText = "saved final", CleanupOutcome = outcome,
        };

        var entries = store.MergedEntries([saved]);

        Assert.Equal("original", entries[0].RawText);
        Assert.Equal("saved original", entries[1].RawText);
        Assert.All(entries, entry => Assert.Equal(outcome, entry.CleanupOutcome));
    }

    [Fact]
    public async Task Recovery_PreviewsNewTextWithoutOverwritingOrCopyingOriginalEntry()
    {
        var copies = new List<string>();
        var entry = Entry("one", "raw transcript");
        var vm = Create([entry], (_, _) => Task.FromResult(Success("new result")), copies);

        await vm.CleanAgainCommand.ExecuteAsync(null);

        Assert.Equal("new result", vm.RecoveryText);
        Assert.Equal("old result", entry.FinalText);
        Assert.Empty(copies);
        await vm.CopyRecoveryCommand.ExecuteAsync(null);
        Assert.Equal("new result", Assert.Single(copies));
    }

    [Fact]
    public async Task Recovery_MissingRawText_DisablesActionsRatherThanTreatingFinalAsOriginal()
    {
        var vm = Create([Entry("one", null)], (_, _) => throw new Exception("must not call"), []);

        Assert.False(vm.CleanAgainCommand.CanExecute(null));
        Assert.False(vm.CopyOriginalCommand.CanExecute(null));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Recovery_SelectionChanges_LateProviderResultIsDiscarded()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<CleanupResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = Create([Entry("one", "first"), Entry("two", "second")],
            (_, _) => { entered.SetResult(); return release.Task; }, []);
        var pending = vm.CleanAgainCommand.ExecuteAsync(null);
        await entered.Task;
        vm.SelectedItem = vm.FilteredEntries[1];
        release.SetResult(Success("stale result"));
        await pending;

        Assert.Empty(vm.RecoveryText);
        Assert.False(vm.CopyRecoveryCommand.CanExecute(null));
    }

    [Fact]
    public async Task Recovery_Fallback_RemainsVisibleAndDoesNotOverwriteClipboard()
    {
        var copies = new List<string>();
        var vm = Create([Entry("one", "uh hello")], (_, _) => Task.FromResult(new CleanupResult("Hello",
            new CleanupOutcome(CleanupLevel.Medium, CleanupLevel.Light, CleanupFallbackReason.ProviderUnavailable))), copies);

        await vm.CleanAgainCommand.ExecuteAsync(null);

        Assert.NotEmpty(vm.RecoveryStatus);
        Assert.Equal("Hello", vm.RecoveryText);
        Assert.Empty(copies);
    }

    [Fact]
    public async Task Recovery_ClosedPalette_CancelsAndDiscardsLateResult()
    {
        var release = new TaskCompletionSource<CleanupResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken captured = default;
        var vm = Create([Entry("one", "original")], (_, token) => { captured = token; return release.Task; }, []);
        var pending = vm.CleanAgainCommand.ExecuteAsync(null);
        vm.CancelRecovery();
        release.SetResult(Success("late"));
        await pending;

        Assert.True(captured.IsCancellationRequested);
        Assert.Empty(vm.RecoveryText);
    }

    [Fact]
    public async Task CopyOriginal_UsesExactRawTextAndReportsCopyFailure()
    {
        string? copied = null;
        var vm = new RecentTranscriptionsPaletteViewModel([Entry("one", " raw\ntext ")], _ => { },
            (_, _) => Task.FromResult(Success("new")), text => { copied = text; return Task.FromResult(InsertionResult.Failed); });

        await vm.CopyOriginalCommand.ExecuteAsync(null);

        Assert.Equal(" raw\ntext ", copied);
        Assert.NotEmpty(vm.RecoveryStatus);
    }

    private static RecentTranscriptionEntry Entry(string id, string? raw) =>
        new(id, "old result", DateTime.UtcNow, null, null, RecentTranscriptionSource.Session) { RawText = raw };

    private static CleanupResult Success(string text) => new(text, new CleanupOutcome(CleanupLevel.Medium, CleanupLevel.Medium));

    private static RecentTranscriptionsPaletteViewModel Create(IReadOnlyList<RecentTranscriptionEntry> entries,
        Func<RecentTranscriptionEntry, CancellationToken, Task<CleanupResult>> clean, List<string> copies) =>
        new(entries, _ => { }, clean, text => { copies.Add(text); return Task.FromResult(InsertionResult.CopiedToClipboard); });
}
