using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using Avalonia.Platform;
using System.ComponentModel;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Hotkey.DeSetup;
using TypeWhisper.Linux.ViewModels;

namespace TypeWhisper.Linux.Views;

public partial class DictationOverlayWindow : Window
{
    private readonly ISettingsService? _settings;
    private readonly DictationOverlayViewModel? _viewModel;
    private readonly DictationOverlayPlacementState _placementState = new();
    private bool _userDragging;
    private bool _programmaticPositionChange;
    private DispatcherTimer? _dragSaveTimer;
    private PixelPoint? _pendingDragPosition;
    private readonly OverlayDockDrag _dockDrag = new();
    private IPointer? _dockPointer;
    private bool _changingDockCapture;
    private readonly DispatcherTimer _launcherCollapseTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };

    public DictationOverlayWindow()
    {
        InitializeComponent();
    }

    public DictationOverlayWindow(DictationOverlayViewModel viewModel, ISettingsService settings)
        : this()
    {
        DataContext = _viewModel = viewModel;
        _settings = settings;

        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        Topmost = true;

        Title = "TypeWhisper Overlay";

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _settings.SettingsChanged += _ => Dispatcher.UIThread.Post(() =>
        {
            if (!_settings.Current.ShowFloatingControl) CancelDockDrag();
            ConfigureNativeWindow();
            PositionOverlay();
        });

        Opened += OnOverlayOpened;
        Closed += OnOverlayClosed;
        SizeChanged += (_, _) => PositionOverlay();

        AddHandler(PointerPressedEvent, OnDockPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnDockMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnDockReleased, RoutingStrategies.Tunnel);
        PointerPressed += OnUserPointerPressed;
        PointerReleased += OnUserPointerReleased;
        PointerCaptureLost += OnUserPointerCaptureLost;
        PositionChanged += OnUserPositionChanged;
        PointerEntered += (_, _) =>
        {
            _launcherCollapseTimer.Stop();
            _viewModel.SetLauncherExpanded(true);
        };
        PointerExited += (_, _) =>
        {
            if (_viewModel.ShowLauncher && !_dockDrag.IsPressed)
                _launcherCollapseTimer.Start();
        };
        _launcherCollapseTimer.Tick += (_, _) =>
        {
            _launcherCollapseTimer.Stop();
            if (!IsPointerOver && !_dockDrag.IsPressed)
                _viewModel.SetLauncherExpanded(false);
        };
    }

    private void OnOverlayOpened(object? sender, EventArgs e)
    {
        ConfigureNativeWindow();
        PositionOverlay();

        // Recover from display changes (monitor hotplug, resolution change, resume from
        // sleep, session unlock) — the WM can leave the overlay off-screen or on a monitor
        // that no longer exists. Re-running PositionOverlay re-clamps it to a valid work
        // area (or the saved screen if it's back). Avalonia surfaces all of these through
        // Screens.Changed; the Win32 SystemEvents equivalents upstream uses don't exist here.
        if (Screens is { } screens)
        {
            screens.Changed += OnScreensChanged;
        }
    }

    private void ConfigureNativeWindow()
    {
        if (TryGetPlatformHandle() is { HandleDescriptor: "XID", Handle: var xid } && xid != IntPtr.Zero)
            X11NonActivatingWindow.Apply((nuint)xid, _settings?.Current.ShowFloatingControl == true);
    }

    private void OnOverlayClosed(object? sender, EventArgs e)
    {
        _launcherCollapseTimer.Stop();
        CancelDockDrag();
        if (Screens is { } screens)
        {
            screens.Changed -= OnScreensChanged;
        }
    }

    private void OnScreensChanged(object? sender, EventArgs e)
    {
        CancelDockDrag();
        // Post so Avalonia has settled the new screen geometry before we re-measure work areas.
        Dispatcher.UIThread.Post(PositionOverlay);
    }

    public void Initialize()
    {
        if (_viewModel is null)
        {
            return;
        }

        UpdateWindowVisibility();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DictationOverlayViewModel.HasVisibleContent):
                if (!_viewModel!.ShowLauncher) CancelDockDrag();
                Dispatcher.UIThread.Post(UpdateWindowVisibility);
                break;
            case nameof(DictationOverlayViewModel.PartialText):
            {
                // Background priority so the scroll runs after layout measures the updated text.
                var partialText = _viewModel?.PartialText;
                Dispatcher.UIThread.Post(
                    () =>
                    {
                        if (string.IsNullOrWhiteSpace(partialText))
                        {
                            PartialPreviewScrollViewer?.ScrollToHome();
                            CompactPartialPreviewScrollViewer?.ScrollToHome();
                        }
                        else
                        {
                            PartialPreviewScrollViewer?.ScrollToEnd();
                            CompactPartialPreviewScrollViewer?.ScrollToEnd();
                        }
                    },
                    DispatcherPriority.Background);
                break;
            }
            case nameof(DictationOverlayViewModel.LlmResponseText):
            {
                // Same after-layout auto-scroll as PartialText, for the streamed LLM response area.
                var llmText = _viewModel?.LlmResponseText;
                Dispatcher.UIThread.Post(
                    () =>
                    {
                        if (string.IsNullOrWhiteSpace(llmText))
                        {
                            LlmResponseScrollViewer?.ScrollToHome();
                        }
                        else
                        {
                            LlmResponseScrollViewer?.ScrollToEnd();
                        }
                    },
                    DispatcherPriority.Background);
                break;
            }
        }
    }

    private void UpdateWindowVisibility()
    {
        if (_viewModel is null)
        {
            return;
        }

        // On tiling WMs the overlay is suppressed: an XWayland toplevel on a tiler reserves
        // a tile, steals focus, and blurs into a box — use the notification indicator instead.
        if (s_usesNotificationIndicator)
        {
            if (IsVisible)
            {
                Hide();
            }

            return;
        }

        // WORKAROUND (backlog item 16): Show() once and drive visibility via Opacity instead of
        // Hide() — Avalonia's Show() after Hide() is unreliable on GNOME Mutter for utility windows
        // (ShowActivated=False / Topmost / ShowInTaskbar=False): some shows leave the window
        // invisible until restart. Inner Border bindings still control which content is drawn.
        var hasContent = _viewModel.HasVisibleContent;

        if (!IsVisible)
        {
            // Keep the first mapping transparent too: OnOverlayOpened parks it before
            // _placementState.Show() runs, so a content-bearing first show is only revealed
            // by the Loaded-priority reposition below.
            Opacity = 0.0;
            IsHitTestVisible = false;
            Show();
            MakeStickyAcrossWorkspaces();
        }

        if (hasContent)
        {
            _placementState.Show();

            // Post at Loaded so a size-changing transition uses final dimensions; staying
            // transparent at the parked position until this runs avoids a visible jump.
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (!_placementState.IsShown)
                    {
                        return;
                    }

                    PositionOverlay();
                    if (!_placementState.IsShown)
                    {
                        return;
                    }

                    SetClickThrough(false);
                    Opacity = 1.0;
                    IsHitTestVisible = true;
                },
                DispatcherPriority.Loaded
            );
            return;
        }

        // Opacity and Avalonia's IsHitTestVisible do not clear a mapped toplevel's native X11
        // input region, so this Topmost window would leave a dead-click rectangle. Empty that
        // region, since Mutter clamps the off-screen park back onto the nearest monitor.
        Opacity = 0.0;
        IsHitTestVisible = false;
        SetClickThrough(true);
        SetPositionProgrammatically(
            _placementState.Hide(CollectScreenBounds(), Position)
        );
    }

    // Cached — the desktop environment can't change within a session.
    private static readonly bool s_usesNotificationIndicator =
        DesktopDetector.UsesNotificationRecordingIndicator();

    // The overlay is mapped once and kept alive via Opacity (see UpdateWindowVisibility),
    // so it stays pinned to the workspace it was first mapped on. Marking it sticky lets the
    // WM show it on the active workspace instead — so the recording indicator follows the user.
    // Posted at Loaded priority so the X11 toplevel is mapped before the request is sent
    // (an unmapped window's _NET_WM_STATE ClientMessage is ignored by the WM).
    private void MakeStickyAcrossWorkspaces()
    {
        Dispatcher.UIThread.Post(
            () =>
            {
                var handle = TryGetPlatformHandle();
                if (handle is { Handle: var xid } && xid != IntPtr.Zero)
                {
                    X11StickyWindow.MakeSticky((nuint)xid);
                }
            },
            DispatcherPriority.Loaded);
    }

    private void SetClickThrough(bool passThrough)
    {
        if (TryGetPlatformHandle() is { Handle: var xid } && xid != IntPtr.Zero)
        {
            X11InputPassthrough.Set((nuint)xid, passThrough);
        }
    }

    private void PositionOverlay()
    {
        if (!IsVisible)
        {
            return;
        }

        var screenBounds = CollectScreenBounds();

        // IsVisible stays true for the mapped-once Mutter workaround. Consult our own content
        // state instead, so settings, screen, and size events recompute (or preserve) an offscreen
        // parked position rather than moving the transparent X11 input rectangle back on-screen.
        if (!_placementState.IsShown)
        {
            SetPositionProgrammatically(
                _placementState.Reposition(Position, screenBounds, Position)
            );
            return;
        }

        if (_settings is null)
        {
            return;
        }

        // Don't fight the user while they're dragging the overlay
        // — a SizeChanged mid-drag would otherwise yank the window.
        if (_userDragging)
        {
            return;
        }

        // ReSharper disable once ConditionalAccessQualifierIsNonNullableAccordingToAPIContract -- Avalonia annotates Screens non-null, but it can be null before the platform window is realized (headless/early lifecycle); keep the defensive ?. consistent with the Screens?. access below.
        var primaryScreen = Screens?.Primary ?? (Screens?.All is { Count: > 0 } available ? available[0] : null);
        if (primaryScreen is null)
        {
            return;
        }

        var savedPoint = _settings.Current is { OverlayCustomLeft: { } left, OverlayCustomTop: { } top }
            ? new PixelPoint((int)Math.Round(left), (int)Math.Round(top))
            : (PixelPoint?)null;
        var docking = _settings.Current.ShowFloatingControl;
        var dockScreen = SelectDockScreen() ?? primaryScreen;
        var sizingScreen = docking ? dockScreen
            : savedPoint is { } point ? Screens?.ScreenFromPoint(point) ?? primaryScreen : primaryScreen;
        MaxWidth = Math.Max(36, sizingScreen.WorkingArea.Width / sizingScreen.Scaling);
        MaxHeight = Math.Max(36, sizingScreen.WorkingArea.Height / sizingScreen.Scaling);
        var pixelSize = PixelSize.FromSize(Bounds.Size, sizingScreen.Scaling);
        var width = Math.Max(1, pixelSize.Width);
        var height = Math.Max(1, pixelSize.Height);

        if (!docking && _settings.Current is { OverlayCustomLeft: { } customLeft, OverlayCustomTop: { } customTop })
        {
            // Clamp to the screen the saved point is on, not always primary — otherwise
            // dragging to a secondary monitor would snap back on the debounced save.
            // Fall back to primary only when the point is off every screen (e.g. unplugged
            // monitor) so re-plugging restores the saved position.
            var customPoint = new PixelPoint(
                (int)Math.Round(customLeft),
                (int)Math.Round(customTop));
            var targetScreen = Screens?.ScreenFromPoint(customPoint) ?? primaryScreen;
            var targetWorkArea = targetScreen.WorkingArea;
            var (clampedLeft, clampedTop) = AppSettings.ClampOverlayPositionToWorkArea(
                customLeft,
                customTop,
                targetWorkArea.X,
                targetWorkArea.Y,
                targetWorkArea.Right,
                targetWorkArea.Bottom,
                width,
                height);
            SetPositionProgrammatically(
                _placementState.Reposition(
                    new PixelPoint(
                        (int)Math.Round(clampedLeft),
                        (int)Math.Round(clampedTop)
                    ),
                    screenBounds,
                    Position
                )
            );
            return;
        }

        var workArea = sizingScreen.WorkingArea;
        var configuredPosition = OverlayDocking.ComputePosition(
            _settings.Current.OverlayPosition,
            workArea,
            new PixelSize(width, height),
            sizingScreen.Scaling
        );

        SetPositionProgrammatically(
            _placementState.Reposition(
                configuredPosition,
                screenBounds,
                Position
            )
        );
    }

    private Screen? SelectDockScreen()
    {
        var screens = Screens?.All;
        if (screens is not { Count: > 0 } || _settings is null) return null;
        var settings = _settings.Current;
        if (settings.OverlayScreenName is null && settings.OverlayScreenX is null
            && settings is { OverlayCustomLeft: { } left, OverlayCustomTop: { } top })
            return Screens?.ScreenFromPoint(new PixelPoint((int)Math.Round(left), (int)Math.Round(top)));
        var index = OverlayDocking.SelectScreen(screens.Select(s => ((string?)s.DisplayName, s.Bounds, s.IsPrimary)).ToArray(),
            settings.OverlayScreenName, settings.OverlayScreenX, settings.OverlayScreenY);
        return index >= 0 ? screens[index] : null;
    }

    private void OnDockPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel?.ShowLauncher != true || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || e.Source is not Visual source
            || !(source == ExpandLauncherButton || source.GetVisualAncestors().Contains(ExpandLauncherButton))) return;
        _dockDrag.Press(this.PointToScreen(e.GetPosition(this)), Position, RenderScaling);
        _dockPointer = e.Pointer;
        _launcherCollapseTimer.Stop();
    }

    private Screen? ScreenForDrop(PixelPoint point)
    {
        var screens = Screens?.All;
        if (screens is not { Count: > 0 }) return null;
        var index = OverlayDocking.NearestScreen(screens.Select(s => s.Bounds).ToArray(), point);
        return index >= 0 ? screens[index] : null;
    }

    private void OnDockMoved(object? sender, PointerEventArgs e)
    {
        if (_dockPointer != e.Pointer || !_dockDrag.IsPressed) return;
        var point = this.PointToScreen(e.GetPosition(this));
        if (_dockDrag.Move(point) is not { } position) return;
        _userDragging = true;
        _changingDockCapture = true;
        try { e.Pointer.Capture(this); }
        finally { _changingDockCapture = false; }
        SetPositionProgrammatically(position);
        if (ScreenForDrop(point) is { } screen)
            _viewModel!.DockTargetEdge = OverlayDocking.NearestEdge(point, screen.WorkingArea, _settings!.Current.OverlayPosition);
        e.Handled = true;
    }

    private void OnDockReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dockPointer != e.Pointer || !_dockDrag.IsPressed) return;
        var point = this.PointToScreen(e.GetPosition(this));
        var dragged = _dockDrag.Release();
        _dockPointer = null;
        _userDragging = false;
        _viewModel!.DockTargetEdge = null;
        if (!dragged) return;
        e.Handled = true;
        e.Pointer.Capture(null);
        if (ScreenForDrop(point) is { } screen)
        {
            var edge = OverlayDocking.NearestEdge(point, screen.WorkingArea, _settings!.Current.OverlayPosition);
            _settings.Update(current => current with
            {
                OverlayPosition = edge, OverlayCustomLeft = null, OverlayCustomTop = null,
                OverlayScreenName = screen.DisplayName, OverlayScreenX = screen.Bounds.X, OverlayScreenY = screen.Bounds.Y,
            });
        }
        PositionOverlay();
        if (!IsPointerOver) _launcherCollapseTimer.Start();
    }

    private void CancelDockDrag()
    {
        if (!_dockDrag.IsPressed) return;
        var origin = _dockDrag.Origin;
        _dockDrag.Release();
        var pointer = _dockPointer;
        _dockPointer = null;
        _userDragging = false;
        _viewModel!.DockTargetEdge = null;
        pointer?.Capture(null);
        if (IsVisible) SetPositionProgrammatically(origin);
    }

    private List<PixelRect> CollectScreenBounds()
    {
        var result = new List<PixelRect>();

        var screens = Screens;
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract -- Avalonia annotates Screens non-null, but it can be null before the platform window is realized; the placement state tolerates an empty list (mirrors LearnedCorrectionToastWindow).
        if (screens is null)
        {
            return result;
        }

        // Put the primary first so parking uses a stable Y coordinate, matching the correction
        // toast's documented native-X11 workaround.
        if (screens.Primary is { } primary)
        {
            result.Add(primary.Bounds);
        }

        result.AddRange(
            screens.All
                .Where(screen => !ReferenceEquals(screen, screens.Primary))
                .Select(screen => screen.Bounds)
        );

        return result;
    }

    private void SetPositionProgrammatically(PixelPoint point)
    {
        _programmaticPositionChange = true;
        try
        {
            Position = point;
        }
        finally
        {
            _programmaticPositionChange = false;
        }
    }

    private void OnUserPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_settings is null || _settings.Current.ShowFloatingControl || e.Source is Visual visual
            && (visual is Button || visual.GetVisualAncestors().OfType<Button>().Any()))
        {
            return;
        }

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _userDragging = true;
        BeginMoveDrag(e);
    }

    private void OnUserPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        EndUserDrag();
    }

    private void OnUserPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_changingDockCapture) return;
        if (_dockDrag.IsPressed) CancelDockDrag();
        EndUserDrag();
    }

    private void EndUserDrag()
    {
        _userDragging = false;

        // A move-drag that outlived a hide (content cleared mid-drag) leaves the still-mapped
        // window on-screen wherever the WM's interactive move dropped it — that grab overrides
        // our one-off park while active. Its native X11 input region stays live regardless of
        // IsHitTestVisible, so re-park now rather than waiting for a later screen/settings/size event.
        if (!_placementState.IsShown)
        {
            PositionOverlay();
        }
    }

    private void OnUserPositionChanged(object? sender, PixelPointEventArgs e)
    {
        if (_programmaticPositionChange || _settings is null || _settings.Current.ShowFloatingControl)
        {
            return;
        }

        // A hidden overlay is only ever moved programmatically (parked off-screen). On X11 the
        // move's PositionChanged arrives asynchronously — after SetPositionProgrammatically has
        // cleared _programmaticPositionChange — so if content clears mid-drag the parked sentinel
        // could be mistaken for a user drag and persisted as the saved position. Never persist a
        // position while parked.
        if (!_placementState.IsShown)
        {
            return;
        }

        if (!_userDragging)
        {
            return;
        }

        _pendingDragPosition = e.Point;

        _dragSaveTimer ??= new DispatcherTimer(
            TimeSpan.FromMilliseconds(250),
            DispatcherPriority.Background,
            OnDragSaveTimerTick);
        _dragSaveTimer.Stop();
        _dragSaveTimer.Start();
    }

    private void OnDragSaveTimerTick(object? sender, EventArgs e)
    {
        _dragSaveTimer?.Stop();

        if (_settings is null || _settings.Current.ShowFloatingControl || _pendingDragPosition is null)
        {
            return;
        }

        var pos = _pendingDragPosition.Value;
        _pendingDragPosition = null;

        _settings.Update(current => current with
        {
            OverlayCustomLeft = (double)pos.X,
            OverlayCustomTop = (double)pos.Y,
        });
    }
}

/// <summary>
///     Deterministic visibility and placement decisions for the mapped-once dictation overlay,
///     kept independent of Window/Screens so it can be unit tested without a live compositor.
/// </summary>
internal sealed class DictationOverlayPlacementState
{
    public bool IsShown { get; private set; }

    public void Show()
    {
        IsShown = true;
    }

    public PixelPoint Hide(
        IReadOnlyList<PixelRect> screenBounds,
        PixelPoint currentPosition
    )
    {
        IsShown = false;
        return ComputeParkedPosition(screenBounds, currentPosition);
    }

    public PixelPoint Reposition(
        PixelPoint configuredPosition,
        IReadOnlyList<PixelRect> screenBounds,
        PixelPoint currentPosition
    )
    {
        return IsShown
            ? configuredPosition
            : ComputeParkedPosition(screenBounds, currentPosition);
    }

    private static PixelPoint ComputeParkedPosition(
        IReadOnlyList<PixelRect> screenBounds,
        PixelPoint currentPosition
    )
    {
        if (screenBounds.Count == 0)
        {
            return currentPosition;
        }

        // Match LearnedCorrectionToastWindow: the left edge just beyond the union's right boundary
        // puts the entire mapped window outside every monitor, including negative-origin layouts.
        var right = screenBounds.Max(bounds => bounds.Right);
        return new PixelPoint(right + 1, screenBounds[0].Y);
    }
}
