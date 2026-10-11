using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenPin.Core;
using OpenPin.Recording.Codec;

namespace OpenPin.Recording.Capture;

/// <summary>
/// Shared-mode WASAPI capture through plain COM interop (no third-party audio library): a microphone, or the default
/// render device in loopback mode ("system audio"). A polling thread drains the capture buffer every ~10 ms, converts the
/// device mix format to stereo 48 kHz float (<see cref="AudioConverter"/>) and raises <see cref="Data"/> with the
/// Stopwatch timestamp of the packet's first sample (from the device's QPC position).
/// </summary>
internal sealed class WasapiCapture : IDisposable
{
    // ------------------------------------------------------------------ COM interop
    private const int CLSCTX_ALL = 0x17;
    private const int AUDCLNT_SHAREMODE_SHARED = 0;
    private const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    private const int AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
    private const int AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR = 0x4;
    private const ushort WAVE_FORMAT_PCM = 1, WAVE_FORMAT_IEEE_FLOAT = 3, WAVE_FORMAT_EXTENSIBLE = 0xFFFE;
    private static readonly Guid SubtypeFloat = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    private enum EDataFlow { Render = 0, Capture = 1 }
    private enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        int GetBufferSize(out uint frames);
        int GetStreamLatency(out long latency);
        int GetCurrentPadding(out uint padding);
        int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        int GetMixFormat(out IntPtr format);
        int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        int Start();
        int Stop();
        int Reset();
        int SetEventHandle(IntPtr handle);
        int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        int GetBuffer(out IntPtr data, out int frames, out int flags, out long devicePosition, out long qpcPosition);
        int ReleaseBuffer(int frames);
        int GetNextPacketSize(out int frames);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag, nChannels;
        public uint nSamplesPerSec, nAvgBytesPerSec;
        public ushort nBlockAlign, wBitsPerSample, cbSize;
    }

    // ------------------------------------------------------------------ public API

    private readonly bool _loopback;
    private readonly string? _deviceId;
    private Thread? _thread;
    private volatile bool _stop;
    private readonly ManualResetEventSlim _started = new();
    private Exception? _startError;

    /// <summary>Converted samples (stereo 48 kHz float) + Stopwatch timestamp of the first sample. Raised on the capture thread.</summary>
    public event Action<ReadOnlyMemory<float>, long>? Data;

    /// <summary>Raised once if the device fails while running (unplugged…).</summary>
    public event Action<Exception>? Failed;

    public WasapiCapture(bool loopback, string? deviceId = null)
    {
        _loopback = loopback;
        _deviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;
    }

    public string Description { get; private set; } = "";

    /// <summary>Open the device and start capturing. Throws if the device cannot be opened.</summary>
    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = _loopback ? "OpenPin loopback audio" : "OpenPin microphone", Priority = ThreadPriority.Highest };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        _started.Wait();
        if (_startError != null)
        {
            _thread.Join();
            throw new InvalidOperationException((_loopback ? "System audio" : "Microphone") + " capture failed: " + _startError.Message, _startError);
        }
    }

    /// <summary>True if a default device of the kind exists (microphone or speakers for loopback).</summary>
    public static bool HasDefaultDevice(bool loopback)
    {
        try
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            try { return en.GetDefaultAudioEndpoint(loopback ? EDataFlow.Render : EDataFlow.Capture, ERole.Console, out var dev) == 0 && dev != null; }
            finally { Marshal.ReleaseComObject(en); }
        }
        catch { return false; }
    }

    private void Run()
    {
        IMMDeviceEnumerator? en = null;
        IMMDevice? device = null;
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        IntPtr mixFormat = IntPtr.Zero;
        try
        {
            en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            int hr = _deviceId != null && !_loopback
                ? en.GetDevice(_deviceId, out device)
                : en.GetDefaultAudioEndpoint(_loopback ? EDataFlow.Render : EDataFlow.Capture, ERole.Console, out device);
            Marshal.ThrowExceptionForHR(hr);
            var iid = IID_IAudioClient;
            Marshal.ThrowExceptionForHR(device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var clientObj));
            client = (IAudioClient)clientObj;
            Marshal.ThrowExceptionForHR(client.GetMixFormat(out mixFormat));
            var wf = Marshal.PtrToStructure<WAVEFORMATEX>(mixFormat);
            bool isFloat = wf.wFormatTag == WAVE_FORMAT_IEEE_FLOAT ||
                           (wf.wFormatTag == WAVE_FORMAT_EXTENSIBLE && wf.cbSize >= 22 &&
                            Marshal.PtrToStructure<Guid>(mixFormat + 24) == SubtypeFloat);
            int channels = wf.nChannels, bits = wf.wBitsPerSample, blockAlign = wf.nBlockAlign;
            Description = $"{wf.nSamplesPerSec} Hz, {channels} ch, {bits} bit{(isFloat ? " float" : "")}";

            Marshal.ThrowExceptionForHR(client.Initialize(AUDCLNT_SHAREMODE_SHARED, _loopback ? AUDCLNT_STREAMFLAGS_LOOPBACK : 0,
                2_000_000 /* 200 ms */, 0, mixFormat, IntPtr.Zero));
            var cid = IID_IAudioCaptureClient;
            Marshal.ThrowExceptionForHR(client.GetService(ref cid, out var capObj));
            capture = (IAudioCaptureClient)capObj;
            var converter = new AudioConverter((int)wf.nSamplesPerSec, channels);
            Marshal.ThrowExceptionForHR(client.Start());
            _started.Set();

            var floats = new float[4096];
            while (!_stop)
            {
                Thread.Sleep(10);
                while (!_stop)
                {
                    Marshal.ThrowExceptionForHR(capture.GetNextPacketSize(out int packet));
                    if (packet == 0) break;
                    Marshal.ThrowExceptionForHR(capture.GetBuffer(out var data, out int frames, out int flags, out _, out long qpc));
                    try
                    {
                        int samples = frames * channels;
                        if (floats.Length < samples) floats = new float[samples];
                        var span = floats.AsSpan(0, samples);
                        if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0 || data == IntPtr.Zero) span.Clear();
                        else ConvertSamples(data, span, isFloat, bits, blockAlign, channels, frames);

                        long ts = (flags & AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR) == 0 && qpc > 0
                            ? (long)(qpc / 1e7 * Stopwatch.Frequency) // qpc is in 100 ns units
                            : Stopwatch.GetTimestamp() - (long)(frames / (double)wf.nSamplesPerSec * Stopwatch.Frequency);
                        var converted = converter.Process(span);
                        Data?.Invoke(converted.ToArray(), ts);
                    }
                    finally
                    {
                        capture.ReleaseBuffer(frames);
                    }
                }
            }
            client.Stop();
        }
        catch (Exception ex)
        {
            if (!_started.IsSet) _startError = ex;
            else
            {
                Log.Error($"{(_loopback ? "Loopback" : "Microphone")} capture failed", ex);
                try { Failed?.Invoke(ex); } catch { /* ignore */ }
            }
        }
        finally
        {
            _started.Set();
            if (mixFormat != IntPtr.Zero) Marshal.FreeCoTaskMem(mixFormat);
            if (capture != null) Marshal.ReleaseComObject(capture);
            if (client != null) Marshal.ReleaseComObject(client);
            if (device != null) Marshal.ReleaseComObject(device);
            if (en != null) Marshal.ReleaseComObject(en);
        }
    }

    private static unsafe void ConvertSamples(IntPtr data, Span<float> dst, bool isFloat, int bits, int blockAlign, int channels, int frames)
    {
        byte* p = (byte*)data;
        int bytesPerSample = blockAlign / Math.Max(1, channels);
        for (int f = 0; f < frames; f++)
        {
            byte* frame = p + f * blockAlign;
            for (int c = 0; c < channels; c++)
            {
                byte* s = frame + c * bytesPerSample;
                float v;
                if (isFloat && bits == 32) v = *(float*)s;
                else if (bits == 16) v = *(short*)s / 32768f;
                else if (bits == 24) v = ((s[0] << 8) | (s[1] << 16) | (s[2] << 24)) / 2147483648f;
                else if (bits == 32) v = *(int*)s / 2147483648f;
                else v = 0;
                dst[f * channels + c] = v;
            }
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread?.Join(2000);
        _started.Dispose();
    }
}
