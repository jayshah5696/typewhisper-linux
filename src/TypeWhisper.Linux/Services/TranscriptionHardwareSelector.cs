using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Linux.Services;

/// <summary>Host policy over provider-native discovery. Does not probe drivers or provision runtimes.</summary>
internal static class TranscriptionHardwareSelector
{
    internal static TranscriptionDevice CpuDevice { get; } = new(
        TranscriptionAccelerationBackend.Cpu, "cpu", "CPU", TranscriptionDeviceKind.Cpu);

    internal static TranscriptionHardwareRequest Resolve(
        TranscriptionAccelerationPreference requested,
        IReadOnlyList<TranscriptionDevice> devices,
        string? stableId = null)
    {
        var backend = ToBackend(requested);
        var device = devices
            .Where(IsEligible)
            .Where(device => backend is null
                ? device.Backend is TranscriptionAccelerationBackend.NvidiaCuda or TranscriptionAccelerationBackend.Vulkan or TranscriptionAccelerationBackend.Cpu
                : device.Backend == backend)
            .Where(device => stableId is null || device.StableId == stableId)
            .OrderBy(device => BackendPriority(device.Backend))
            .ThenBy(device => DevicePriority(device.Kind))
            .ThenBy(device => device.StableId, StringComparer.Ordinal)
            .FirstOrDefault();
        return device is null
            ? throw new InvalidOperationException(
                $"No available {requested} device{(stableId is null ? "" : $" ({stableId})")} for this model. "
                + "Select CPU to retry without changing the model.")
            : new TranscriptionHardwareRequest(requested, device);
    }

    internal static TranscriptionAccelerationPreference ToPreference(TranscriptionAccelerationBackend backend) => backend switch
    {
        TranscriptionAccelerationBackend.Cpu => TranscriptionAccelerationPreference.Cpu,
        TranscriptionAccelerationBackend.NvidiaCuda => TranscriptionAccelerationPreference.NvidiaCuda,
        TranscriptionAccelerationBackend.Vulkan => TranscriptionAccelerationPreference.Vulkan,
        TranscriptionAccelerationBackend.AmdMigraphx => TranscriptionAccelerationPreference.AmdMigraphx,
        TranscriptionAccelerationBackend.WebGpu => TranscriptionAccelerationPreference.WebGpu,
        _ => throw new ArgumentOutOfRangeException(nameof(backend)),
    };

    private static TranscriptionAccelerationBackend? ToBackend(TranscriptionAccelerationPreference preference) => preference switch
    {
        TranscriptionAccelerationPreference.Auto => null,
        TranscriptionAccelerationPreference.Cpu => TranscriptionAccelerationBackend.Cpu,
        TranscriptionAccelerationPreference.NvidiaCuda => TranscriptionAccelerationBackend.NvidiaCuda,
        TranscriptionAccelerationPreference.Vulkan => TranscriptionAccelerationBackend.Vulkan,
        TranscriptionAccelerationPreference.AmdMigraphx => TranscriptionAccelerationBackend.AmdMigraphx,
        TranscriptionAccelerationPreference.WebGpu => TranscriptionAccelerationBackend.WebGpu,
        _ => throw new ArgumentOutOfRangeException(nameof(preference)),
    };

    internal static bool IsEligible(TranscriptionDevice device) => device.IsAvailable
        && !string.IsNullOrWhiteSpace(device.StableId)
        && device.Kind switch
        {
            TranscriptionDeviceKind.Cpu => device.Backend == TranscriptionAccelerationBackend.Cpu,
            TranscriptionDeviceKind.DiscreteGpu or TranscriptionDeviceKind.IntegratedGpu or TranscriptionDeviceKind.VirtualGpu =>
                device.Backend != TranscriptionAccelerationBackend.Cpu,
            TranscriptionDeviceKind.SoftwareGpu or TranscriptionDeviceKind.Unknown => false,
            _ => false,
        };

    private static int BackendPriority(TranscriptionAccelerationBackend backend) => backend switch
    {
        TranscriptionAccelerationBackend.NvidiaCuda => 0,
        TranscriptionAccelerationBackend.Vulkan => 1,
        TranscriptionAccelerationBackend.Cpu => 2,
        // Other backends remain available explicitly, but Auto does not opt into them.
        TranscriptionAccelerationBackend.AmdMigraphx or TranscriptionAccelerationBackend.WebGpu => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(backend)),
    };

    private static int DevicePriority(TranscriptionDeviceKind kind) => kind switch
    {
        TranscriptionDeviceKind.DiscreteGpu => 0,
        TranscriptionDeviceKind.IntegratedGpu => 1,
        TranscriptionDeviceKind.VirtualGpu => 2,
        TranscriptionDeviceKind.Cpu => 3,
        TranscriptionDeviceKind.SoftwareGpu or TranscriptionDeviceKind.Unknown => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
