using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.ViewModels;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class DictationOverlayViewModelTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RecordingTimerTick_TimerInSideSlot_RaisesOnlyCorrespondingTextNotification(
        bool timerOnLeft
    )
    {
        var settings = new FakeSettingsService(
            AppSettings.Default with
            {
                OverlayLeftWidget = timerOnLeft
                    ? OverlayWidget.Timer
                    : OverlayWidget.Profile,
                OverlayRightWidget = timerOnLeft
                    ? OverlayWidget.Profile
                    : OverlayWidget.Timer,
            });
        var sut = CreateViewModel(settings);
        var propertyNames = TrackPropertyChanges(sut);

        sut.RecordingTimerTick();

        Assert.Equal(
            [timerOnLeft
                ? nameof(DictationOverlayViewModel.LeftText)
                : nameof(DictationOverlayViewModel.RightText)],
            SideTextNotifications(propertyNames)
        );
    }

    [Fact]
    public void RecordingTimerTick_NoTimerSlots_DoesNotRaiseSideTextNotifications()
    {
        var settings = new FakeSettingsService(
            AppSettings.Default with
            {
                OverlayLeftWidget = OverlayWidget.Profile,
                OverlayRightWidget = OverlayWidget.HotkeyMode,
            });
        var sut = CreateViewModel(settings);
        var propertyNames = TrackPropertyChanges(sut);

        sut.RecordingTimerTick();

        Assert.Empty(SideTextNotifications(propertyNames));
    }

    [Fact]
    public void ClockTimerTick_VisibleClockSlot_RaisesOnlyClockSlotTextNotification()
    {
        var settings = new FakeSettingsService(
            AppSettings.Default with
            {
                OverlayLeftWidget = OverlayWidget.Profile,
                OverlayRightWidget = OverlayWidget.Clock,
            });
        var sut = CreateViewModel(settings);
        sut.IsOverlayVisible = true;
        var propertyNames = TrackPropertyChanges(sut);

        sut.ClockTimerTick();

        Assert.Equal(
            [nameof(DictationOverlayViewModel.RightText)],
            SideTextNotifications(propertyNames)
        );

        sut.IsOverlayVisible = false;
    }

    [Fact]
    public void ClockTimer_TracksLiveSlotSettingAndOverlayVisibility()
    {
        var settings = new FakeSettingsService(
            AppSettings.Default with
            {
                OverlayLeftWidget = OverlayWidget.Clock,
                OverlayRightWidget = OverlayWidget.Profile,
            });
        var sut = CreateViewModel(settings);

        Assert.False(sut.IsClockTimerRunning);

        sut.IsOverlayVisible = true;

        Assert.True(sut.IsClockTimerRunning);

        settings.Change(
            settings.Current with
            {
                OverlayLeftWidget = OverlayWidget.Profile,
            });

        Assert.False(sut.IsClockTimerRunning);

        settings.Change(
            settings.Current with
            {
                OverlayRightWidget = OverlayWidget.Clock,
            });

        Assert.True(sut.IsClockTimerRunning);

        sut.IsOverlayVisible = false;

        Assert.False(sut.IsClockTimerRunning);
    }

    [Fact]
    public void AudioLevel_ServiceDeliveryUpdatesBeforeJobReturnsWithoutConsumerPost()
    {
        var serviceJobs = new List<Action>();
        var consumerPostCount = 0;
        using var audio = new AudioRecordingService(
            _ => { },
            () => 0,
            () => { },
            postToUiThread: serviceJobs.Add
        );
        var sut = new DictationOverlayViewModel(
            new FakeSettingsService(AppSettings.Default),
            _ => consumerPostCount++,
            audio
        )
        {
            IsRecording = true,
        };
        Assert.True(audio.StartPreview());

        audio.ProcessAudioBufferForTest([0.1f]);
        var serviceDelivery = Assert.Single(serviceJobs);

        serviceDelivery();

        Assert.Equal(0, consumerPostCount);
        Assert.Equal(0.8, sut.AudioLevel, precision: 5);
    }

    [Fact]
    public void AudioLevel_StoppedOverlayIgnoresLaterServiceDelivery()
    {
        var serviceJobs = new List<Action>();
        var consumerPostCount = 0;
        using var audio = new AudioRecordingService(
            _ => { },
            () => 0,
            () => { },
            postToUiThread: serviceJobs.Add
        );
        var sut = new DictationOverlayViewModel(
            new FakeSettingsService(AppSettings.Default),
            action =>
            {
                consumerPostCount++;
                action();
            },
            audio
        );
        Assert.True(audio.StartPreview());

        audio.ProcessAudioBufferForTest([0.1f]);
        Assert.Single(serviceJobs)();

        Assert.Equal(0, consumerPostCount);
        Assert.Equal(0, sut.AudioLevel);
    }

    [Fact]
    public void Action_result_uses_plugin_duration_override_and_offers_url()
    {
        var settings = new FakeSettingsService(
            AppSettings.Default with { PreviewBubbleAutoHideMilliseconds = 1200 }
        );
        var sut = CreateViewModel(settings);

        sut.ApplyState(
            new DictationOverlayState
            {
                ShowFeedback = true,
                FeedbackText = "Issue created",
                ActionResultUrl = "https://example.com/issues/42",
                FeedbackDurationMilliseconds = 5000,
            }
        );

        Assert.True(sut.ShowFeedback);
        Assert.True(sut.HasActionResultUrl);
        Assert.Equal("https://example.com/issues/42", sut.ActionResultUrl);
        Assert.Equal(TimeSpan.FromSeconds(5), sut.FeedbackTimerInterval);
    }

    [Fact]
    public void Global_zero_hides_action_feedback_even_with_plugin_duration()
    {
        var settings = new FakeSettingsService(
            AppSettings.Default with { PreviewBubbleAutoHideMilliseconds = 0 }
        );
        var sut = CreateViewModel(settings);

        sut.ApplyState(
            new DictationOverlayState
            {
                ShowFeedback = true,
                FeedbackText = "Issue created",
                ActionResultUrl = "https://example.com/issues/42",
                FeedbackDurationMilliseconds = 5000,
            }
        );

        Assert.False(sut.ShowFeedback);
        Assert.False(sut.HasActionResultUrl);
        Assert.Null(sut.ActionResultUrl);
    }

    [Fact]
    public void OpenActionResultCommand_never_opens_until_explicitly_invoked()
    {
        var openedUrls = new List<string?>();
        var sut = new DictationOverlayViewModel(
            new FakeSettingsService(AppSettings.Default),
            static action => action(),
            openUrl: url =>
            {
                openedUrls.Add(url);
                return true;
            }
        );

        sut.ApplyState(
            new DictationOverlayState
            {
                ShowFeedback = true,
                FeedbackText = "Issue created",
                ActionResultUrl = "https://example.com/issues/42",
                FeedbackDurationMilliseconds = 5000,
            }
        );

        Assert.Empty(openedUrls);

        sut.OpenActionResultCommand.Execute(null);

        Assert.Equal(["https://example.com/issues/42"], openedUrls);
    }

    [Fact]
    public void CoordinatorPresentation_RendersPropertiesWithoutViewModelExpiryTimer()
    {
        var settings = new FakeSettingsService(
            AppSettings.Default with { PreviewBubbleAutoHideMilliseconds = 1500 }
        );
        var scheduler = new FakeScheduler();
        var coordinator = new OverlayCoordinator(
            settings,
            static action => action(),
            scheduler.Schedule
        );
        var sut = new DictationOverlayViewModel(
            settings,
            static action => action(),
            overlayCoordinator: coordinator
        );
        var token = coordinator.Acquire(OverlayRequester.Dictation);

        Assert.True(coordinator.Show(token, new DictationOverlayState
        {
            ShowFeedback = true,
            FeedbackText = "Coordinator feedback",
            FeedbackIsError = true,
            FeedbackDurationMilliseconds = 5000,
        }));

        Assert.True(sut.ShowFeedback);
        Assert.Equal("Coordinator feedback", sut.FeedbackText);
        Assert.True(sut.FeedbackIsError);
        Assert.True(sut.HasVisibleContent);
        Assert.False(sut.IsFeedbackTimerRunning);
        Assert.Equal(TimeSpan.FromSeconds(5), scheduler.LastDelay);
    }

    [Fact]
    public void OpenActionResult_Failure_ReplacesCoordinatorFeedbackAndReArmsExpiry()
    {
        var settings = new FakeSettingsService(
            AppSettings.Default with { PreviewBubbleAutoHideMilliseconds = 1500 }
        );
        var scheduler = new FakeScheduler();
        var coordinator = new OverlayCoordinator(
            settings,
            static action => action(),
            scheduler.Schedule
        );
        var sut = new DictationOverlayViewModel(
            settings,
            static action => action(),
            openUrl: _ => false,
            overlayCoordinator: coordinator
        );
        var token = coordinator.Acquire(OverlayRequester.Transform);
        Assert.True(coordinator.Show(token, new DictationOverlayState
        {
            ShowFeedback = true,
            FeedbackText = "Issue created",
            ActionResultUrl = "https://example.com/issues/42",
            FeedbackDurationMilliseconds = 5000,
        }));
        Assert.True(sut.HasActionResultUrl);
        Assert.Equal(TimeSpan.FromSeconds(5), scheduler.LastDelay);

        sut.OpenActionResultCommand.Execute(null);

        // The replacement must be the coordinator's presented state — a VM-local rewrite
        // would leave the original expiry armed and be invisible to arbitration.
        var openFailed = Loc.Instance["ActionResult.OpenFailed"];
        Assert.Equal(openFailed, coordinator.PresentedState.FeedbackText);
        Assert.Null(coordinator.PresentedState.ActionResultUrl);
        Assert.Equal(openFailed, sut.FeedbackText);
        Assert.True(sut.FeedbackIsError);
        Assert.False(sut.HasActionResultUrl);
        // Expiry is re-armed for the replacement: the global duration, not the remainder
        // of the original 5 s claim.
        Assert.Equal(TimeSpan.FromMilliseconds(1500), scheduler.LastDelay);

        scheduler.FirePending();

        Assert.False(sut.ShowFeedback);
        Assert.Equal(DictationOverlayState.Hidden, coordinator.PresentedState);
    }

    [Fact]
    public void DetectionFailureToast_RoutesThroughCoordinatorAndWaitsBehindRecording()
    {
        var settings = new FakeSettingsService(
            AppSettings.Default with { PreviewBubbleAutoHideMilliseconds = 1500 }
        );
        var scheduler = new FakeScheduler();
        var coordinator = new OverlayCoordinator(
            settings,
            static action => action(),
            scheduler.Schedule
        );
        var tracker = new FakeDetectionFailureTracker();
        var sut = new DictationOverlayViewModel(
            settings,
            static action => action(),
            overlayCoordinator: coordinator,
            failureTracker: tracker
        );
        var token = coordinator.Acquire(OverlayRequester.Dictation);
        Assert.True(coordinator.Show(token, new DictationOverlayState
        {
            IsOverlayVisible = true,
            IsRecording = true,
            StatusText = "Recording",
        }));
        Assert.True(sut.IsRecording);

        tracker.RaiseFailure("Focus detection failed");

        // The toast competes in arbitration and loses to the live recording instead of
        // overpainting it with a direct property write the coordinator cannot see.
        Assert.True(sut.IsRecording);
        Assert.False(sut.ShowFeedback);

        Assert.True(coordinator.Release(token));

        Assert.True(sut.ShowFeedback);
        // The toast carries the localized generic message; the detailed reason goes
        // to the error log via the tracker, not the overlay.
        Assert.Equal(Loc.Instance["Overlay.WindowDetectionFailed"], sut.FeedbackText);
        Assert.Equal("Focus detection failed", tracker.LastFailureReason);
        Assert.True(sut.FeedbackIsError);
    }

    [Fact]
    public void DetectionFailureWithPersistentBanner_DoesNotShowToast()
    {
        var settings = new FakeSettingsService(AppSettings.Default);
        var tracker = new FakeDetectionFailureTracker();
        var sut = new DictationOverlayViewModel(
            settings,
            static action => action(),
            failureTracker: tracker
        );

        tracker.RaiseFailure("Focus detection failed", shouldShowPersistentBanner: true);

        // The persistent banner owns the messaging; a toast on top would be redundant.
        Assert.False(sut.ShowFeedback);
        Assert.Null(sut.FeedbackText);
    }

    [Fact]
    public void LegacyApplyState_RemainsFunctionalAndOwnsFeedbackExpiry()
    {
        var settings = new FakeSettingsService(
            AppSettings.Default with { PreviewBubbleAutoHideMilliseconds = 1500 }
        );
        var sut = CreateViewModel(settings);

        sut.ApplyState(new DictationOverlayState
        {
            ShowFeedback = true,
            FeedbackText = "Legacy feedback",
        });

        Assert.True(sut.ShowFeedback);
        Assert.Equal("Legacy feedback", sut.FeedbackText);
        Assert.True(sut.IsFeedbackTimerRunning);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), sut.FeedbackTimerInterval);
    }

    [Fact]
    public void FloatingControl_IsOptIn_AndRefreshesWithoutReplacingRecording()
    {
        var settings = new FakeSettingsService(AppSettings.Default);
        var sut = CreateViewModel(settings);
        Assert.False(sut.HasVisibleContent);

        settings.Change(settings.Current with { ShowFloatingControl = true });
        Assert.True(sut.ShowCompactLauncher);
        Assert.True(sut.HasVisibleContent);
        sut.SetLauncherExpanded(true);
        Assert.True(sut.ShowLauncherActions);

        sut.ApplyState(new DictationOverlayState
        {
            IsOverlayVisible = true,
            IsRecording = true,
            StatusText = "Current recording",
        });
        settings.Change(settings.Current with { ShowFloatingControl = false });
        Assert.False(sut.ShowLauncher);
        Assert.False(sut.IsLauncherExpanded);
        Assert.True(sut.HasVisibleContent);
        Assert.Equal("Current recording", sut.StatusText);
    }

    [Fact]
    public async Task ExpandingLauncher_DoesNotStartRecording_AndStartIsSerialized()
    {
        var settings = new FakeSettingsService(AppSettings.Default with { ShowFloatingControl = true });
        var started = 0;
        var completion = new TaskCompletionSource();
        var sut = new DictationOverlayViewModel(settings, action => action(),
            startDictation: () => { started++; return completion.Task; });

        sut.SetLauncherExpanded(true);
        Assert.Equal(0, started);
        var first = sut.StartDictationCommand.ExecuteAsync(null);
        Assert.Equal(1, started);
        Assert.False(sut.StartDictationCommand.CanExecute(null));
        await sut.StartDictationCommand.ExecuteAsync(null);
        Assert.Equal(1, started);
        Assert.False(sut.IsRecording);
        completion.SetResult();
        await first;
        Assert.True(sut.StartDictationCommand.CanExecute(null));
    }

    [Fact]
    public async Task PrimaryLauncherClick_ExpandsBeforeStartingWithoutHover()
    {
        var started = 0;
        var sut = new DictationOverlayViewModel(
            new FakeSettingsService(AppSettings.Default with { ShowFloatingControl = true }),
            action => action(), startDictation: () => { started++; return Task.CompletedTask; });
        await sut.ActivateLauncherCommand.ExecuteAsync(null);
        Assert.True(sut.IsLauncherExpanded);
        Assert.Equal(0, started);
        await sut.ActivateLauncherCommand.ExecuteAsync(null);
        Assert.Equal(1, started);
    }

    [Fact]
    public async Task FailedLauncherCommand_ReportsErrorWithoutClaimingRecording()
    {
        var sut = new DictationOverlayViewModel(
            new FakeSettingsService(AppSettings.Default with { ShowFloatingControl = true }),
            action => action(), startDictation: () => Task.FromException(new InvalidOperationException("Fixture fault")));

        await sut.StartDictationCommand.ExecuteAsync(null);

        Assert.True(sut.ShowFeedback);
        Assert.True(sut.FeedbackIsError);
        Assert.False(sut.IsRecording);
        Assert.False(sut.ShowLauncher);
    }

    [Fact]
    public async Task RecordingControls_OnlyOperateOnDictation_AndLateCollapseCannotHideIt()
    {
        var settings = new FakeSettingsService(AppSettings.Default with { ShowFloatingControl = true });
        var coordinator = new OverlayCoordinator(settings, action => action());
        var stopped = 0;
        var canceled = 0;
        var sut = new DictationOverlayViewModel(settings, action => action(), overlayCoordinator: coordinator,
            stopDictation: () => { stopped++; return Task.CompletedTask; },
            cancelDictation: () => { canceled++; return Task.CompletedTask; });
        sut.SetLauncherExpanded(true);
        var transform = coordinator.Acquire(OverlayRequester.Transform);
        coordinator.Show(transform, new DictationOverlayState { IsOverlayVisible = true, IsRecording = true });
        Assert.False(sut.ShowRecordingControls);
        await sut.StopDictationCommand.ExecuteAsync(null);
        await sut.CancelDictationCommand.ExecuteAsync(null);
        Assert.Equal(0, stopped + canceled);

        coordinator.Release(transform);
        var dictation = coordinator.Acquire(OverlayRequester.Dictation);
        coordinator.Show(dictation, new DictationOverlayState { IsOverlayVisible = true, IsRecording = true });
        sut.SetLauncherExpanded(false);
        Assert.True(sut.ShowRecordingControls);
        Assert.True(sut.IsOverlayVisible);
        await sut.StopDictationCommand.ExecuteAsync(null);
        await sut.CancelDictationCommand.ExecuteAsync(null);
        Assert.Equal(1, stopped);
        Assert.Equal(1, canceled);
        Assert.False(sut.ShowLauncherActions);
    }

    [Fact]
    public async Task Finishing_RemainsCancelableDuringStop_AndNewRecordingCanStopIndependently()
    {
        var settings = new FakeSettingsService(AppSettings.Default with { ShowFloatingControl = true });
        var coordinator = new OverlayCoordinator(settings, action => action());
        var completion = new TaskCompletionSource();
        var stops = 0;
        var cancels = new List<OverlayPresentationToken>();
        var sut = new DictationOverlayViewModel(settings, action => action(), overlayCoordinator: coordinator,
            stopDictation: () =>
            {
                stops++;
                coordinator.Show(coordinator.PresentedToken!, new DictationOverlayState { IsOverlayVisible = true });
                return completion.Task;
            },
            cancelPresentedWork: token => { cancels.Add(token); return Task.CompletedTask; });
        var first = coordinator.Acquire(OverlayRequester.Dictation);
        coordinator.Show(first, new DictationOverlayState { IsOverlayVisible = true, IsRecording = true });
        var pending = sut.StopDictationCommand.ExecuteAsync(null);
        Assert.True(sut.ShowProcessingControls);
        Assert.True(sut.CancelDictationCommand.CanExecute(null));
        await sut.CancelDictationCommand.ExecuteAsync(null);
        Assert.Same(first, Assert.Single(cancels));
        var second = coordinator.Acquire(OverlayRequester.Dictation);
        coordinator.Show(second, new DictationOverlayState { IsOverlayVisible = true, IsRecording = true });
        Assert.True(sut.StopDictationCommand.CanExecute(null));
        var next = sut.StopDictationCommand.ExecuteAsync(null);
        Assert.Equal(2, stops);
        completion.SetResult();
        await Task.WhenAll(pending, next);
    }

    [Fact]
    public void CompactSuccess_NeverHidesCopyWarningsOrActionResults()
    {
        var sut = CreateViewModel(new FakeSettingsService(AppSettings.Default with { ShowFloatingControl = true }));
        sut.ApplyState(new DictationOverlayState { ShowFeedback = true, DeliverySucceeded = true, FeedbackText = "Typed text" });
        Assert.True(sut.ShowSuccessConfirmation);
        Assert.False(sut.ShowDetailedFeedback);
        sut.ApplyState(new DictationOverlayState { ShowFeedback = true, FeedbackText = "Copied to clipboard" });
        Assert.False(sut.ShowSuccessConfirmation);
        Assert.True(sut.ShowDetailedFeedback);
        sut.ApplyState(new DictationOverlayState { ShowFeedback = true, DeliverySucceeded = true,
            ActionResultUrl = "https://example.com/result" });
        Assert.False(sut.ShowSuccessConfirmation);
        Assert.True(sut.ShowDetailedFeedback);
    }

    [Fact]
    public async Task RecoveryCopy_UsesTheFailedText_AndCannotReplaceANewerRecording()
    {
        var settings = new FakeSettingsService(AppSettings.Default with { ShowFloatingControl = true });
        var coordinator = new OverlayCoordinator(settings, action => action());
        var completion = new TaskCompletionSource<bool>();
        string? copied = null;
        var sut = new DictationOverlayViewModel(settings, action => action(), overlayCoordinator: coordinator,
            copyRecoveryText: text => { copied = text; return completion.Task; });
        var first = coordinator.Acquire(OverlayRequester.Dictation);
        coordinator.Show(first, new DictationOverlayState { ShowFeedback = true, FeedbackIsError = true, RecoveryText = "failed session text" });
        var copy = sut.CopyRecoveryCommand.ExecuteAsync(null);
        Assert.Equal("failed session text", copied);
        var newer = coordinator.Acquire(OverlayRequester.Dictation);
        coordinator.Show(newer, new DictationOverlayState { IsOverlayVisible = true, IsRecording = true });
        completion.SetResult(true);
        await copy;
        Assert.True(sut.ShowRecordingControls);
        Assert.False(sut.ShowFeedback);
        Assert.False(sut.HasRecoveryText);
    }

    [Fact]
    public async Task RecoveryCopyFailure_KeepsTextAndShowsAnImmediateRetryMessage()
    {
        var settings = new FakeSettingsService(AppSettings.Default with { ShowFloatingControl = true });
        var coordinator = new OverlayCoordinator(settings, action => action());
        var sut = new DictationOverlayViewModel(settings, action => action(), overlayCoordinator: coordinator,
            copyRecoveryText: _ => Task.FromException<bool>(new IOException("Clipboard fixture fault")));
        var owner = coordinator.Acquire(OverlayRequester.Dictation);
        coordinator.Show(owner, new DictationOverlayState { ShowFeedback = true, FeedbackIsError = true,
            FeedbackText = "Insertion failed", RecoveryText = "Keep these words" });
        await sut.CopyRecoveryCommand.ExecuteAsync(null);
        Assert.Equal(Loc.Instance["Overlay.RecoveryCopyFailed"], sut.FeedbackText);
        Assert.True(sut.HasRecoveryText);
        Assert.True(sut.CopyRecoveryCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(OverlayPosition.Top, false)]
    [InlineData(OverlayPosition.Left, true)]
    [InlineData(OverlayPosition.Right, true)]
    public void QuietMarker_FollowsEdgeOrientation(OverlayPosition edge, bool vertical)
    {
        var sut = CreateViewModel(new FakeSettingsService(AppSettings.Default with { ShowFloatingControl = true, OverlayPosition = edge }));
        Assert.Equal(vertical ? 10 : 28, sut.LauncherButtonWidth);
        Assert.Equal(vertical ? 28 : 10, sut.LauncherButtonHeight);
        sut.SetLauncherExpanded(true);
        Assert.Equal(44, sut.LauncherButtonWidth);
        Assert.Equal(28, sut.LauncherButtonHeight);
    }

    private static DictationOverlayViewModel CreateViewModel(FakeSettingsService settings)
    {
        return new DictationOverlayViewModel(settings, static action => action());
    }

    private static List<string?> TrackPropertyChanges(DictationOverlayViewModel sut)
    {
        var propertyNames = new List<string?>();
        sut.PropertyChanged += (_, args) => propertyNames.Add(args.PropertyName);
        return propertyNames;
    }

    private static IEnumerable<string?> SideTextNotifications(IEnumerable<string?> propertyNames)
    {
        return propertyNames.Where(name =>
            name is nameof(DictationOverlayViewModel.LeftText)
                or nameof(DictationOverlayViewModel.RightText));
    }

    private sealed class FakeDetectionFailureTracker : IDetectionFailureTracker
    {
        public bool ShouldShowPersistentBanner => false;

        public string? LastFailureReason { get; private set; }

        public event EventHandler<DetectionFailureEvent>? OnFailure;

        public void RecordSuccess()
        {
        }

        public void RecordFailure(string compositor, string reason)
        {
            RaiseFailure(reason);
        }

        public void RaiseFailure(string reason, bool shouldShowPersistentBanner = false)
        {
            LastFailureReason = reason;
            OnFailure?.Invoke(
                this,
                new DetectionFailureEvent(reason, shouldShowPersistentBanner)
            );
        }
    }

    private sealed class FakeSettingsService(AppSettings current) : ISettingsService
    {
        public AppSettings Current { get; private set; } = current;

        public AppSettings Load()
        {
            return Current;
        }

        public void Save(AppSettings settings)
        {
            Change(settings);
        }

        public AppSettings Update(Func<AppSettings, AppSettings> mutate)
        {
            var updated = mutate(Current);
            Change(updated);
            return updated;
        }

        public void Change(AppSettings settings)
        {
            Current = settings;
            SettingsChanged?.Invoke(settings);
        }

        public event Action<AppSettings>? SettingsChanged;
    }
}
