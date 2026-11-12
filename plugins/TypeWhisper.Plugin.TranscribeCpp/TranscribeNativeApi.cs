using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TypeWhisper.Plugin.TranscribeCpp;

// Layouts are pinned to 0.4.1. Verify every size and alignment before invoking ANY *_init function.
internal static class TranscribeNativeApi
{
    private const string Library = "typewhisper-transcribe";

    [StructLayout(LayoutKind.Sequential)]
    internal struct BackendParams { internal ulong Size; internal IntPtr Directory; internal uint Allowed; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceInfo
    {
        internal ulong Size;
        internal IntPtr Name, Description, Kind, Id;
        internal ulong MemoryTotal, MemoryFree;
        internal int Type;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct LoadParams { internal ulong Size; internal int Backend; internal IntPtr Device; internal byte NoMmap; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct RunParams
    {
        internal ulong Size;
        internal int Task, Timestamps, Pnc, Itn, Diarize;
        internal IntPtr Language, TargetLanguage;
        internal byte KeepSpecialTags;
        internal IntPtr Family;
        internal int SpecDrafts;
        internal IntPtr Vocabulary;
        internal int VocabularyCount;
        internal IntPtr Prompt, Prefix;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Capabilities
    {
        internal ulong Size;
        internal int SampleRate, LanguageCount;
        internal IntPtr Languages;
        internal int Timestamps;
        internal byte LanguageDetection, Translation, Streaming, SpecDecode;
        internal long MaxAudioMs;
        internal int TranslationLanguageCount;
        internal IntPtr TranslationLanguages;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct SessionLimits { internal ulong Size; internal int Context; internal long MaxAudioMs, MaxKvBytes; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct StreamParams { internal ulong Size; internal IntPtr Family; internal int CommitPolicy; internal uint AgreementCount; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct StreamUpdate
    {
        internal ulong Size;
        internal byte Changed, Final;
        internal int Revision;
        internal long ReceivedMs, CommittedMs, BufferedMs;
        internal byte CommittedChanged, TentativeChanged;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal delegate bool AbortCallback(IntPtr state);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr transcribe_version();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern nuint transcribe_abi_struct_size(int id);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern nuint transcribe_abi_struct_align(int id);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void transcribe_backend_init_params_init(out BackendParams parameters);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int transcribe_init_backends_ex(in BackendParams parameters);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern uint transcribe_allowed_backends();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int transcribe_device_count();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr transcribe_device_get(int index);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void transcribe_device_info_init(out DeviceInfo info);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int transcribe_device_get_info(IntPtr device, ref DeviceInfo info);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void transcribe_model_load_params_init(out LoadParams parameters);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int transcribe_model_load_file([MarshalAs(UnmanagedType.LPUTF8Str)] string path, in LoadParams parameters, out IntPtr model);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void transcribe_model_free(IntPtr model);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr transcribe_model_device(NativeModelHandle model);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr transcribe_model_arch_string(NativeModelHandle model);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern uint transcribe_model_roles(NativeModelHandle model);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void transcribe_capabilities_init(out Capabilities capabilities);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int transcribe_model_get_capabilities(NativeModelHandle model, ref Capabilities capabilities);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool transcribe_model_supports(NativeModelHandle model, int feature);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int transcribe_session_init(NativeModelHandle model, IntPtr parameters, out IntPtr session);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void transcribe_session_free(IntPtr session);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void transcribe_session_limits_init(out SessionLimits limits);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int transcribe_session_get_limits(NativeSessionHandle session, ref SessionLimits limits);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void transcribe_run_params_init(out RunParams parameters);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int transcribe_run(NativeSessionHandle session, float[] samples, int count, in RunParams parameters);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void transcribe_set_abort_callback(NativeSessionHandle session, AbortCallback? callback, IntPtr state);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool transcribe_was_aborted(NativeSessionHandle session);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool transcribe_was_truncated(NativeSessionHandle session);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr transcribe_full_text(NativeSessionHandle session);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr transcribe_detected_language(NativeSessionHandle session);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void transcribe_stream_params_init(out StreamParams parameters);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void transcribe_stream_update_init(out StreamUpdate update);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int transcribe_stream_begin(NativeSessionHandle session, in RunParams run, in StreamParams stream);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int transcribe_stream_feed(NativeSessionHandle session, float[] samples, int count, ref StreamUpdate update);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int transcribe_stream_finalize(NativeSessionHandle session, ref StreamUpdate update);

    internal static string Text(IntPtr pointer) => Marshal.PtrToStringUTF8(pointer) ?? "";
}

internal sealed class NativeModelHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal NativeModelHandle(IntPtr pointer) : base(true) => SetHandle(pointer);
    protected override bool ReleaseHandle() { TranscribeNativeApi.transcribe_model_free(handle); return true; }
}

internal sealed class NativeSessionHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private readonly NativeModelHandle _model;
    internal NativeSessionHandle(IntPtr pointer, NativeModelHandle model) : base(true)
    {
        var retained = false;
        model.DangerousAddRef(ref retained);
        _model = model;
        SetHandle(pointer);
    }
    protected override bool ReleaseHandle()
    {
        TranscribeNativeApi.transcribe_session_free(handle);
        _model.DangerousRelease();
        return true;
    }
}
