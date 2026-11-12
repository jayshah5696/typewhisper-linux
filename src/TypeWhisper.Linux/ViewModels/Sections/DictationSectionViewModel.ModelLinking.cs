using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Linux.Services.Localization;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class DictationSectionViewModel
{
    private LocalModelCard? _linkingCard;
    private CancellationTokenSource? _modelLinkCts;
    private bool HasLocalModelOperation => _testingCard is not null || _linkingCard is not null;
    public string SelectedModelRemoveText => SelectedModel is { } model && IsLocalModelLinked(model.ModelId)
        ? Loc.Instance["Dictation.ModelUnlink"] : Loc.Instance["Dictation.DeleteModelFiles"];
    public bool IsLocalModelLinked(string modelId) => _models.GetLinkedModelPath(modelId) is not null;

    public async Task LinkLocalModelAsync(LocalModelCard card, string sourcePath)
    {
        if (!card.CanLink || HasLocalModelOperation || IsRecording) return;
        using var cancel = new CancellationTokenSource();
        _modelLinkCts = cancel;
        _linkingCard = card;
        card.LinkError = null;
        card.LinkCanceled = false;
        card.LinkCompleted = false;
        card.IsLinking = true;
        StatusText = Loc.Instance.GetString("Dictation.ModelLinking", card.DisplayName);
        RefreshLocalModelCards(checkDisk: false);
        try
        {
            await Task.Run(() => _models.LinkModelAsync(card.ModelId, sourcePath, cancel.Token), cancel.Token);
            card.LinkCompleted = true;
            StatusText = Loc.Instance.GetString("Dictation.ModelLinked", card.DisplayName);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            card.LinkCanceled = true;
            StatusText = Loc.Instance["Dictation.ModelLinkCanceled"];
        }
        catch (Exception ex)
        {
            card.LinkError = ex.Message;
            StatusText = Loc.Instance.GetString("Dictation.ModelLinkFailed", FormatModelStatusError(ex.Message));
        }
        finally
        {
            _modelLinkCts = null;
            _linkingCard = null;
            card.IsLinking = false;
            RefreshLocalModelCards(checkDisk: true);
            OnPropertyChanged(nameof(SelectedModelRemoveText));
        }
    }

    [RelayCommand]
    private void CancelLocalModelLink() => _modelLinkCts?.Cancel();
}
