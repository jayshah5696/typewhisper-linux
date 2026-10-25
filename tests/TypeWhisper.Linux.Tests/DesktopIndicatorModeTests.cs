using TypeWhisper.Linux.Services.Hotkey.DeSetup;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class DesktopIndicatorModeTests
{
    [Theory]
    [InlineData("GNOME", "1", true)]
    [InlineData("GNOME", null, false)]
    [InlineData("GNOME", "0", false)]
    [InlineData("GNOME", "invalid", false)]
    [InlineData("sway", null, true)]
    [InlineData("niri", null, true)]
    public void NotificationMode_CanDisableOverlayWindowsWithoutChangingDesktopDetection(
        string desktop, string? disableOverlays, bool expected)
    {
        string[] names = ["XDG_CURRENT_DESKTOP", "HYPRLAND_INSTANCE_SIGNATURE", "SWAYSOCK", "TYPEWHISPER_DISABLE_OVERLAYS"];
        var original = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var name in names)
            {
                Environment.SetEnvironmentVariable(name, null);
            }

            Environment.SetEnvironmentVariable("XDG_CURRENT_DESKTOP", desktop);
            var detectedDesktop = DesktopDetector.DetectId();
            Environment.SetEnvironmentVariable("TYPEWHISPER_DISABLE_OVERLAYS", disableOverlays);

            Assert.Equal(expected, DesktopDetector.UsesNotificationRecordingIndicator());
            Assert.Equal(detectedDesktop, DesktopDetector.DetectId());
        }
        finally
        {
            foreach (var (name, value) in original)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }
}
