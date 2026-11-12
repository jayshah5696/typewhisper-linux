namespace TypeWhisper.PluginSDK.Models;

/// <summary>Plain device identity suitable for settings and worker transport, without native handles.</summary>
/// <param name="StableId">Provider-native stable identity, such as a PCI address. Never an enumeration index.</param>
/// <param name="IsAvailable">Whether the runtime is already ready. Discovery must not provision it.</param>
public sealed record TranscriptionDevice(
    TranscriptionAccelerationBackend Backend,
    string StableId,
    string DisplayName,
    TranscriptionDeviceKind Kind,
    bool IsAvailable = true,
    string? UnavailableReason = null);

/// <summary>Device classification reported by the provider's native runtime.</summary>
public enum TranscriptionDeviceKind
{
    Unknown,
    Cpu,
    IntegratedGpu,
    DiscreteGpu,
    VirtualGpu,
    SoftwareGpu,
}

/// <summary>User intent and resolved execution device. Backend plus stable identity identifies a device.</summary>
public sealed record TranscriptionHardwareRequest(
    TranscriptionAccelerationPreference RequestedPreference,
    TranscriptionDevice Device);
