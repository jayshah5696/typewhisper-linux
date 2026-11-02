using TypeWhisper.Core.Models;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Setup;
using TypeWhisper.Linux.ViewModels.Sections;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.PluginSDK;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class DashboardSectionViewModelTests : IDisposable
{
    private readonly string _tempDir;

    public DashboardSectionViewModelTests()
    {
        _tempDir = Path.Join(
            Path.GetTempPath(),
            "TypeWhisper.Dashboard.Tests_" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // Best-effort cleanup for temp test directories.
        }
    }

    [Theory]
    [InlineData(DashboardSectionViewModel.TimeRange.AllTime, 300)]
    [InlineData(DashboardSectionViewModel.TimeRange.Weekly, 100)]
    [InlineData(DashboardSectionViewModel.TimeRange.Month, 100)]
    public void Statistics_FutureSavedDayIsIncludedOnlyInAllTime(
        DashboardSectionViewModel.TimeRange range, int expectedWords)
    {
        var today = new DateOnly(2026, 9, 9);
        var tomorrow = today.AddDays(1);
        var statistics = new FakeStatistics
        {
            Days = [Day(today, 100, 60), Day(tomorrow, 200, 60)],
        };
        using var sut = CreateStatisticsDashboard(statistics, today);
        sut.SelectedRange = range;
        sut.Refresh();

        Assert.Equal(expectedWords, sut.WordCount);
        Assert.Equal(expectedWords, sut.ActivityDays.Sum(row => row.WordCount));
        Assert.Equal(range == DashboardSectionViewModel.TimeRange.AllTime ? tomorrow : today,
            sut.ActivityDays[^1].EndDay);
        Assert.Equal(range == DashboardSectionViewModel.TimeRange.AllTime ? 2 : 1, sut.TranscriptionCount);
    }

    [Fact]
    public void Refresh_BuildsHistoryInsightsForSelectedRange()
    {
        var history = new HistoryService(Path.Join(_tempDir, "history.json"));
        history.AddRecord(
            CreateRecord(
                "one two three four",
                "code",
                8,
                DateTime.UtcNow,
                TextInsertionStatus.Pasted,
                true
            )
        );
        history.AddRecord(
            CreateRecord(
                "one two",
                "code",
                4,
                DateTime.UtcNow,
                TextInsertionStatus.Typed,
                snippetApplied: true
            )
        );
        history.AddRecord(
            CreateRecord(
                "one two three four five six",
                "browser",
                12,
                DateTime.UtcNow,
                TextInsertionStatus.CopiedToClipboard,
                dictionaryApplied: true,
                promptApplied: true,
                translationApplied: true
            )
        );
        var settings = new SettingsService(Path.Join(_tempDir, "settings.json"));
        using var sut = new DashboardSectionViewModel(history, settings, new HistoryInsightsService(), Backfill(history));

        sut.SelectedRange = DashboardSectionViewModel.TimeRange.AllTime;

        Assert.Equal("4", sut.AverageWordsPerDictationLabel);
        Assert.Equal("8s", sut.AverageDurationLabel);
        Assert.True(sut.HasTopApps);
        Assert.Equal("code", sut.TopApps[0].AppProcessName);
        Assert.Equal("2 dictations · 6 words", sut.TopApps[0].Summary);
        Assert.Equal("66.7%", sut.InsertionSuccessRateLabel);
        Assert.Equal("1", sut.PastedCountLabel);
        Assert.Equal("1", sut.TypedCountLabel);
        Assert.Equal("1 pasted / 1 typed", sut.InsertedBreakdownLabel);
        Assert.Equal("1", sut.ClipboardFallbackCountLabel);
        Assert.Equal("0", sut.FailedInsertionCountLabel);
        Assert.Equal("1", sut.CleanupAppliedCountLabel);
        Assert.Equal("1", sut.SnippetAppliedCountLabel);
        Assert.Equal("1", sut.DictionaryCorrectionAppliedCountLabel);
        Assert.Equal("1", sut.PromptActionAppliedCountLabel);
        Assert.Equal("1", sut.TranslationAppliedCountLabel);
    }

    [Fact]
    public void Refresh_CalculatesTimeSavedFromManualTypingBaseline()
    {
        var history = new HistoryService(Path.Join(_tempDir, "history.json"));
        history.AddRecord(
            CreateRecord("one two three four five six seven eight", "code", 4, DateTime.UtcNow)
        );
        var settings = new SettingsService(Path.Join(_tempDir, "settings.json"));
        using var sut = new DashboardSectionViewModel(history, settings, new HistoryInsightsService(), Backfill(history));

        sut.SelectedRange = DashboardSectionViewModel.TimeRange.AllTime;

        Assert.Equal("8s", sut.TimeSavedLabel);
    }

    [Fact]
    public void Refresh_CountsWordsSeparatedByNewlines()
    {
        var history = new HistoryService(Path.Join(_tempDir, "history.json"));
        history.AddRecord(
            CreateRecord("Hi Ryan,\n\nThis has spacing.", "browser", 1, DateTime.UtcNow)
        );
        var settings = new SettingsService(Path.Join(_tempDir, "settings.json"));
        using var sut = new DashboardSectionViewModel(history, settings, new HistoryInsightsService(), Backfill(history));

        sut.SelectedRange = DashboardSectionViewModel.TimeRange.AllTime;

        Assert.Equal(5, sut.WordCount);
    }

    [Fact]
    public void RecentActivity_ExposesLocalPresentationTimestamp()
    {
        var timeZone = TimeZoneInfo.CreateCustomTimeZone(
            "Dashboard tests UTC+13",
            TimeSpan.FromHours(13),
            "Dashboard tests UTC+13",
            "Dashboard tests UTC+13"
        );
        var history = new HistoryService(Path.Join(_tempDir, "history.json"));
        var timestamp = new DateTime(2030, 1, 2, 23, 30, 0, DateTimeKind.Utc);
        history.AddRecord(CreateRecord("recent words", "code", 2, timestamp));
        var settings = new SettingsService(Path.Join(_tempDir, "settings.json"));
        using var sut = new DashboardSectionViewModel(
            history,
            settings,
            new HistoryInsightsService(),
            Backfill(history),
            timeZone
        );

        sut.SelectedRange = DashboardSectionViewModel.TimeRange.AllTime;

        var activity = Assert.Single(sut.RecentActivity);
        Assert.Same(history.Records[0], activity.Record);
        Assert.Equal(new DateTime(2030, 1, 3, 12, 30, 0), activity.LocalTimestamp);
    }

    private UsageStatisticsService Backfill(HistoryService history)
    {
        var statistics = new UsageStatisticsService(Path.Join(_tempDir, "statistics.json"));
        statistics.BackfillFromHistoryIfNeeded(history.Records);
        return statistics;
    }

    [Theory]
    [InlineData(DashboardSectionViewModel.TimeRange.Weekly, 300, 2, 2, 2, 3, 2)]
    [InlineData(DashboardSectionViewModel.TimeRange.Month, 450, 4, 2, 2, 5, 3)]
    [InlineData(DashboardSectionViewModel.TimeRange.AllTime, 600, 7, 2, 3, 8, 4)]
    public void Statistics_DriveTilesAndStreaksAcrossRanges(
        DashboardSectionViewModel.TimeRange range, int words, int active, int current, int longest, int count, int apps)
    {
        var today = new DateOnly(2026, 9, 9);
        var statistics = new FakeStatistics
        {
            Days = [Day(today, 100, 60), Day(today.AddDays(-1), 200, 60, 2, "b"),
                Day(today.AddDays(-7), 100, 60), Day(today.AddDays(-20), 50, 30, 1, "c"),
                Day(today.AddDays(-40), 50, 30, 1, "d"), Day(today.AddDays(-41), 50, 30, 1, "d"),
                Day(today.AddDays(-42), 50, 30, 1, "d")],
        };
        var history = new HistoryService(Path.Join(_tempDir, "history.json"));
        history.AddRecord(CreateRecord("retained history", "other", 1, today.AddDays(-100).ToDateTime(TimeOnly.MinValue)) with
        {
            CreatedAt = DateTime.UtcNow.AddDays(-100),
        });
        var settings = new SettingsService(Path.Join(_tempDir, "settings.json"));
        using var sut = new DashboardSectionViewModel(history, settings, new HistoryInsightsService(), statistics,
            TimeZoneInfo.Utc, timeProvider: new FixedTimeProvider(today.ToDateTime(new TimeOnly(12, 0))));
        sut.SelectedRange = range;
        sut.Refresh();
        Assert.Equal(words, sut.WordCount);
        Assert.Equal(active, sut.DaysActive);
        Assert.Equal(current, sut.CurrentStreak);
        Assert.Equal(longest, sut.LongestStreak);
        Assert.Equal(count, sut.TranscriptionCount);
        Assert.Equal(apps, sut.AppCount);
        Assert.Equal(range switch
        {
            DashboardSectionViewModel.TimeRange.Weekly => 150,
            DashboardSectionViewModel.TimeRange.Month => 128,
            _ => 120,
        }, sut.AverageWpm);
        Assert.Equal(range switch
        {
            DashboardSectionViewModel.TimeRange.Weekly => "5m",
            DashboardSectionViewModel.TimeRange.Month => "7m",
            _ => "10m",
        }, sut.TimeSavedLabel);
        var before = (sut.WordCount, sut.AverageWpm, sut.AppCount, sut.TimeSavedLabel, sut.DaysActive, sut.CurrentStreak, sut.LongestStreak, sut.TranscriptionCount);
        history.PurgeOldRecords(TimeSpan.FromDays(1));
        Assert.Empty(history.Records);
        sut.Refresh();
        Assert.Equal(before, (sut.WordCount, sut.AverageWpm, sut.AppCount, sut.TimeSavedLabel, sut.DaysActive, sut.CurrentStreak, sut.LongestStreak, sut.TranscriptionCount));
        Assert.Equal(range switch
        {
            DashboardSectionViewModel.TimeRange.Weekly => 7,
            DashboardSectionViewModel.TimeRange.Month => 30,
            _ => 43,
        }, sut.ActivityDays.Count);
    }

    [Theory]
    [InlineData(DashboardSectionViewModel.TimeRange.Weekly, 7)]
    [InlineData(DashboardSectionViewModel.TimeRange.Month, 30)]
    public void Statistics_TrendsCompareEqualLocalWindowsAndClearForAllTime(
        DashboardSectionViewModel.TimeRange range, int length)
    {
        var today = new DateOnly(2026, 9, 9);
        var statistics = new FakeStatistics { Days = [Day(today, 200, 120), Day(today.AddDays(-length), 100, 30)] };
        var pending = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        using var sut = CreateStatisticsDashboard(statistics, today, pending.Enqueue);
        sut.SelectedRange = range;
        sut.Refresh();
        Assert.Equal("▲ 100 %", sut.WordsTrendLabel);
        Assert.Equal("▼ 50 %", sut.WpmTrendLabel);
        sut.SelectedRange = DashboardSectionViewModel.TimeRange.AllTime;
        Assert.Empty(sut.WordsTrendLabel);
        Assert.Empty(sut.WpmTrendLabel);
        statistics.Days = [Day(today, 100, 60)];
        sut.SelectedRange = DashboardSectionViewModel.TimeRange.Month;
        Assert.Empty(sut.WordsTrendLabel);
        Assert.Empty(sut.WpmTrendLabel);
        statistics.Days = [Day(today.AddDays(-1), 100, 60), Day(today.AddDays(-2), 100, 60)];
        statistics.Notify();
        Assert.NotEmpty(pending);
        while (pending.TryDequeue(out var refresh))
        {
            refresh();
        }
        Assert.Equal(2, sut.CurrentStreak);
        statistics.Days = [Day(today.AddDays(-2), 100, 60)];
        statistics.Notify();
        Assert.Equal(2, sut.CurrentStreak);
        while (pending.TryDequeue(out var nextRefresh))
        {
            nextRefresh();
        }
        Assert.Equal(0, sut.CurrentStreak);
    }

    [Fact]
    public void Statistics_AllTimeBucketsAndBreakdownsPreserveTotalsAndOrdering()
    {
        var today = new DateOnly(2026, 9, 9);
        var models = Enumerable.Range(0, 10).ToDictionary(i => UsageStatisticsService.BuildModelKey("engine", $"model{i}"), i => i + 1);
        var statistics = new FakeStatistics
        {
            Days = [Day(today.AddDays(-100), 100, 60) with
            {
                LanguageCounts = new Dictionary<string, int> { ["en"] = 3, ["de"] = 1, ["zz"] = 1 },
                ModelCounts = models,
                HourCounts = Enumerable.Range(0, 24).ToArray(),
            }, Day(today, 200, 60)],
        };
        using var sut = CreateStatisticsDashboard(statistics, today);
        sut.SelectedRange = DashboardSectionViewModel.TimeRange.AllTime;
        Assert.InRange(sut.ActivityDays.Count, 1, 48);
        Assert.Equal(today.AddDays(-100), sut.ActivityDays[0].Day);
        Assert.Equal(300, sut.ActivityDays.Sum(row => row.WordCount));
        Assert.Equal(2, sut.ActivityDays.Sum(row => row.TranscriptionCount));
        Assert.Equal(today.AddDays(-1), sut.ActivityDays[^1].Day);
        Assert.Equal(today, sut.ActivityDays[^1].EndDay);
        Assert.Equal($"{today.AddDays(-1):d MMM} – {today:d MMM}", sut.ActivityDays[^1].DateLabel);
        Assert.Equal(200, sut.ActivityDays[^1].WordCount);
        Assert.Equal(1, sut.ActivityDays[^1].TranscriptionCount);
        Assert.Equal(240, sut.ActivityDays[^1].BarWidth);
        Assert.All(sut.ActivityDays, row => Assert.InRange(row.BarWidthFraction, 0, 1));
        Assert.Equal(["English", "Deutsch", "ZZ"], sut.Languages.Select(row => row.Label));
        Assert.Equal(60, sut.Languages[0].Percent);
        Assert.Equal(0.6, sut.Languages[0].BarWidthFraction);
        Assert.Equal(8, sut.Models.Count);
        Assert.Equal("engine · model9", sut.Models[0].Label);
        Assert.Equal(10 * 100d / 55, sut.Models[0].Percent);
        Assert.Equal(24, sut.Hours.Count);
        Assert.Equal(23, sut.Hours[23].Count);
        Assert.Equal(1, sut.Hours[23].Intensity);
        Assert.Equal(0, sut.Hours[0].Intensity);
    }

    [Fact]
    public void Statistics_UsesLocalRangeBoundariesAndLoadedProviderNames()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("UTC+3", TimeSpan.FromHours(3), "UTC+3", "UTC+3");
        var now = new DateTime(2026, 9, 9, 23, 30, 0);
        var today = new DateOnly(2026, 9, 10);
        var statistics = new FakeStatistics
        {
            Days = [Day(today.AddDays(-7), 500, 60), Day(today.AddDays(-6), 100, 60), Day(today, 200, 60) with
            {
                ModelCounts = new Dictionary<string, int> { [UsageStatisticsService.BuildModelKey("provider", "large")] = 1 },
            }],
        };
        using var plugins = TestPluginManagerFactory.Create();
        var engine = new Moq.Mock<PluginSDK.ITranscriptionEngineRole>();
        engine.SetupGet(plugin => plugin.ProviderId).Returns("provider");
        engine.SetupGet(plugin => plugin.ProviderDisplayName).Returns("Friendly provider");
        TypeWhisper.Tests.PluginManagerTestAccess.SetTranscriptionEngines(plugins, [engine.Object]);
        using var sut = new DashboardSectionViewModel(
            new HistoryService(Path.Join(_tempDir, "history.json")),
            new SettingsService(Path.Join(_tempDir, "settings.json")),
            new HistoryInsightsService(), statistics, zone, plugins, new FixedTimeProvider(now));
        sut.Refresh();
        Assert.Equal(300, sut.WordCount);
        Assert.Equal(today.AddDays(-6), sut.ActivityDays[0].Day);
        Assert.Equal(today, sut.ActivityDays[^1].Day);
        Assert.All(sut.ActivityDays, row =>
        {
            Assert.Equal(row.Day, row.EndDay);
            Assert.Equal(row.Day.ToString("d MMM"), row.DateLabel);
        });
        Assert.Equal("Friendly provider · large", Assert.Single(sut.Models).Label);
    }

    [Fact]
    public async Task DayRollover_RefreshesThroughUiAndRearmsUntilDisposed()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("UTC+3", TimeSpan.FromHours(3), "UTC+3", "UTC+3");
        var clock = new ControllableTimeProvider
        {
            UtcNow = new DateTimeOffset(2026, 9, 9, 20, 30, 0, TimeSpan.Zero),
            Zone = TimeZoneInfo.Utc,
        };
        var statistics = new FakeStatistics { Days = [Day(new DateOnly(2026, 9, 8), 100, 60)] };
        var pending = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        var history = new HistoryService(Path.Join(_tempDir, "history.json"));
        await history.EnsureLoadedAsync();
        var sut = new DashboardSectionViewModel(
            history,
            new SettingsService(Path.Join(_tempDir, "settings.json")),
            new HistoryInsightsService(), statistics, zone, timeProvider: clock, postToUi: pending.Enqueue);
        ControllableTimeProvider.FakeTimer nextTimer;
        Action queuedRefresh;
        try
        {
            sut.Refresh();
            while (pending.TryDequeue(out var initialRefresh))
            {
                initialRefresh();
            }
            Assert.Equal(1, sut.CurrentStreak);
            var timer = Assert.IsType<ControllableTimeProvider.FakeTimer>(clock.Timer);
            Assert.Equal(TimeSpan.FromMinutes(30) + TimeSpan.FromSeconds(1), timer.DueTime);
            Assert.Equal(Timeout.InfiniteTimeSpan, timer.Period);

            clock.UtcNow = new DateTimeOffset(2026, 9, 9, 21, 0, 1, TimeSpan.Zero);
            timer.Fire();
            Assert.Equal(1, sut.CurrentStreak);
            Assert.True(pending.TryDequeue(out var refresh));
            refresh();
            Assert.Equal(0, sut.CurrentStreak);
            Assert.Equal(new DateOnly(2026, 9, 10), sut.ActivityDays[^1].Day);
            Assert.True(timer.Disposed);
            nextTimer = Assert.IsType<ControllableTimeProvider.FakeTimer>(clock.Timer);
            Assert.NotSame(timer, nextTimer);
            Assert.Equal(TimeSpan.FromDays(1), nextTimer.DueTime);

            nextTimer.Fire();
            Assert.True(pending.TryDequeue(out var callback));
            queuedRefresh = callback;
        }
        finally
        {
            sut.Dispose();
        }
        Assert.True(nextTimer.Disposed);
        clock.UtcNow = clock.UtcNow.AddDays(1);
        queuedRefresh();
        nextTimer.Fire();
        Assert.Empty(pending);
        Assert.Same(nextTimer, clock.Timer);
        Assert.Equal(new DateOnly(2026, 9, 10), sut.ActivityDays[^1].Day);
    }

    [Fact]
    public async Task DayRollover_AmbiguousMidnightSchedulesFirstOccurrence()
    {
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 3, 1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 1, 0, 0), 11, 1));
        var zone = TimeZoneInfo.CreateCustomTimeZone(
            "Midnight fall-back", TimeSpan.Zero, "Midnight fall-back", "Standard", "Daylight", [rule]);
        var midnight = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Unspecified);
        Assert.True(zone.IsAmbiguousTime(midnight));
        var clock = new ControllableTimeProvider
        {
            UtcNow = new DateTimeOffset(2026, 10, 31, 22, 30, 0, TimeSpan.Zero),
        };
        var history = new HistoryService(Path.Join(_tempDir, "history.json"));
        await history.EnsureLoadedAsync();
        using var sut = new DashboardSectionViewModel(
            history,
            new SettingsService(Path.Join(_tempDir, "settings.json")),
            new HistoryInsightsService(), new FakeStatistics(), zone,
            timeProvider: clock, postToUi: action => action());
        sut.Refresh();

        var timer = Assert.IsType<ControllableTimeProvider.FakeTimer>(clock.Timer);
        Assert.Equal(TimeSpan.FromMinutes(30) + TimeSpan.FromSeconds(1), timer.DueTime);
        Assert.Equal(new DateTimeOffset(2026, 10, 31, 23, 0, 1, TimeSpan.Zero), clock.UtcNow + timer.DueTime);
        Assert.Equal(Timeout.InfiniteTimeSpan, timer.Period);
    }

    private sealed class ControllableTimeProvider : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; }
        public TimeZoneInfo Zone { get; init; } = TimeZoneInfo.Utc;
        public FakeTimer? Timer { get; private set; }
        public override DateTimeOffset GetUtcNow() => UtcNow;
        public override TimeZoneInfo LocalTimeZone => Zone;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Timer = new FakeTimer(callback, state, dueTime, period);
            return Timer;
        }

        public sealed class FakeTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) : ITimer
        {
            public TimeSpan DueTime { get; private set; } = dueTime;
            public TimeSpan Period { get; private set; } = period;
            public bool Disposed { get; private set; }
            // Allow late callbacks to exercise the view model's disposal guard.
            public void Fire() => callback(state);
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                DueTime = dueTime;
                Period = period;
                return !Disposed;
            }
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private DashboardSectionViewModel CreateStatisticsDashboard(FakeStatistics statistics, DateOnly today, Action<Action>? postToUi = null) =>
        new(new HistoryService(Path.Join(_tempDir, "history.json")),
            new SettingsService(Path.Join(_tempDir, "settings.json")), new HistoryInsightsService(), statistics,
            TimeZoneInfo.Utc, timeProvider: new FixedTimeProvider(today.ToDateTime(new TimeOnly(12, 0))), postToUi: postToUi);

    private static UsageStatisticsDaySnapshot Day(DateOnly day, int words, double seconds, int count = 1, string app = "a") => new()
    {
        Day = day,
        TotalWords = words,
        TotalDurationSeconds = seconds,
        TranscriptionCount = count,
        AppCounts = new Dictionary<string, int> { [app] = count },
    };

    private sealed class FixedTimeProvider(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(now, DateTimeKind.Utc));
    }

    private sealed class FakeStatistics : IUsageStatisticsService
    {
        public IReadOnlyList<UsageStatisticsDaySnapshot> Days { get; set; } = [];
        public bool HasAnyStatistics => Days.Count > 0;
        public event Action? StatisticsChanged;
        public void Notify() => StatisticsChanged?.Invoke();
        public void RecordTranscription(TranscriptionRecord record) => throw new NotSupportedException();
        public void BackfillFromHistoryIfNeeded(IEnumerable<TranscriptionRecord> records) => throw new NotSupportedException();
    }

    private static TranscriptionRecord CreateRecord(
        string finalText,
        string appProcessName,
        double durationSeconds,
        DateTime timestamp,
        TextInsertionStatus insertionStatus = TextInsertionStatus.Unknown,
        bool cleanupApplied = false,
        bool snippetApplied = false,
        bool dictionaryApplied = false,
        bool promptApplied = false,
        bool translationApplied = false
    )
    {
        return new TranscriptionRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = timestamp,
            RawText = finalText,
            FinalText = finalText,
            AppProcessName = appProcessName,
            DurationSeconds = durationSeconds,
            InsertionStatus = insertionStatus,
            CleanupApplied = cleanupApplied,
            SnippetApplied = snippetApplied,
            DictionaryCorrectionApplied = dictionaryApplied,
            PromptActionApplied = promptApplied,
            TranslationApplied = translationApplied,
        };
    }

    [Fact]
    public async Task Readiness_WhenModelDownloadedAndHotkeyConfigured_ReportsReady()
    {
        var settingsPath = Path.Join(_tempDir, "settings.json");
        var settings = new SettingsService(settingsPath);
        var modelId = ModelManagerService.GetPluginModelId("provider", "base");
        settings.Update(s => s with
        {
            SelectedModelId = modelId,
            ToggleHotkey = "Ctrl+Shift+Space",
        });

        using var plugins = TestPluginManagerFactory.Create();
        var engine = new Moq.Mock<ITranscriptionEngineRole>();
        engine.SetupGet(p => p.PluginId).Returns("provider");
        engine.SetupGet(p => p.ProviderId).Returns("provider");
        engine.SetupGet(p => p.ProviderDisplayName).Returns("Test Engine");
        engine.Setup(p => p.IsModelDownloaded("base")).Returns(true);
        engine.SetupGet(p => p.SupportsModelDownload).Returns(true);
        engine.SetupGet(p => p.TranscriptionModels).Returns([new TypeWhisper.PluginSDK.Models.PluginModelInfo("base", "Whisper Base")]);
        TypeWhisper.Tests.PluginManagerTestAccess.SetTranscriptionEngines(plugins, [engine.Object]);

        using var models = new ModelManagerService(plugins, settings);
        using var hotkey = TestShortcutBackend.CreateHotkeyService();
        hotkey.Initialize();
        hotkey.TrySetHotkeyFromString("Ctrl+Shift+Space");

        var setupTask = new FakeDashboardSetupTask("hotkey", "Hotkey setup", SetupTaskSeverity.Required, SetupTaskStatusKind.Satisfied, "Ready");

        using var sut = new DashboardSectionViewModel(
            new HistoryService(Path.Join(_tempDir, "history.json")),
            settings,
            new HistoryInsightsService(),
            new FakeStatistics(),
            plugins: plugins,
            models: models,
            hotkey: hotkey,
            setupTasks: [setupTask],
            postToUi: action => action()
        );

        await sut.RefreshReadinessAsync();

        Assert.True(sut.IsModelReady, $"Model not ready: {sut.ModelStatusDetail}");
        Assert.True(sut.IsHotkeyReady, $"Hotkey not ready: {sut.HotkeyStatusDetail}");
        Assert.False(sut.HasSetupAction, $"Has setup action: {sut.SetupNextActionTitle} - {sut.SetupNextActionSummary}");
        Assert.True(sut.IsDictationReady);
        Assert.Equal(Loc.Instance["Dashboard.ReadinessReady"], sut.ReadinessStatusText);
        Assert.Equal("ok", sut.ReadinessTone);
        Assert.Equal("✓", sut.ReadinessGlyph);
        Assert.Contains("Test Engine", sut.CurrentModelDisplayName);
        Assert.Contains("Whisper Base", sut.CurrentModelDisplayName);
        Assert.Equal(Loc.Instance["Dashboard.ModelReady"], sut.ModelStatusDetail);
        Assert.Equal("Ctrl+Shift+Space", sut.CurrentHotkeyDisplay);
        Assert.False(sut.HasSetupAction);
    }

    [Fact]
    public async Task Readiness_WhenNoModelSelected_ReportsNoModelAndSetupNeeded()
    {
        var settings = new SettingsService(Path.Join(_tempDir, "settings.json"));
        settings.Update(s => s with { SelectedModelId = null });

        using var sut = new DashboardSectionViewModel(
            new HistoryService(Path.Join(_tempDir, "history.json")),
            settings,
            new HistoryInsightsService(),
            new FakeStatistics(),
            postToUi: action => action()
        );

        await sut.RefreshReadinessAsync();

        Assert.False(sut.IsDictationReady);
        Assert.Equal(Loc.Instance["Dashboard.NoModelSelected"], sut.CurrentModelDisplayName);
        Assert.True(sut.HasSetupAction);
        Assert.Equal("warning", sut.ReadinessTone);
    }

    [Fact]
    public async Task Readiness_WhenRequiredSetupTaskUnsatisfied_ReportsSetupAction()
    {
        var settings = new SettingsService(Path.Join(_tempDir, "settings.json"));
        var modelId = ModelManagerService.GetPluginModelId("provider", "base");
        settings.Update(s => s with { SelectedModelId = modelId });

        using var plugins = TestPluginManagerFactory.Create();
        var engine = new Moq.Mock<ITranscriptionEngineRole>();
        engine.SetupGet(p => p.PluginId).Returns("provider");
        engine.SetupGet(p => p.ProviderId).Returns("provider");
        engine.SetupGet(p => p.ProviderDisplayName).Returns("Test Engine");
        engine.Setup(p => p.IsModelDownloaded("base")).Returns(true);
        engine.SetupGet(p => p.SupportsModelDownload).Returns(true);
        TypeWhisper.Tests.PluginManagerTestAccess.SetTranscriptionEngines(plugins, [engine.Object]);

        using var models = new ModelManagerService(plugins, settings);
        using var hotkey = TestShortcutBackend.CreateHotkeyService();
        hotkey.Initialize();

        var setupTask = new FakeDashboardSetupTask(
            "ydotool",
            "Auto paste setup",
            SetupTaskSeverity.Required,
            SetupTaskStatusKind.NeedsAction,
            "ydotool service not running",
            "Start ydotool"
        );

        using var sut = new DashboardSectionViewModel(
            new HistoryService(Path.Join(_tempDir, "history.json")),
            settings,
            new HistoryInsightsService(),
            new FakeStatistics(),
            plugins: plugins,
            models: models,
            hotkey: hotkey,
            setupTasks: [setupTask],
            postToUi: action => action()
        );

        await sut.RefreshReadinessAsync();

        Assert.False(sut.IsDictationReady);
        Assert.True(sut.HasSetupAction);
        Assert.Equal("Auto paste setup", sut.SetupNextActionTitle);
        Assert.Equal("Start ydotool", sut.SetupActionLabel);
        Assert.True(sut.CanRunDirectSetupAction);

        sut.RunSetupActionCommand.Execute(null);
        Assert.True(setupTask.RanAction);
    }

    [Fact]
    public void Statistics_IsCollapsibleAndDefaultsToCollapsed()
    {
        var settings = new SettingsService(Path.Join(_tempDir, "settings.json"));
        using var sut = new DashboardSectionViewModel(
            new HistoryService(Path.Join(_tempDir, "history.json")),
            settings,
            new HistoryInsightsService(),
            new FakeStatistics()
        );

        Assert.False(sut.IsStatisticsExpanded);
        Assert.Equal(Loc.Instance["Dashboard.ShowStatistics"], sut.StatisticsToggleLabel);

        sut.ToggleStatisticsCommand.Execute(null);

        Assert.True(sut.IsStatisticsExpanded);
        Assert.Equal(Loc.Instance["Dashboard.HideStatistics"], sut.StatisticsToggleLabel);

        sut.ToggleStatisticsCommand.Execute(null);

        Assert.False(sut.IsStatisticsExpanded);
        Assert.Equal(Loc.Instance["Dashboard.ShowStatistics"], sut.StatisticsToggleLabel);
    }

    [Fact]
    public async Task RecentActivity_EmptyState_ProvidesActionableGuidance()
    {
        var settings = new SettingsService(Path.Join(_tempDir, "settings.json"));
        using var sut = new DashboardSectionViewModel(
            new HistoryService(Path.Join(_tempDir, "history.json")),
            settings,
            new HistoryInsightsService(),
            new FakeStatistics(),
            postToUi: action => action()
        );

        await sut.RefreshReadinessAsync();

        Assert.False(sut.HasRecentActivity);
        Assert.NotEmpty(sut.EmptyActivityGuidance);
    }

    [Fact]
    public async Task Readiness_WhenRequiredSetupTaskThrowsException_ReportsSetupFailureAndPreservesError()
    {
        var settingsPath = Path.Join(_tempDir, "settings.json");
        var settings = new SettingsService(settingsPath);
        var modelId = ModelManagerService.GetPluginModelId("provider", "base");
        settings.Update(s => s with
        {
            SelectedModelId = modelId,
            ToggleHotkey = "Ctrl+Shift+Space",
        });

        using var plugins = TestPluginManagerFactory.Create();
        var engine = new Moq.Mock<ITranscriptionEngineRole>();
        engine.SetupGet(p => p.PluginId).Returns("provider");
        engine.SetupGet(p => p.ProviderId).Returns("provider");
        engine.SetupGet(p => p.ProviderDisplayName).Returns("Test Engine");
        engine.Setup(p => p.IsModelDownloaded("base")).Returns(true);
        engine.SetupGet(p => p.SupportsModelDownload).Returns(true);
        TypeWhisper.Tests.PluginManagerTestAccess.SetTranscriptionEngines(plugins, [engine.Object]);

        using var models = new ModelManagerService(plugins, settings);
        using var hotkey = TestShortcutBackend.CreateHotkeyService();
        hotkey.Initialize();
        hotkey.TrySetHotkeyFromString("Ctrl+Shift+Space");

        var failingTask = new ThrowingDashboardSetupTask("crash", "Crash Task", SetupTaskSeverity.Required, new InvalidOperationException("Device unreachable"));

        using var sut = new DashboardSectionViewModel(
            new HistoryService(Path.Join(_tempDir, "history.json")),
            settings,
            new HistoryInsightsService(),
            new FakeStatistics(),
            plugins: plugins,
            models: models,
            hotkey: hotkey,
            setupTasks: [failingTask],
            postToUi: action => action()
        );

        await sut.RefreshReadinessAsync();

        Assert.False(sut.IsDictationReady);
        Assert.Equal("warning", sut.ReadinessTone);
        Assert.Equal(Loc.Instance["Dashboard.ReadinessNeedsSetup"], sut.ReadinessStatusText);
        Assert.True(sut.HasSetupAction);
        Assert.Equal("Crash Task", sut.SetupNextActionTitle);
        Assert.Contains("Device unreachable", sut.SetupNextActionSummary);
    }

    [Fact]
    public async Task Readiness_WhenHotkeyBackendInactiveOrNull_ReportsInactiveBackendAndNotReady()
    {
        var settingsPath = Path.Join(_tempDir, "settings.json");
        var settings = new SettingsService(settingsPath);
        var modelId = ModelManagerService.GetPluginModelId("provider", "base");
        settings.Update(s => s with
        {
            SelectedModelId = modelId,
            ToggleHotkey = "Ctrl+Shift+Space",
        });

        using var plugins = TestPluginManagerFactory.Create();
        var engine = new Moq.Mock<ITranscriptionEngineRole>();
        engine.SetupGet(p => p.PluginId).Returns("provider");
        engine.SetupGet(p => p.ProviderId).Returns("provider");
        engine.SetupGet(p => p.ProviderDisplayName).Returns("Test Engine");
        engine.Setup(p => p.IsModelDownloaded("base")).Returns(true);
        engine.SetupGet(p => p.SupportsModelDownload).Returns(true);
        TypeWhisper.Tests.PluginManagerTestAccess.SetTranscriptionEngines(plugins, [engine.Object]);

        using var models = new ModelManagerService(plugins, settings);

        // hotkey service is null (backend inactive/absent)
        using var sut = new DashboardSectionViewModel(
            new HistoryService(Path.Join(_tempDir, "history.json")),
            settings,
            new HistoryInsightsService(),
            new FakeStatistics(),
            plugins: plugins,
            models: models,
            hotkey: null,
            postToUi: action => action()
        );

        await sut.RefreshReadinessAsync();

        Assert.False(sut.IsHotkeyReady);
        Assert.False(sut.IsDictationReady);
        Assert.NotEqual(Loc.Instance["Dashboard.HotkeyActive"], sut.HotkeyStatusDetail);
        Assert.True(sut.HasSetupAction);
    }

    [Fact]
    public async Task Readiness_WhenHotkeyMissing_ProvidesActionableGuidanceAndDoesNotClaimReady()
    {
        var settingsPath = Path.Join(_tempDir, "settings.json");
        var settings = new SettingsService(settingsPath);
        var modelId = ModelManagerService.GetPluginModelId("provider", "base");
        settings.Update(s => s with
        {
            SelectedModelId = modelId,
            ToggleHotkey = "",
        });

        using var plugins = TestPluginManagerFactory.Create();
        var engine = new Moq.Mock<ITranscriptionEngineRole>();
        engine.SetupGet(p => p.PluginId).Returns("provider");
        engine.SetupGet(p => p.ProviderId).Returns("provider");
        engine.SetupGet(p => p.ProviderDisplayName).Returns("Test Engine");
        engine.Setup(p => p.IsModelDownloaded("base")).Returns(true);
        engine.SetupGet(p => p.SupportsModelDownload).Returns(true);
        TypeWhisper.Tests.PluginManagerTestAccess.SetTranscriptionEngines(plugins, [engine.Object]);

        using var models = new ModelManagerService(plugins, settings);
        using var hotkey = TestShortcutBackend.CreateHotkeyService();
        hotkey.Initialize();

        using var sut = new DashboardSectionViewModel(
            new HistoryService(Path.Join(_tempDir, "history.json")),
            settings,
            new HistoryInsightsService(),
            new FakeStatistics(),
            plugins: plugins,
            models: models,
            hotkey: hotkey,
            postToUi: action => action()
        );

        await sut.RefreshReadinessAsync();

        Assert.False(sut.IsHotkeyReady);
        Assert.False(sut.IsDictationReady);
        Assert.True(sut.HasSetupAction);
        Assert.Equal(Loc.Instance["Dashboard.ReadinessNeedsSetup"], sut.ReadinessStatusText);
        Assert.DoesNotContain("Ready", sut.ReadinessSubtitle, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Not configured", sut.ReadinessSubtitle, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Readiness_WhenModelManagerAbsent_DoesNotAssumeModelReady()
    {
        var settingsPath = Path.Join(_tempDir, "settings.json");
        var settings = new SettingsService(settingsPath);
        settings.Update(s => s with
        {
            SelectedModelId = "provider:large",
            ToggleHotkey = "Ctrl+Shift+Space",
        });

        using var hotkey = TestShortcutBackend.CreateHotkeyService();
        hotkey.Initialize();
        hotkey.TrySetHotkeyFromString("Ctrl+Shift+Space");

        // models is null
        using var sut = new DashboardSectionViewModel(
            new HistoryService(Path.Join(_tempDir, "history.json")),
            settings,
            new HistoryInsightsService(),
            new FakeStatistics(),
            models: null,
            hotkey: hotkey,
            postToUi: action => action()
        );

        await sut.RefreshReadinessAsync();

        Assert.False(sut.IsModelReady);
        Assert.False(sut.IsDictationReady);
        Assert.NotEqual(Loc.Instance["Dashboard.ModelReady"], sut.ModelStatusDetail);
        Assert.True(sut.HasSetupAction);
    }

    [Fact]
    public async Task Readiness_DispatchesToUiAndNewerSnapshotSupersedes()
    {
        var settingsPath = Path.Join(_tempDir, "settings.json");
        var settings = new SettingsService(settingsPath);
        var queue = new System.Collections.Concurrent.ConcurrentQueue<Action>();

        var history = new HistoryService(Path.Join(_tempDir, "history.json"));
        await history.EnsureLoadedAsync();

        var tcs = new TaskCompletionSource();
        var blockingTask = new BlockingDashboardSetupTask("block", "Blocking Task", tcs.Task);

        using var sut = new DashboardSectionViewModel(
            history,
            settings,
            new HistoryInsightsService(),
            new FakeStatistics(),
            TimeZoneInfo.Utc,
            postToUi: queue.Enqueue,
            setupTasks: [blockingTask]
        );

        // Drain any initial initialization
        while (queue.TryDequeue(out var initAction))
        {
            initAction();
        }

        // Start first refresh (will block on task gate)
        var task1 = sut.RefreshReadinessAsync();

        // Second readiness refresh with updated settings
        settings.Update(s => s with { SelectedModelId = "new-model" });
        var task2 = sut.RefreshReadinessAsync();

        // Release first task
        tcs.SetResult();

        await Task.WhenAll(task1, task2);

        // Run all queued UI actions
        while (queue.TryDequeue(out var action))
        {
            action();
        }

        // Newest snapshot should have won
        Assert.Contains("new-model", sut.CurrentModelDisplayName);
    }

    private sealed class BlockingDashboardSetupTask(string id, string title, Task gate) : ISetupTask
    {
        public string Id => id;
        public string Title => title;
        public SetupTaskSeverity Severity => SetupTaskSeverity.Required;
        public bool AppliesToThisMachine() => true;
        public async Task<SetupTaskState> EvaluateAsync(CancellationToken ct)
        {
            await Task.WhenAny(gate, Task.Delay(Timeout.Infinite, ct));
            return new SetupTaskState(SetupTaskStatusKind.Satisfied, "Gate open");
        }
        public Task<SetupActionOutcome> RunActionAsync(CancellationToken ct) =>
            Task.FromResult(new SetupActionOutcome(true, "Done"));
    }

    private sealed class ThrowingDashboardSetupTask(string id, string title, SetupTaskSeverity severity, Exception exception) : ISetupTask
    {
        public string Id => id;
        public string Title => title;
        public SetupTaskSeverity Severity => severity;
        public bool AppliesToThisMachine() => true;
        public Task<SetupTaskState> EvaluateAsync(CancellationToken ct) =>
            Task.FromException<SetupTaskState>(exception);
        public Task<SetupActionOutcome> RunActionAsync(CancellationToken ct) =>
            Task.FromException<SetupActionOutcome>(exception);
    }

    private sealed class FakeDashboardSetupTask(
        string id,
        string title,
        SetupTaskSeverity severity,
        SetupTaskStatusKind kind,
        string summary,
        string? actionLabel = null) : ISetupTask
    {
        public string Id => id;
        public string Title => title;
        public SetupTaskSeverity Severity => severity;
        public bool RanAction { get; private set; }
        public bool AppliesToThisMachine() => true;
        public Task<SetupTaskState> EvaluateAsync(CancellationToken ct) =>
            Task.FromResult(new SetupTaskState(kind, summary, ActionLabel: actionLabel));
        public Task<SetupActionOutcome> RunActionAsync(CancellationToken ct)
        {
            RanAction = true;
            return Task.FromResult(new SetupActionOutcome(true, "Fixed"));
        }
    }
}