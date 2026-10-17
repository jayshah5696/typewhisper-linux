using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.Localization;

namespace TypeWhisper.Linux.Services;

internal static class CleanupOutcomeText
{
    public static string ForFallback(CleanupFallbackReason reason) => Loc.Instance[reason switch
    {
        CleanupFallbackReason.TimedOut => "Recovery.CleanupTimedOut",
        CleanupFallbackReason.ProviderUnavailable => "Recovery.ProviderUnavailable",
        CleanupFallbackReason.EmptyResponse => "Recovery.EmptyResponse",
        _ => "Recovery.CleanupFailed",
    }];
}
