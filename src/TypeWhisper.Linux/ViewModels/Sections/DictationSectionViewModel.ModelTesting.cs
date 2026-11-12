using CommunityToolkit.Mvvm.Input;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class DictationSectionViewModel
{
    private CancellationTokenSource? _rawModelTestCts;
    private LocalModelCard? _testingCard;

    partial void OnIsRecordingChanged(bool value)
    {
        if (value)
        {
            _rawModelTestCts?.Cancel();
            _modelLinkCts?.Cancel();
        }
        RefreshLocalModelCards(checkDisk: false);
    }

    [RelayCommand]
    private async Task TestLocalModel(LocalModelCard? card)
    {
        if (card is not { CanTest: true } || HasLocalModelOperation || IsRecording) return;
        using var caller = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var running = CancellationTokenSource.CreateLinkedTokenSource(caller.Token, deadline.Token);
        _rawModelTestCts = caller;
        _testingCard = card;
        card.TestResult = null;
        card.TestError = null;
        card.TestCanceled = false;
        card.TestTimedOut = false;
        card.TestTranscript = "";
        card.IsTesting = true;
        RefreshLocalModelCards(checkDisk: false);
        try
        {
            // Discovery and native startup must not run synchronously on the UI thread.
            var result = await Task.Run(() => _models.TestModelAsync(card.ModelId, running.Token), running.Token);
            running.Token.ThrowIfCancellationRequested();
            card.TestResult = result;
            card.TestTranscript = result.Transcription.Text;
        }
        catch (OperationCanceledException) when (caller.IsCancellationRequested)
        {
            card.TestCanceled = true;
        }
        catch (Exception ex)
        {
            card.TestTimedOut = deadline.IsCancellationRequested;
            card.TestError = ex.Message;
        }
        finally
        {
            _rawModelTestCts = null;
            _testingCard = null;
            card.IsTesting = false;
            RefreshLocalModelCards(checkDisk: true);
        }
    }

    [RelayCommand]
    private void CancelLocalModelTest() => _rawModelTestCts?.Cancel();
}
