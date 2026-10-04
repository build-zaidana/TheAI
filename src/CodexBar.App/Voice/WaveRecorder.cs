using System.Runtime.InteropServices;

namespace CodexBar.App.Voice;

/// <summary>
/// Microphone capture through the classic waveIn API: 16 kHz, mono, 16-bit PCM, which is exactly
/// what whisper wants, so nothing needs resampling. Starts in a few milliseconds (no device
/// negotiation like MediaCapture), which matters for push-to-talk. Buffers of 50 ms are polled on
/// a background thread; <see cref="Level"/> reports the loudness of each one for the meter.
/// </summary>
internal sealed class WaveRecorder : IDisposable
{
    public const int SampleRate = 16000;
    private const int BufferMs = 50;
    private const int BufferCount = 8;
    private const int BufferBytes = SampleRate * 2 * BufferMs / 1000;

    private readonly List<byte> _pcm = new();
    private readonly object _gate = new();
    private IntPtr _device;
    private IntPtr[] _headers = [];
    private IntPtr[] _data = [];
    private Thread? _pump;
    private int _next;
    private volatile bool _running;

    /// <summary>Loudness 0–1 of the latest buffer (raised on the capture thread).</summary>
    public event Action<double>? Level;

    public bool IsRecording => _running;

    public void Start()
    {
        if (_running)
        {
            return;
        }

        var format = new WaveFormat
        {
            wFormatTag = 1,
            nChannels = 1,
            nSamplesPerSec = SampleRate,
            wBitsPerSample = 16,
            nBlockAlign = 2,
            nAvgBytesPerSec = SampleRate * 2
        };
        Check(waveInOpen(out _device, WaveMapper, ref format, IntPtr.Zero, IntPtr.Zero, 0), "waveInOpen");

        lock (_gate)
        {
            _pcm.Clear();
        }

        _headers = new IntPtr[BufferCount];
        _data = new IntPtr[BufferCount];
        var headerSize = Marshal.SizeOf<WaveHeader>();
        for (var i = 0; i < BufferCount; i++)
        {
            _data[i] = Marshal.AllocHGlobal(BufferBytes);
            _headers[i] = Marshal.AllocHGlobal(headerSize);
            Marshal.StructureToPtr(new WaveHeader { lpData = _data[i], dwBufferLength = BufferBytes }, _headers[i], false);
            Check(waveInPrepareHeader(_device, _headers[i], headerSize), "waveInPrepareHeader");
            Check(waveInAddBuffer(_device, _headers[i], headerSize), "waveInAddBuffer");
        }

        _next = 0;
        _running = true;
        Check(waveInStart(_device), "waveInStart");
        _pump = new Thread(Pump) { IsBackground = true, Name = "CodexBar mic" };
        _pump.Start();
    }

    /// <summary>Stop and return everything recorded as raw 16 kHz mono PCM.</summary>
    public byte[] Stop()
    {
        if (!_running)
        {
            return [];
        }

        _running = false;
        _pump?.Join(500);
        waveInStop(_device);
        Drain(requeue: false);
        waveInReset(_device);
        var headerSize = Marshal.SizeOf<WaveHeader>();
        for (var i = 0; i < _headers.Length; i++)
        {
            waveInUnprepareHeader(_device, _headers[i], headerSize);
            Marshal.FreeHGlobal(_headers[i]);
            Marshal.FreeHGlobal(_data[i]);
        }

        waveInClose(_device);
        _device = IntPtr.Zero;
        _headers = [];
        _data = [];
        lock (_gate)
        {
            return _pcm.ToArray();
        }
    }

    public void Dispose()
    {
        if (_running)
        {
            Stop();
        }
    }

    private void Pump()
    {
        while (_running)
        {
            Drain(requeue: true);
            Thread.Sleep(15);
        }
    }

    // The driver fills buffers in the order they were queued, and each one is re-queued as soon
    // as it is read, so walking the ring from the next expected buffer keeps the audio in order.
    private void Drain(bool requeue)
    {
        var headerSize = Marshal.SizeOf<WaveHeader>();
        for (var n = 0; n < _headers.Length; n++)
        {
            var headerPtr = _headers[_next];
            var header = Marshal.PtrToStructure<WaveHeader>(headerPtr);
            if ((header.dwFlags & WhdrDone) == 0)
            {
                return;
            }

            var length = header.dwBytesRecorded;
            if (length > 0)
            {
                var chunk = new byte[length];
                Marshal.Copy(header.lpData, chunk, 0, length);
                lock (_gate)
                {
                    _pcm.AddRange(chunk);
                }

                Level?.Invoke(Rms(chunk));
            }

            if (requeue && _running)
            {
                header.dwFlags &= ~WhdrDone;
                header.dwBytesRecorded = 0;
                Marshal.StructureToPtr(header, headerPtr, false);
                waveInAddBuffer(_device, headerPtr, headerSize);
            }

            _next = (_next + 1) % _headers.Length;
        }
    }

    private static double Rms(byte[] chunk)
    {
        double sum = 0;
        var samples = chunk.Length / 2;
        for (var i = 0; i < samples; i++)
        {
            var sample = BitConverter.ToInt16(chunk, i * 2) / 32768.0;
            sum += sample * sample;
        }

        // Speech sits around 0.02–0.2 RMS; stretch that range over the meter.
        return samples == 0 ? 0 : Math.Clamp(Math.Sqrt(sum / samples) * 6, 0, 1);
    }

    private static void Check(int result, string call)
    {
        if (result != 0)
        {
            throw new InvalidOperationException(result == 2 /* MMSYSERR_BADDEVICEID */ || result == 6 /* MMSYSERR_NODRIVER */
                ? Tr.Text("Mikrofon tidak ditemukan.", "No microphone found.")
                : $"{call} failed ({result}).");
        }
    }

    private const int WaveMapper = -1;
    private const int WhdrDone = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormat
    {
        public short wFormatTag;
        public short nChannels;
        public int nSamplesPerSec;
        public int nAvgBytesPerSec;
        public short nBlockAlign;
        public short wBitsPerSample;
        public short cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr lpData;
        public int dwBufferLength;
        public int dwBytesRecorded;
        public IntPtr dwUser;
        public int dwFlags;
        public int dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [DllImport("winmm.dll")] private static extern int waveInOpen(out IntPtr device, int deviceId, ref WaveFormat format, IntPtr callback, IntPtr instance, int flags);
    [DllImport("winmm.dll")] private static extern int waveInPrepareHeader(IntPtr device, IntPtr header, int size);
    [DllImport("winmm.dll")] private static extern int waveInUnprepareHeader(IntPtr device, IntPtr header, int size);
    [DllImport("winmm.dll")] private static extern int waveInAddBuffer(IntPtr device, IntPtr header, int size);
    [DllImport("winmm.dll")] private static extern int waveInStart(IntPtr device);
    [DllImport("winmm.dll")] private static extern int waveInStop(IntPtr device);
    [DllImport("winmm.dll")] private static extern int waveInReset(IntPtr device);
    [DllImport("winmm.dll")] private static extern int waveInClose(IntPtr device);
}
