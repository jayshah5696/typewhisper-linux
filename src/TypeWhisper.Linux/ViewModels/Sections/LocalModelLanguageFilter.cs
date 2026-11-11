using CommunityToolkit.Mvvm.ComponentModel;

namespace TypeWhisper.Linux.ViewModels.Sections;

/// <summary>A catalog filter, separate from the recognition language setting.</summary>
public sealed partial class LocalModelLanguageFilter(string code, string displayName) : ObservableObject
{
    public string Code { get; } = code;
    public string DisplayName { get; } = displayName;

    [ObservableProperty]
    private bool _isSelected;
}
