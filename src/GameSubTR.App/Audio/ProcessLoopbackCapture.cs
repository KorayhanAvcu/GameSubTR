using System.Runtime.InteropServices;
using GameSubTR.Core.Logging;

namespace GameSubTR.App.Audio;

/// <summary>
/// WASAPI "process loopback" (Windows 10 build 20348+ / Windows 11):
/// yalnızca belirli bir sürecin (oyunun) sesini yakalar; Discord, müzik vb. karışmaz.
/// <paramref name="excludeTarget"/> = true ise hedef süreç HARİÇ tüm sistem sesi yakalanır
/// (kendi PID'imizle "tüm sistem sesi" modu için kullanılır).
/// Çıktı: 44.1 kHz, 16 bit, stereo PCM.
/// </summary>
public sealed class ProcessLoopbackCapture : IDisposable
{
    public const int SampleRate = 44100;
    public const int Channels = 2;
    public const int BitsPerSample = 16;

    private readonly int _pid;
    private readonly bool _excludeTarget;
    private Thread? _thread;
    private volatile bool _stop;

    public event Action<byte[], int>? DataAvailable;
    public event Action<Exception>? Failed;

    public ProcessLoopbackCapture(int pid, bool excludeTarget)
    {
        _pid = pid; _excludeTarget = excludeTarget;
    }

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "AudioCapture" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    private void Run()
    {
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        IntPtr fmt = IntPtr.Zero;
        using var ev = new AutoResetEvent(false);
        try
        {
            client = ActivateAsync(_pid, _excludeTarget).GetAwaiter().GetResult();

            var wf = new WAVEFORMATEX
            {
                wFormatTag = 1, nChannels = Channels, nSamplesPerSec = SampleRate,
                wBitsPerSample = BitsPerSample, nBlockAlign = Channels * BitsPerSample / 8,
                nAvgBytesPerSec = SampleRate * Channels * BitsPerSample / 8, cbSize = 0
            };
            fmt = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEFORMATEX>());
            Marshal.StructureToPtr(wf, fmt, false);

            const uint LOOPBACK = 0x00020000, EVENTCALLBACK = 0x00040000, AUTOCONVERTPCM = 0x80000000, SRC_DEFAULT_QUALITY = 0x08000000;
            Check(client.Initialize(0, LOOPBACK | EVENTCALLBACK | AUTOCONVERTPCM | SRC_DEFAULT_QUALITY, 2_000_000, 0, fmt, IntPtr.Zero), "Initialize");
            Check(client.SetEventHandle(ev.SafeWaitHandle.DangerousGetHandle()), "SetEventHandle");
            var iid = typeof(IAudioCaptureClient).GUID;
            Check(client.GetService(ref iid, out var svc), "GetService");
            capture = (IAudioCaptureClient)svc;
            Check(client.Start(), "Start");
            Log.Info($"Ses yakalama başladı (pid {_pid}, {(_excludeTarget ? "tüm sistem" : "sadece oyun")}).");

            int frameBytes = Channels * BitsPerSample / 8;
            byte[] buffer = new byte[SampleRate * frameBytes];
            while (!_stop)
            {
                ev.WaitOne(100);
                while (!_stop && capture.GetNextPacketSize(out uint packet) == 0 && packet > 0)
                {
                    Check(capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _), "GetBuffer");
                    int bytes = (int)frames * frameBytes;
                    if (buffer.Length < bytes) buffer = new byte[bytes];
                    if ((flags & 0x2) != 0) Array.Clear(buffer, 0, bytes); // AUDCLNT_BUFFERFLAGS_SILENT
                    else Marshal.Copy(data, buffer, 0, bytes);
                    capture.ReleaseBuffer(frames);
                    DataAvailable?.Invoke(buffer, bytes);
                }
            }
            client.Stop();
        }
        catch (Exception ex)
        {
            Log.Error("Ses yakalama hatası", ex);
            Failed?.Invoke(ex);
        }
        finally
        {
            if (capture != null) Marshal.ReleaseComObject(capture);
            if (client != null) Marshal.ReleaseComObject(client);
            if (fmt != IntPtr.Zero) Marshal.FreeHGlobal(fmt);
        }
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new COMException($"{what} başarısız (0x{hr:X8})", hr);
    }

    private static async Task<IAudioClient> ActivateAsync(int pid, bool exclude)
    {
        // AUDIOCLIENT_ACTIVATION_PARAMS { ActivationType=PROCESS_LOOPBACK(1), TargetProcessId, ProcessLoopbackMode }
        IntPtr pars = Marshal.AllocHGlobal(12);
        IntPtr prop = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.WriteInt32(pars, 0, 1);
            Marshal.WriteInt32(pars, 4, pid);
            Marshal.WriteInt32(pars, 8, exclude ? 1 : 0);
            // PROPVARIANT { vt = VT_BLOB(65), blob = { cbSize = 12, pBlobData = pars } }
            for (int i = 0; i < 24; i += 4) Marshal.WriteInt32(prop, i, 0);
            Marshal.WriteInt16(prop, 0, 65);
            Marshal.WriteInt32(prop, 8, 12);
            Marshal.WriteIntPtr(prop, 16, pars);

            var handler = new CompletionHandler();
            ActivateAudioInterfaceAsync("VAD\\Process_Loopback", typeof(IAudioClient).GUID, prop, handler, out var op);
            var client = await handler.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            GC.KeepAlive(op);
            return client;
        }
        catch (EntryPointNotFoundException)
        {
            throw new PlatformNotSupportedException("Bu Windows sürümü süreç bazlı ses yakalamayı desteklemiyor (Windows 10 21H2+ / Windows 11 gerekir).");
        }
        finally
        {
            Marshal.FreeHGlobal(prop);
            Marshal.FreeHGlobal(pars);
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread?.Join(1500);
    }

    // ---------------- COM interop ----------------

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public int nSamplesPerSec;
        public int nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAgileObject { }

    [ComVisible(true)]
    internal sealed class CompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        private readonly TaskCompletionSource<IAudioClient> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IAudioClient> Task => _tcs.Task;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op)
        {
            try
            {
                op.GetActivateResult(out int hr, out object iface);
                if (hr < 0) _tcs.TrySetException(new COMException($"Ses arayüzü etkinleştirilemedi (0x{hr:X8})", hr));
                else _tcs.TrySetResult((IAudioClient)iface);
            }
            catch (Exception ex) { _tcs.TrySetException(ex); }
        }
    }
}
