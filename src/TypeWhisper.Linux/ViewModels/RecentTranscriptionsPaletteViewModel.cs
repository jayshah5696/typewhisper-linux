using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;

namespace TypeWhisper.Linux.ViewModels;

// MVVM Toolkit [ObservableProperty] generates the On<Property>Changed(value) partial hooks; the
// value parameter is part of the generated signature and cannot be dropped even when ignored here.
// ReSharper disable UnusedParameterInPartialMethod
public partial class RecentTranscriptionsPaletteViewModel : ObservableObject
{
    private readonly IReadOnlyList<RecentTranscriptionPaletteItem> _allItems;
    private readonly Action<RecentTranscriptionPaletteItem> _onSelect;
    private readonly Func<RecentTranscriptionEntry, CancellationToken, Task<CleanupResult>>? _cleanAgain;
    private readonly Func<string, Task<InsertionResult>>? _copy;
    private CancellationTokenSource? _recoveryCts;
    private bool _closed;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyRecoveryCommand))]
    private string _recoveryText = "";

    [ObservableProperty]
    private string _recoveryStatus = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CleanAgainCommand))]
    private bool _isRecovering;

    [ObservableProperty]
    private string _searchQuery = "";

    [ObservableProperty]
    private RecentTranscriptionPaletteItem? _selectedItem;

    public RecentTranscriptionsPaletteViewModel(
        IReadOnlyList<RecentTranscriptionEntry> entries,
        Action<RecentTranscriptionPaletteItem> onSelect,
        Func<RecentTranscriptionEntry, CancellationToken, Task<CleanupResult>>? cleanAgain = null,
        Func<string, Task<InsertionResult>>? copy = null
    )
    {
        _allItems = entries
            .Select(entry => new RecentTranscriptionPaletteItem(entry, BuildSubtitle(entry)))
            .ToList();
        _onSelect = onSelect;
        _cleanAgain = cleanAgain;
        _copy = copy;
        RefreshFilteredEntries();
    }

    public ObservableCollection<RecentTranscriptionPaletteItem> FilteredEntries { get; } = [];
    public bool HasFilteredEntries => FilteredEntries.Count > 0;
    public string SelectedFinalText => SelectedItem?.FinalText ?? "";
    public string SelectedOriginalText => SelectedItem?.Entry.RawText ?? "";
    public string SelectedCleanupStatus => SelectedItem?.Entry.CleanupOutcome?.FallbackReason is { } reason
        ? CleanupOutcomeText.ForFallback(reason) : "";

    private bool CanCopyOriginal() => !_closed && _copy is not null && !string.IsNullOrWhiteSpace(SelectedItem?.Entry.RawText);
    private bool CanCleanAgain() => CanCopyOriginal() && _cleanAgain is not null && !IsRecovering;
    private bool CanCopyRecovery() => !_closed && _copy is not null && !string.IsNullOrWhiteSpace(RecoveryText);

    [RelayCommand(CanExecute = nameof(CanCopyOriginal))]
    private Task CopyOriginalAsync() => CopyAsync(SelectedOriginalText);

    [RelayCommand(CanExecute = nameof(CanCopyRecovery))]
    private Task CopyRecoveryAsync() => CopyAsync(RecoveryText);

    private async Task CopyAsync(string text)
    {
        if (_copy is null || string.IsNullOrWhiteSpace(text)) return;
        try
        {
            var result = await _copy(text);
            if (!_closed)
                RecoveryStatus = Loc.Instance[result == InsertionResult.CopiedToClipboard
                    ? "RecentTranscriptions.CopiedToClipboard" : "RecentTranscriptions.InsertionFailed"];
        }
        catch (Exception)
        {
            if (!_closed) RecoveryStatus = Loc.Instance["RecentTranscriptions.InsertionFailed"];
        }
    }

    [RelayCommand(CanExecute = nameof(CanCleanAgain))]
    private async Task CleanAgainAsync()
    {
        if (SelectedItem is not { } item || _cleanAgain is null) return;
        using var cts = new CancellationTokenSource();
        _recoveryCts = cts;
        RecoveryText = "";
        RecoveryStatus = Loc.Instance["Recovery.Cleaning"];
        IsRecovering = true;
        try
        {
            var result = await _cleanAgain(item.Entry, cts.Token);
            if (_closed || cts.IsCancellationRequested || !ReferenceEquals(SelectedItem, item)) return;
            RecoveryText = result.Text;
            RecoveryStatus = result.Outcome.FallbackReason is { } reason
                ? CleanupOutcomeText.ForFallback(reason) : Loc.Instance["Recovery.Ready"];
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!_closed && !cts.IsCancellationRequested && ReferenceEquals(SelectedItem, item))
                RecoveryStatus = Loc.Instance["Recovery.Failed"];
        }
        finally
        {
            if (ReferenceEquals(_recoveryCts, cts)) _recoveryCts = null;
            IsRecovering = false;
        }
    }

    public void CancelRecovery()
    {
        _closed = true;
        _recoveryCts?.Cancel();
        CleanAgainCommand.NotifyCanExecuteChanged();
        CopyOriginalCommand.NotifyCanExecuteChanged();
        CopyRecoveryCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedItemChanged(RecentTranscriptionPaletteItem? value)
    {
        _recoveryCts?.Cancel();
        RecoveryText = "";
        RecoveryStatus = "";
        OnPropertyChanged(nameof(SelectedFinalText));
        OnPropertyChanged(nameof(SelectedOriginalText));
        OnPropertyChanged(nameof(SelectedCleanupStatus));
        CleanAgainCommand.NotifyCanExecuteChanged();
        CopyOriginalCommand.NotifyCanExecuteChanged();
    }

    public void MoveSelection(int offset)
    {
        if (FilteredEntries.Count == 0)
        {
            return;
        }

        var currentIndex = SelectedItem is null ? -1 : FilteredEntries.IndexOf(SelectedItem);
        var nextIndex = Math.Clamp(currentIndex + offset, 0, FilteredEntries.Count - 1);
        SelectedItem = FilteredEntries[nextIndex];
    }

    // ReSharper disable once UnusedMember.Global  public API surface (palette confirm-selection entry point alongside MoveSelection/Select); not currently called in-tree
    public void SelectCurrent()
    {
        Select(SelectedItem ?? FilteredEntries.FirstOrDefault());
    }

    public void Select(RecentTranscriptionPaletteItem? item)
    {
        if (item is null)
        {
            return;
        }

        _onSelect(item);
    }

    partial void OnSearchQueryChanged(string value)
    {
        RefreshFilteredEntries();
    }

    private void RefreshFilteredEntries()
    {
        var query = SearchQuery.Trim();
        var filtered = string.IsNullOrEmpty(query)
            ? _allItems
            : _allItems.Where(item => item.Matches(query)).ToList();

        FilteredEntries.Clear();
        foreach (var item in filtered)
        {
            FilteredEntries.Add(item);
        }

        SelectedItem = FilteredEntries.FirstOrDefault();
        OnPropertyChanged(nameof(HasFilteredEntries));
    }

    private static string BuildSubtitle(RecentTranscriptionEntry entry)
    {
        var appName = !string.IsNullOrWhiteSpace(entry.AppName)
            ? entry.AppName
            : entry.AppProcessName;
        var timestamp =
            entry.Timestamp.Kind == DateTimeKind.Utc
                ? entry.Timestamp.ToLocalTime()
                : entry.Timestamp;
        var time = timestamp.ToString("g");

        return string.IsNullOrWhiteSpace(appName) ? time : $"{appName} - {time}";
    }
}

public sealed record RecentTranscriptionPaletteItem(RecentTranscriptionEntry Entry, string Subtitle)
{
    public string FinalText => Entry.FinalText;

    public bool Matches(string query)
    {
        return FinalText.Contains(query, StringComparison.OrdinalIgnoreCase)
               || Subtitle.Contains(query, StringComparison.OrdinalIgnoreCase)
               || (Entry.AppName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
               || (Entry.AppProcessName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
    }
}
