using Avalonia;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Linux.Services;

internal static class OverlayDocking
{
    public static PixelPoint ComputePosition(OverlayPosition edge, PixelRect area, PixelSize size, double scaling = 1)
    {
        var inset = (int)Math.Round(12 * (double.IsFinite(scaling) && scaling > 0 ? scaling : 1));
        var x = area.X + (area.Width - size.Width) / 2;
        var y = area.Y + (area.Height - size.Height) / 2;
        switch (edge)
        {
            case OverlayPosition.Top: y = area.Y + inset; break;
            case OverlayPosition.Left: x = area.X + inset; break;
            case OverlayPosition.Right: x = area.Right - size.Width - inset; break;
            default: y = area.Bottom - size.Height - inset; break;
        }
        return new PixelPoint(
            Math.Clamp(x, area.X, Math.Max(area.X, area.Right - size.Width)),
            Math.Clamp(y, area.Y, Math.Max(area.Y, area.Bottom - size.Height)));
    }

    public static OverlayPosition NearestEdge(PixelPoint point, PixelRect area, OverlayPosition preferred = OverlayPosition.Bottom)
    {
        var distances = new (OverlayPosition Edge, long Distance)[]
        {
            (OverlayPosition.Top, Math.Abs((long)point.Y - area.Y)),
            (OverlayPosition.Bottom, Math.Abs((long)point.Y - area.Bottom)),
            (OverlayPosition.Left, Math.Abs((long)point.X - area.X)),
            (OverlayPosition.Right, Math.Abs((long)point.X - area.Right)),
        };
        var nearest = distances.Min(d => d.Distance);
        return distances.Any(d => d.Edge == preferred && d.Distance == nearest)
            ? preferred : distances.First(d => d.Distance == nearest).Edge;
    }

    public static int SelectScreen(IReadOnlyList<(string? Name, PixelRect Bounds, bool IsPrimary)> screens,
        string? savedName, int? savedX, int? savedY)
    {
        for (var i = 0; i < screens.Count; i++)
            if (screens[i].Name == savedName && savedName is not null
                && screens[i].Bounds.X == savedX && screens[i].Bounds.Y == savedY) return i;
        if (!string.IsNullOrEmpty(savedName))
            for (var i = 0; i < screens.Count; i++)
                if (screens[i].Name == savedName) return i;
        for (var i = 0; i < screens.Count; i++)
            if (screens[i].Bounds.X == savedX && screens[i].Bounds.Y == savedY) return i;
        for (var i = 0; i < screens.Count; i++)
            if (screens[i].IsPrimary) return i;
        return screens.Count > 0 ? 0 : -1;
    }

    public static int NearestScreen(IReadOnlyList<PixelRect> screens, PixelPoint point)
    {
        var best = -1;
        var distance = double.PositiveInfinity;
        for (var i = 0; i < screens.Count; i++)
        {
            var area = screens[i];
            var dx = (double)point.X - Math.Clamp(point.X, area.X, area.Right);
            var dy = (double)point.Y - Math.Clamp(point.Y, area.Y, area.Bottom);
            var next = dx * dx + dy * dy;
            if (next < distance) { best = i; distance = next; }
        }
        return best;
    }
}

/// <summary>Keep click and drag intent distinct without relying on a WM's native move grab.</summary>
internal sealed class OverlayDockDrag
{
    private PixelPoint _press;
    private PixelPoint _origin;
    private double _threshold;
    public bool IsPressed { get; private set; }
    public bool IsDragging { get; private set; }
    public PixelPoint Origin => _origin;

    public void Press(PixelPoint point, PixelPoint origin, double scaling)
    {
        _press = point;
        _origin = origin;
        _threshold = 6 * scaling;
        IsPressed = true;
        IsDragging = false;
    }

    public PixelPoint? Move(PixelPoint point)
    {
        if (!IsPressed) return null;
        var dx = (double)point.X - _press.X;
        var dy = (double)point.Y - _press.Y;
        if (!IsDragging && dx * dx + dy * dy < _threshold * _threshold) return null;
        IsDragging = true;
        return new PixelPoint(_origin.X + point.X - _press.X, _origin.Y + point.Y - _press.Y);
    }

    public bool Release()
    {
        var dragged = IsDragging;
        IsPressed = false;
        IsDragging = false;
        return dragged;
    }
}
