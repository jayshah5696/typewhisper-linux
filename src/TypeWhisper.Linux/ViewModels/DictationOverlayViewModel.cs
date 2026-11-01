using Avalonia.Threading;
using Avalonia.Layout;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;

namespace TypeWhisper.Linux.ViewModels;

// MVVM Toolkit [ObservableProperty] generates the On<Property>Changed(value) partial hooks; the
// value parameter is part of the generated signature and cannot be dropped even when ignored here.
// ReSharper disable UnusedParameterInPartialMethod
public partial class DictationOverlayViewModel : ObservableObject
{
    // 5 samples of audio-level history feed the waveform dots; chosen to match
    // the bubble's footprint and give a perceptible rolling motion at the
    // ~10 Hz cadence of LevelChanged.
    private const int WaveformSampleCount = 5;

    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _feedbackTimer;
    private readonly Func<string?, bool> _openUrl;
    private readonly OverlayCoordinator? _overlayCoordinator;
    private readonly Action<Action> _postToUiThread;
    private readonly DispatcherTimer _recordingTimer;
    private readonly ISettingsService _settings;
    private readonly float[] _waveformLevels = new float[WaveformSampleCount];

    private bool _applyingCoordinatorPresentation;
    private long _lastCoordinatorRevision;
    private readonly Func<Task>? _startDictation;
    private readonly Func<Task>? _stopDictation;
    private readonly Func<Task>? _cancelDictation;
    private bool _startingControl;
    private bool _dictationOwnsPresentation;
    private bool _deliverySucceeded;
    private OverlayPresentationToken? _presentedToken;
    private readonly HashSet<OverlayPresentationToken> _stoppingControls = [];
    private readonly HashSet<OverlayPresentationToken> _cancelingControls = [];
    private readonly Func<OverlayPresentationToken, Task>? _stopPresentedWork;
    private readonly Func<OverlayPresentationToken, Task>? _cancelPresentedWork;
    private readonly Func<string, Task<bool>>? _copyRecoveryText;
    private string? _recoveryText;
    private bool _copyRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCompactLauncher))]
    [NotifyPropertyChangedFor(nameof(ShowLauncherActions))]
    [NotifyPropertyChangedFor(nameof(LauncherButtonHeight))]
    [NotifyPropertyChangedFor(nameof(LauncherButtonWidth))]
    [NotifyPropertyChangedFor(nameof(LauncherSpacing))]
    [NotifyPropertyChangedFor(nameof(LauncherActionLabel))]
    private bool _isLauncherExpanded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDockPreviewVisible))]
    [NotifyPropertyChangedFor(nameof(DockTargetText))]
    private OverlayPosition? _dockTargetEdge;

    public bool IsDockPreviewVisible => DockTargetEdge is not null;
    public string DockTargetText => DockTargetEdge is { } edge
        ? Loc.Instance.GetString("Overlay.DockTarget", Loc.Instance["Appearance.Position" + edge]) : "";
    public double QuietWidth => IsSideDock ? 18 : 36;
    public double QuietHeight => IsSideDock ? 36 : 18;

    [ObservableProperty]
    private string? _activeAppName;

    [ObservableProperty]
    private string? _activeProfileName;

    [ObservableProperty]
    private float _audioLevel;

    [ObservableProperty]
    private bool _feedbackIsError;

    [ObservableProperty]
    private int? _feedbackDurationMilliseconds;

    [ObservableProperty]
    private string? _feedbackText;

    [ObservableProperty]
    private string? _actionResultUrl;

    [ObservableProperty]
    private bool _isOverlayVisible;

    [ObservableProperty]
    private bool _isRecording;

    [ObservableProperty]
    private string? _llmResponseText;

    [ObservableProperty]
    private string? _partialText;

    [ObservableProperty]
    private double _recordingSeconds;

    private DateTime? _sessionStartedAtUtc;

    [ObservableProperty]
    private bool _showFeedback;

    [ObservableProperty]
    private string _statusText = Loc.Instance["Overlay.Ready"];

    public DictationOverlayViewModel(
        AudioRecordingService audio,
        ISettingsService settings,
        IDetectionFailureTracker failureTracker,
        UrlLauncher urlLauncher,
        OverlayCoordinator overlayCoordinator,
        DictationOrchestrator dictation,
        TextInsertionService insertion
    )
        : this(
            settings,
            static action => Dispatcher.UIThread.Post(action),
            openUrl: urlLauncher.Open,
            overlayCoordinator: overlayCoordinator,
            failureTracker: failureTracker,
            startDictation: async () => { await dictation.StartAsync(); },
            stopDictation: dictation.StopAsync,
            cancelDictation: dictation.CancelAsync,
            stopPresentedWork: dictation.StopOverlayAsync,
            cancelPresentedWork: dictation.CancelOverlayAsync,
            copyRecoveryText: async text => await insertion.InsertTextAsync(new TextInsertionRequest(text, AutoPaste: false))
                == InsertionResult.CopiedToClipboard
        )
    {
        SubscribeToAudioLevels(audio);
    }

    // Test seam: production posts non-audio service events to Avalonia's UI thread; tests can
    // run that path synchronously and optionally exercise the shared direct audio subscription.
    internal DictationOverlayViewModel(
        ISettingsService settings,
        Action<Action> postToUiThread,
        AudioRecordingService? audio = null,
        Func<string?, bool>? openUrl = null,
        OverlayCoordinator? overlayCoordinator = null,
        IDetectionFailureTracker? failureTracker = null,
        Func<Task>? startDictation = null,
        Func<Task>? stopDictation = null,
        Func<Task>? cancelDictation = null,
        Func<OverlayPresentationToken, Task>? stopPresentedWork = null,
        Func<OverlayPresentationToken, Task>? cancelPresentedWork = null,
        Func<string, Task<bool>>? copyRecoveryText = null
    )
    {
        _settings = settings;
        _startDictation = startDictation;
        _stopDictation = stopDictation;
        _cancelDictation = cancelDictation;
        _stopPresentedWork = stopPresentedWork;
        _cancelPresentedWork = cancelPresentedWork;
        _copyRecoveryText = copyRecoveryText;
        _postToUiThread = postToUiThread;
        _openUrl = openUrl ?? (_ => false);
        _overlayCoordinator = overlayCoordinator;
        if (audio is not null)
        {
            SubscribeToAudioLevels(audio);
        }

        if (failureTracker is not null)
        {
            failureTracker.OnFailure += (_, e) =>
            {
                if (!e.ShouldShowPersistentBanner)
                {
                    // The detailed English reason is already in the error log; the toast is
                    // UI-facing and must be localized.
                    ShowSystemErrorFeedback(Loc.Instance["Overlay.WindowDetectionFailed"]);
                }
            };
        }

        _recordingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _recordingTimer.Tick += (_, _) => RecordingTimerTick();

        // LeftText/RightText render DateTime.Now with minute resolution. Polling once per second
        // keeps a minute rollover's visible delay below one second without re-arming for wall-clock
        // alignment; this timer is stopped whenever no clock slot is actually visible.
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => ClockTimerTick();

        // Interval is set per arm so live setting changes take effect on the
        // next event. RestartFeedbackTimer re-arms even when ShowFeedback is
        // already true — plain re-assignment is skipped by value equality.
        _feedbackTimer = new DispatcherTimer();
        _feedbackTimer.Tick += (_, _) =>
        {
            _feedbackTimer.Stop();
            ShowFeedback = false;
            FeedbackText = null;
            ActionResultUrl = null;
            FeedbackDurationMilliseconds = null;
            OnPropertyChanged(nameof(HasVisibleContent));
        };

        if (overlayCoordinator is not null)
        {
            overlayCoordinator.PresentationChanged += OnCoordinatorPresentationChanged;
        }

        _settings.SettingsChanged += _ => _postToUiThread(RefreshOverlaySlots);
        Loc.Instance.LanguageChanged += (_, _) => _postToUiThread(() =>
        {
            OnPropertyChanged(nameof(LauncherActionLabel));
            OnPropertyChanged(nameof(DockTargetText));
        });
    }

    private void SubscribeToAudioLevels(AudioRecordingService audio)
    {
        audio.LevelChanged += (_, level) =>
        {
            if (IsRecording)
            {
                // Raw RMS is typically well below 0.1 for speech, so amplify ×8 to drive a
                // visible meter — same scaling the recorder and wizard VMs apply.
                AudioLevel = Math.Clamp(level * 8, 0f, 1f);
            }
        };
    }

    public bool HasVisibleContent => IsOverlayVisible || ShowFeedback || ShowLauncher;

    public bool ShowLauncher => _settings.Current.ShowFloatingControl && !IsOverlayVisible && !ShowFeedback;
    public bool ShowCompactLauncher => ShowLauncher && !IsLauncherExpanded;
    public bool ShowLauncherActions => ShowLauncher && IsLauncherExpanded;
    public bool ShowRecordingControls => _settings.Current.ShowFloatingControl && _dictationOwnsPresentation && IsOverlayVisible && IsRecording;
    public bool ShowProcessingControls => _settings.Current.ShowFloatingControl && _dictationOwnsPresentation && IsOverlayVisible && !IsRecording && !ShowFeedback;
    public bool ShowCompactWorkflow => ShowRecordingControls || ShowProcessingControls;
    public bool ShowLegacyWorkflow => IsOverlayVisible && !ShowFeedback && !ShowCompactWorkflow;
    public bool ShowSuccessConfirmation => _settings.Current.ShowFloatingControl && ShowFeedback && _deliverySucceeded
        && !FeedbackIsError && !HasActionResultUrl;
    public bool ShowDetailedFeedback => ShowFeedback && !ShowSuccessConfirmation;
    public bool IsSideDock => _settings.Current.OverlayPosition is OverlayPosition.Left or OverlayPosition.Right;
    public double QuietMarkWidth => IsSideDock ? 3 : 18;
    public double QuietMarkHeight => IsSideDock ? 18 : 3;
    public double LauncherButtonWidth => IsLauncherExpanded ? 32 : IsSideDock ? 10 : 28;
    public double LauncherSpacing => IsLauncherExpanded ? 4 : 0;
    public Orientation ControlOrientation => IsSideDock ? Orientation.Vertical : Orientation.Horizontal;
    public bool HasRecoveryText => ShowDetailedFeedback && !string.IsNullOrWhiteSpace(_recoveryText);
    private bool CanCopyRecovery() => HasRecoveryText && !_copyRunning && _copyRecoveryText is not null;

    [RelayCommand(CanExecute = nameof(CanCopyRecovery))]
    private async Task CopyRecovery()
    {
        if (!CanCopyRecovery()) return;
        var text = _recoveryText!;
        var revision = _lastCoordinatorRevision;
        _copyRunning = true;
        CopyRecoveryCommand.NotifyCanExecuteChanged();
        try
        {
            bool success;
            try { success = await _copyRecoveryText!(text); }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[FloatingControl] Recovery copy failed: {FailureMessageSanitizer.Sanitize(ex.Message)}");
                success = false;
            }
            if (revision != _lastCoordinatorRevision || !ShowDetailedFeedback) return;
            var message = Loc.Instance[success ? "Overlay.RecoveryCopied" : "Overlay.RecoveryCopyFailed"];
            if (_overlayCoordinator is not null)
                _overlayCoordinator.ReplacePresentedFeedback(state => state with
                {
                    FeedbackText = message, FeedbackIsError = !success, DeliverySucceeded = false,
                    RecoveryText = success ? null : text,
                });
            else
            {
                FeedbackText = message;
                FeedbackIsError = !success;
                _recoveryText = success ? null : text;
            }
        }
        finally
        {
            _copyRunning = false;
            RefreshLauncherState();
        }
    }

    public event EventHandler? OpenRecorderRequested;
    public event EventHandler? OpenSettingsRequested;

    public void SetLauncherExpanded(bool expanded) => IsLauncherExpanded = expanded && ShowLauncher;

    public double LauncherButtonHeight => IsLauncherExpanded ? 28 : IsSideDock ? 28 : 10;
    public string LauncherActionLabel => Loc.Instance[IsLauncherExpanded ? "Overlay.StartDictation" : "Overlay.ShowControls"];

    [RelayCommand(CanExecute = nameof(CanStartDictation))]
    private async Task ActivateLauncher()
    {
        if (!CanStartDictation())
            return;
        if (!IsLauncherExpanded)
            SetLauncherExpanded(true);
        else
            await StartDictation();
    }

    [RelayCommand]
    private void OpenRecorder() => OpenRecorderRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void OpenSettings() => OpenSettingsRequested?.Invoke(this, EventArgs.Empty);

    private bool CanStartDictation() => ShowLauncher && !_startingControl && _startDictation is not null;
    private bool CanStopDictation() => ShowRecordingControls && _presentedToken is { } owner && !_stoppingControls.Contains(owner)
        && (_stopDictation is not null || _stopPresentedWork is not null);
    private bool CanCancelDictation() => ShowCompactWorkflow && _presentedToken is { } owner && !_cancelingControls.Contains(owner)
        && (_cancelDictation is not null || _cancelPresentedWork is not null);

    [RelayCommand(CanExecute = nameof(CanStartDictation))]
    private async Task StartDictation()
    {
        if (CanStartDictation())
            await RunControlCommandAsync(_startDictation!);
    }

    [RelayCommand(CanExecute = nameof(CanStopDictation), AllowConcurrentExecutions = true)]
    private async Task StopDictation()
    {
        if (CanStopDictation() && _presentedToken is { } owner)
            await RunControlCommandAsync(() => _stopPresentedWork?.Invoke(owner) ?? _stopDictation!(), owner, _stoppingControls);
    }

    [RelayCommand(CanExecute = nameof(CanCancelDictation), AllowConcurrentExecutions = true)]
    private async Task CancelDictation()
    {
        if (CanCancelDictation() && _presentedToken is { } owner)
            await RunControlCommandAsync(() => _cancelPresentedWork?.Invoke(owner) ?? _cancelDictation!(), owner, _cancelingControls);
    }

    private async Task RunControlCommandAsync(Func<Task> command, OverlayPresentationToken? owner = null,
        HashSet<OverlayPresentationToken>? lane = null)
    {
        if (lane is not null && owner is not null) lane.Add(owner);
        else _startingControl = true;
        RefreshLauncherState();
        try
        {
            await command();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[FloatingControl] {ex.GetType().Name}: {FailureMessageSanitizer.Sanitize(ex.Message)}");
            if (owner is null || ReferenceEquals(owner, _presentedToken))
                ShowSystemErrorFeedback(Loc.Instance["Overlay.ControlFailed"]);
        }
        finally
        {
            if (lane is not null && owner is not null) lane.Remove(owner);
            else _startingControl = false;
            RefreshLauncherState();
        }
    }

    private void RefreshLauncherState()
    {
        if (!ShowLauncher)
            IsLauncherExpanded = false;
        OnPropertyChanged(nameof(ShowLauncher));
        OnPropertyChanged(nameof(ShowCompactLauncher));
        OnPropertyChanged(nameof(ShowLauncherActions));
        OnPropertyChanged(nameof(ShowRecordingControls));
        OnPropertyChanged(nameof(ShowProcessingControls));
        OnPropertyChanged(nameof(ShowCompactWorkflow));
        OnPropertyChanged(nameof(ShowLegacyWorkflow));
        OnPropertyChanged(nameof(ShowSuccessConfirmation));
        OnPropertyChanged(nameof(ShowDetailedFeedback));
        OnPropertyChanged(nameof(QuietMarkWidth));
        OnPropertyChanged(nameof(QuietMarkHeight));
        OnPropertyChanged(nameof(LauncherButtonWidth));
        OnPropertyChanged(nameof(LauncherButtonHeight));
        OnPropertyChanged(nameof(IsSideDock));
        OnPropertyChanged(nameof(ControlOrientation));
        OnPropertyChanged(nameof(HasRecoveryText));
        CopyRecoveryCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(QuietWidth));
        OnPropertyChanged(nameof(QuietHeight));
        OnPropertyChanged(nameof(HasVisibleContent));
        ActivateLauncherCommand.NotifyCanExecuteChanged();
        StartDictationCommand.NotifyCanExecuteChanged();
        StopDictationCommand.NotifyCanExecuteChanged();
        CancelDictationCommand.NotifyCanExecuteChanged();
    }

    public bool HasActionResultUrl => !string.IsNullOrWhiteSpace(ActionResultUrl);

    // Floating live text is shown by LiveTextWindow instead.
    public bool ShowInlinePartialText =>
        !_settings.Current.LiveTextFloating && !string.IsNullOrEmpty(PartialText);

    public string RecordingTimerText
    {
        get
        {
            var totalSeconds = Math.Max(0, (int)Math.Floor(RecordingSeconds));
            var minutes = totalSeconds / 60;
            var seconds = totalSeconds % 60;
            return $"{minutes}:{seconds:00}";
        }
    }

    // Single pulsing dot: 10px at silence, grows to 18px at peak level.
    public double IndicatorSize => 10 + PerceptualLevel(AudioLevel) * 8;

    // Each bar reflects one slot of the rolling buffer: 4px at silence, 18px at peak.
    public double WaveformBar0Height => BarHeight(_waveformLevels[0]);
    public double WaveformBar1Height => BarHeight(_waveformLevels[1]);
    public double WaveformBar2Height => BarHeight(_waveformLevels[2]);
    public double WaveformBar3Height => BarHeight(_waveformLevels[3]);
    public double WaveformBar4Height => BarHeight(_waveformLevels[4]);

    public string FeedbackForeground => FeedbackIsError ? "#FF8888" : "#66E3A2";

    public bool ShowLeftIndicator =>
        _settings.Current.OverlayLeftWidget == OverlayWidget.Indicator;

    public bool ShowLeftWaveform =>
        _settings.Current.OverlayLeftWidget == OverlayWidget.Waveform;

    public bool ShowLeftText => IsTextWidget(_settings.Current.OverlayLeftWidget);

    public string LeftText => ResolveText(_settings.Current.OverlayLeftWidget);

    public bool ShowRightIndicator =>
        _settings.Current.OverlayRightWidget == OverlayWidget.Indicator;

    public bool ShowRightWaveform =>
        _settings.Current.OverlayRightWidget == OverlayWidget.Waveform;

    public bool ShowRightText => IsTextWidget(_settings.Current.OverlayRightWidget);

    public string RightText => ResolveText(_settings.Current.OverlayRightWidget);

    private static double BarHeight(float level)
    {
        return 4 + PerceptualLevel(level) * 14;
    }

    // sqrt curve pulls quiet/medium levels closer to the top of the range so
    // the meter reads as responsive without requiring shouting.
    private static double PerceptualLevel(float level)
    {
        return Math.Sqrt(Math.Clamp(level, 0f, 1f));
    }

    partial void OnIsRecordingChanged(bool value) => RefreshLauncherState();

    partial void OnIsOverlayVisibleChanged(bool value)
    {
        RefreshLauncherState();
        UpdateClockTimer();
    }

    partial void OnShowFeedbackChanged(bool value)
    {
        RefreshLauncherState();

        _feedbackTimer.Stop();
        if (value && !_applyingCoordinatorPresentation)
        {
            ArmFeedbackAutoHideTimer();
        }
    }

    private void RestartFeedbackTimer()
    {
        _feedbackTimer.Stop();
        ArmFeedbackAutoHideTimer();
    }

    private void ArmFeedbackAutoHideTimer()
    {
        var globalMilliseconds = AppSettings.NormalizePreviewBubbleAutoHideMilliseconds(
            _settings.Current.PreviewBubbleAutoHideMilliseconds);
        var milliseconds = globalMilliseconds == 0
            ? 0
            : AppSettings.NormalizePreviewBubbleAutoHideMilliseconds(
                FeedbackDurationMilliseconds ?? globalMilliseconds
            );

        if (milliseconds <= 0)
        {
            _feedbackTimer.Stop();
            ShowFeedback = false;
            FeedbackText = null;
            ActionResultUrl = null;
            FeedbackDurationMilliseconds = null;
            OnPropertyChanged(nameof(HasVisibleContent));
            return;
        }

        _feedbackTimer.Interval = TimeSpan.FromMilliseconds(milliseconds);
        _feedbackTimer.Start();
    }

    partial void OnRecordingSecondsChanged(double value)
    {
        OnPropertyChanged(nameof(RecordingTimerText));
    }

    partial void OnAudioLevelChanged(float value)
    {
        for (var i = 0; i < WaveformSampleCount - 1; i++)
        {
            _waveformLevels[i] = _waveformLevels[i + 1];
        }

        _waveformLevels[WaveformSampleCount - 1] = value;

        OnPropertyChanged(nameof(IndicatorSize));
        OnPropertyChanged(nameof(WaveformBar0Height));
        OnPropertyChanged(nameof(WaveformBar1Height));
        OnPropertyChanged(nameof(WaveformBar2Height));
        OnPropertyChanged(nameof(WaveformBar3Height));
        OnPropertyChanged(nameof(WaveformBar4Height));
    }

    partial void OnFeedbackIsErrorChanged(bool value)
    {
        OnPropertyChanged(nameof(FeedbackForeground));
    }

    partial void OnActionResultUrlChanged(string? value)
    {
        OnPropertyChanged(nameof(HasActionResultUrl));
    }

    partial void OnPartialTextChanged(string? value)
    {
        OnPropertyChanged(nameof(ShowInlinePartialText));
    }

    [RelayCommand]
    private void OpenActionResult()
    {
        var url = ActionResultUrl;
        if (string.IsNullOrWhiteSpace(url) || _openUrl(url))
        {
            return;
        }

        // Swap the presented feedback in place so the coordinator stays the sole render
        // path and re-arms its expiry for the replacement; rewriting VM properties here
        // would leave the original expiry armed and cut the replacement short. When the
        // feedback already expired, surface the failure as a fresh system toast instead.
        var message = Loc.Instance["ActionResult.OpenFailed"];
        if (_overlayCoordinator is null
            || !_overlayCoordinator.ReplacePresentedFeedback(state => state with
            {
                ActionResultUrl = null,
                FeedbackDurationMilliseconds = null,
                FeedbackText = message,
                FeedbackIsError = true,
                ShowFeedback = true,
            }))
        {
            ShowSystemErrorFeedback(message);
        }
    }

    // Sole-render-path invariant: with a coordinator wired, every repaint must flow through
    // its arbitration — a direct property write here would be invisible to the coordinator,
    // which suppresses repaints while its own presented state is unchanged and so could
    // never paint over it again.
    private void ShowSystemErrorFeedback(string message)
    {
        // ReSharper disable once InlineTemporaryVariable -- the pattern variable is the null-checked snapshot of a field another thread can clear.
        if (_overlayCoordinator is { } coordinator)
        {
            coordinator.Show(
                coordinator.Acquire(OverlayRequester.System),
                new DictationOverlayState
                {
                    ShowFeedback = true,
                    FeedbackIsError = true,
                    FeedbackText = message,
                }
            );
            return;
        }

        _postToUiThread(() =>
        {
            ActionResultUrl = null;
            FeedbackDurationMilliseconds = null;
            FeedbackText = message;
            FeedbackIsError = true;
            ShowFeedback = true;
            RestartFeedbackTimer();
        });
    }

    internal void ApplyState(DictationOverlayState state)
    {
        ApplyStateCore(state, restartFeedbackTimer: true);
    }

    private void OnCoordinatorPresentationChanged(
        object? sender,
        OverlayPresentationChangedEventArgs presentation
    )
    {
        if (presentation.Revision <= _lastCoordinatorRevision)
        {
            return;
        }

        _lastCoordinatorRevision = presentation.Revision;
        _dictationOwnsPresentation = presentation.Requester == OverlayRequester.Dictation;
        _presentedToken = presentation.Token;
        _feedbackTimer.Stop();
        _applyingCoordinatorPresentation = true;
        try
        {
            ApplyStateCore(presentation.State, restartFeedbackTimer: false);
        }
        finally
        {
            _applyingCoordinatorPresentation = false;
        }
    }

    private void ApplyStateCore(
        DictationOverlayState state,
        bool restartFeedbackTimer
    )
    {
        var wasShowingFeedback = ShowFeedback;
        _deliverySucceeded = state.DeliverySucceeded;
        _recoveryText = state.RecoveryText;
        IsOverlayVisible = state.IsOverlayVisible;
        FeedbackIsError = state.FeedbackIsError;
        FeedbackDurationMilliseconds = state.FeedbackDurationMilliseconds;
        FeedbackText = state.FeedbackText;
        ActionResultUrl = state.ActionResultUrl;
        StatusText = state.StatusText;
        PartialText = state.PartialText;
        LlmResponseText = state.LlmResponseText;
        ActiveProfileName = state.ActiveProfileName;
        ActiveAppName = state.ActiveAppName;
        _sessionStartedAtUtc = state.SessionStartedAtUtc;
        IsRecording = state.IsRecording;
        ShowFeedback = state.ShowFeedback;
        if (restartFeedbackTimer && wasShowingFeedback && state.ShowFeedback)
        {
            RestartFeedbackTimer();
        }

        if (IsRecording && _sessionStartedAtUtc is not null)
        {
            RefreshRecordingSeconds();
            _recordingTimer.Start();
        }
        else
        {
            _recordingTimer.Stop();
            RecordingSeconds = 0;
            AudioLevel = 0f;
            Array.Clear(_waveformLevels);
            OnPropertyChanged(nameof(WaveformBar0Height));
            OnPropertyChanged(nameof(WaveformBar1Height));
            OnPropertyChanged(nameof(WaveformBar2Height));
            OnPropertyChanged(nameof(WaveformBar3Height));
            OnPropertyChanged(nameof(WaveformBar4Height));
        }

        RefreshOverlaySlots();
    }

    private void RefreshRecordingSeconds()
    {
        if (_sessionStartedAtUtc is not { } startedAt)
        {
            RecordingSeconds = 0;
            return;
        }

        RecordingSeconds = Math.Max(0, (DateTime.UtcNow - startedAt).TotalSeconds);
    }

    internal void RecordingTimerTick()
    {
        RefreshRecordingSeconds();
        NotifyTextSlots(OverlayWidget.Timer);
    }

    internal void ClockTimerTick()
    {
        NotifyTextSlots(OverlayWidget.Clock);
    }

    internal bool IsClockTimerRunning => _clockTimer.IsEnabled;

    internal bool IsFeedbackTimerRunning => _feedbackTimer.IsEnabled;

    internal TimeSpan FeedbackTimerInterval => _feedbackTimer.Interval;

    private void RefreshOverlaySlots()
    {
        RefreshLauncherState();
        OnPropertyChanged(nameof(ShowLeftIndicator));
        OnPropertyChanged(nameof(ShowLeftWaveform));
        OnPropertyChanged(nameof(ShowLeftText));
        OnPropertyChanged(nameof(LeftText));
        OnPropertyChanged(nameof(ShowRightIndicator));
        OnPropertyChanged(nameof(ShowRightWaveform));
        OnPropertyChanged(nameof(ShowRightText));
        OnPropertyChanged(nameof(RightText));
        OnPropertyChanged(nameof(ShowInlinePartialText));
        UpdateClockTimer();
    }

    private void NotifyTextSlots(OverlayWidget widget)
    {
        if (_settings.Current.OverlayLeftWidget == widget)
        {
            OnPropertyChanged(nameof(LeftText));
        }

        if (_settings.Current.OverlayRightWidget == widget)
        {
            OnPropertyChanged(nameof(RightText));
        }
    }

    private void UpdateClockTimer()
    {
        var shouldRun = IsOverlayVisible
                        && (_settings.Current.OverlayLeftWidget == OverlayWidget.Clock
                            || _settings.Current.OverlayRightWidget == OverlayWidget.Clock);

        if (shouldRun)
        {
            _clockTimer.Start();
        }
        else
        {
            _clockTimer.Stop();
        }
    }

    private static bool IsTextWidget(OverlayWidget widget)
    {
        return widget
            is OverlayWidget.Timer
            or OverlayWidget.Clock
            or OverlayWidget.Profile
            or OverlayWidget.HotkeyMode
            or OverlayWidget.AppName;
    }

    private string ResolveText(OverlayWidget widget)
    {
        return widget switch
        {
            OverlayWidget.Timer => RecordingTimerText,
            OverlayWidget.Clock => DateTime.Now.ToString("t"),
            OverlayWidget.Profile => ActiveProfileName ?? "",
            OverlayWidget.HotkeyMode => _settings.Current.Mode switch
            {
                RecordingMode.Toggle => Loc.Instance["Common.ModeToggle"],
                RecordingMode.PushToTalk => Loc.Instance["Common.ModePushToTalk"],
                RecordingMode.Hybrid => Loc.Instance["Common.ModeHybrid"],
                _ => "",
            },
            OverlayWidget.AppName => ActiveAppName ?? "",
            // Indicator, Waveform and None render no text; handled by the default arm.
            _ => "",
        };
    }
}
