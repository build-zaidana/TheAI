using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.InteropServices;
using CodexBar.Core;

namespace CodexBar.App.Voice;

/// <summary>
/// Offline speech-to-text with whisper.cpp. The engine (whisper-server + its DLLs, ~10 MB) and a
/// model are downloaded once on the user's say-so into %LOCALAPPDATA%\CodexBar\whisper, from
/// pinned URLs and checked against pinned SHA-256 hashes. The server then runs hidden on a
/// loopback port with the model kept in memory, so a push-to-talk clip is only encoded and
/// decoded: about 0.6 s for 10 s of speech on an 8-core laptop CPU with these settings
/// (fixed language, greedy decoding, 15 s audio context). Longer clips are cut at quiet points.
/// </summary>
internal sealed class WhisperEngine : IDisposable
{
    private const string EngineUrl = "https://github.com/ggml-org/whisper.cpp/releases/download/b5130/whisper-bin-x64.zip";
    private const string EngineSha256 = "f9ec6c52a2e949b62ab51fa21d0d497958f9e41c3010c157c4e42932d5316f3c";
    private const int ChunkSeconds = 15;
    private const int AudioContext = 768; // 15.36 s of audio; whisper's full window is 1500 (30 s)

    private static readonly Dictionary<string, (string Url, string Sha256, long Bytes)> Models = new()
    {
        ["base"] = ("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base-q5_1.bin",
            "422f1ae452ade6f30a004d7e5c6a43195e4433bc370bf23fac9cc591f01a8898", 59_707_625),
        ["small"] = ("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small-q5_1.bin",
            "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb", 190_085_487),
    };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    private readonly SemaphoreSlim _serverGate = new(1, 1);
    private Process? _server;
    private int _port;
    private string? _serverModel;
    private string? _serverLanguage;

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexBar", "whisper");

    private static string EngineFolder => Path.Combine(Folder, "bin");

    private static string ServerExe => Path.Combine(EngineFolder, "whisper-server.exe");

    private static string ModelPath(string model) => Path.Combine(Folder, $"ggml-{model}-q5_1.bin");

    public static bool IsInstalled(string model) => File.Exists(ServerExe) && File.Exists(ModelPath(Normalize(model)));

    /// <summary>Megabytes still to download for this model (engine included when missing).</summary>
    public static int DownloadSizeMb(string model)
    {
        long bytes = File.Exists(ModelPath(Normalize(model))) ? 0 : Models[Normalize(model)].Bytes;
        if (!File.Exists(ServerExe))
        {
            bytes += 8_573_270;
        }

        return (int)Math.Ceiling(bytes / 1048576.0);
    }

    /// <summary>Download (and verify) whatever is missing; progress runs 0–1.</summary>
    public async Task InstallAsync(string model, IProgress<double> progress, CancellationToken cancellationToken)
    {
        model = Normalize(model);
        Directory.CreateDirectory(Folder);
        var needEngine = !File.Exists(ServerExe);
        var needModel = !File.Exists(ModelPath(model));
        double total = (needEngine ? 8_573_270 : 0) + (needModel ? Models[model].Bytes : 0);
        double done = 0;
        void Report(long bytes) => progress.Report(total <= 0 ? 1 : Math.Clamp((done + bytes) / total, 0, 1));

        if (needEngine)
        {
            var zip = Path.Combine(Folder, "engine.zip.part");
            await DownloadAsync(EngineUrl, zip, EngineSha256, Report, cancellationToken);
            done += new FileInfo(zip).Length;
            if (Directory.Exists(EngineFolder))
            {
                Directory.Delete(EngineFolder, recursive: true);
            }

            Directory.CreateDirectory(EngineFolder);
            using (var archive = ZipFile.OpenRead(zip))
            {
                // Only the server and the libraries it loads; the zip also carries demos and tests.
                foreach (var entry in archive.Entries)
                {
                    var name = entry.Name;
                    if (name.Equals("whisper-server.exe", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("whisper.dll", StringComparison.OrdinalIgnoreCase) ||
                        (name.StartsWith("ggml", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                    {
                        entry.ExtractToFile(Path.Combine(EngineFolder, name), overwrite: true);
                    }
                }
            }

            File.Delete(zip);
        }

        if (needModel)
        {
            var part = ModelPath(model) + ".part";
            await DownloadAsync(Models[model].Url, part, Models[model].Sha256, Report, cancellationToken);
            File.Move(part, ModelPath(model), overwrite: true);
        }

        progress.Report(1);
    }

    /// <summary>Remove the engine and all models.</summary>
    public void Uninstall()
    {
        StopServer();
        if (Directory.Exists(Folder))
        {
            Directory.Delete(Folder, recursive: true);
        }
    }

    /// <summary>Start the server ahead of time so the first clip doesn't wait for the model to load.</summary>
    public Task WarmUpAsync(string model, string language) => EnsureServerAsync(Normalize(model), language, CancellationToken.None);

    /// <summary>Transcribe raw 16 kHz mono PCM.</summary>
    public async Task<string> TranscribeAsync(byte[] pcm, string model, string language, CancellationToken cancellationToken)
    {
        model = Normalize(model);
        if (pcm.Length < WaveRecorder.SampleRate * 2 / 4)
        {
            return string.Empty; // under 0.25 s: a click, not speech
        }

        await EnsureServerAsync(model, language, cancellationToken);
        var parts = new List<string>();
        foreach (var chunk in SplitAtPauses(pcm, ChunkSeconds))
        {
            using var form = new MultipartFormDataContent
            {
                { new ByteArrayContent(Wav(chunk)), "file", "clip.wav" },
                { new StringContent("json"), "response_format" },
                { new StringContent(language), "language" },
                { new StringContent("0"), "temperature" }
            };
            using var response = await Http.PostAsync($"http://127.0.0.1:{_port}/inference", form, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (json.RootElement.TryGetProperty("text", out var text) && text.GetString() is { } value)
            {
                parts.Add(value.Trim());
            }
        }

        // whisper marks a cut it didn't finish with "..." on both sides of the chunk boundary.
        for (var i = 0; i < parts.Count; i++)
        {
            if (i < parts.Count - 1 && parts[i].EndsWith("...", StringComparison.Ordinal))
            {
                parts[i] = parts[i][..^3];
            }

            if (i > 0 && parts[i].StartsWith("...", StringComparison.Ordinal))
            {
                parts[i] = parts[i][3..];
            }
        }

        return Clean(string.Join(" ", parts.Where(p => p.Length > 0)));
    }

    /// <summary>Stop the server (frees the model's memory); the next clip starts it again.</summary>
    public void Shutdown() => StopServer();

    public void Dispose()
    {
        StopServer();
        _serverGate.Dispose();
    }

    // ---- Server ------------------------------------------------------------------------------

    private async Task EnsureServerAsync(string model, string language, CancellationToken cancellationToken)
    {
        await _serverGate.WaitAsync(cancellationToken);
        try
        {
            if (_server is { HasExited: false } && _serverModel == model && _serverLanguage == language)
            {
                return;
            }

            StopServer();
            if (!IsInstalled(model))
            {
                throw new InvalidOperationException(Tr.Text("Model suara belum diunduh.", "The voice model isn't downloaded yet."));
            }

            _port = FreePort();
            var threads = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
            var startInfo = new ProcessStartInfo(ServerExe)
            {
                Arguments = $"-m \"{ModelPath(model)}\" --host 127.0.0.1 --port {_port} -t {threads} -bo 1 -nt -ac {AudioContext} -l {language}",
                WorkingDirectory = EngineFolder,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            var server = Process.Start(startInfo) ?? throw new InvalidOperationException("whisper-server failed to start.");
            ChildProcessJob.Attach(server);
            server.OutputDataReceived += (_, _) => { };
            server.ErrorDataReceived += (_, _) => { };
            server.BeginOutputReadLine();
            server.BeginErrorReadLine();
            _server = server;
            _serverModel = model;
            _serverLanguage = language;

            // Ready once it answers on its port (the model loads in well under a second).
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                if (server.HasExited)
                {
                    throw new InvalidOperationException($"whisper-server exited ({server.ExitCode}).");
                }

                try
                {
                    using var probe = await Http.GetAsync($"http://127.0.0.1:{_port}/", cancellationToken);
                    DiagnosticsLog.Info("Voice", $"whisper-server ready model={model} lang={language} port={_port}");
                    return;
                }
                catch (HttpRequestException)
                {
                    await Task.Delay(150, cancellationToken);
                }
            }

            throw new TimeoutException("whisper-server did not start.");
        }
        finally
        {
            _serverGate.Release();
        }
    }

    private void StopServer()
    {
        try
        {
            if (_server is { HasExited: false } server)
            {
                server.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception)
        {
            DiagnosticsLog.Error("Voice", exception.Message);
        }

        _server?.Dispose();
        _server = null;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // ---- Helpers -----------------------------------------------------------------------------

    private static string Normalize(string model) => model == "small" ? "small" : "base";

    private static async Task DownloadAsync(string url, string target, string sha256, Action<long> report, CancellationToken cancellationToken)
    {
        using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var file = File.Create(target);
            var buffer = new byte[1 << 16];
            long received = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                received += read;
                report(received);
            }
        }

        await using (var check = File.OpenRead(target))
        {
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(check, cancellationToken));
            if (!hash.Equals(sha256, StringComparison.OrdinalIgnoreCase))
            {
                check.Close();
                File.Delete(target);
                throw new InvalidDataException(Tr.Text("Unduhan rusak (hash tidak cocok). Coba lagi.", "The download is corrupt (hash mismatch). Try again."));
            }
        }
    }

    /// <summary>Split PCM into pieces of at most <paramref name="maxSeconds"/>, cutting at the quietest moment near each limit.</summary>
    internal static IReadOnlyList<byte[]> SplitAtPauses(byte[] pcm, int maxSeconds)
    {
        const int bytesPerSecond = WaveRecorder.SampleRate * 2;
        const int frame = bytesPerSecond / 50; // 20 ms
        var max = maxSeconds * bytesPerSecond;
        var chunks = new List<byte[]>();
        var start = 0;
        while (pcm.Length - start > max)
        {
            // Look for the quietest 20 ms frame in the last 3 seconds before the limit.
            var searchFrom = start + max - 3 * bytesPerSecond;
            var best = start + max;
            var bestEnergy = double.MaxValue;
            for (var offset = searchFrom; offset + frame <= start + max; offset += frame)
            {
                double energy = 0;
                for (var i = offset; i < offset + frame; i += 2)
                {
                    var sample = BitConverter.ToInt16(pcm, i);
                    energy += sample * (double)sample;
                }

                if (energy < bestEnergy)
                {
                    bestEnergy = energy;
                    best = offset + frame / 2;
                }
            }

            best -= best % 2;
            chunks.Add(pcm[start..best]);
            start = best;
        }

        chunks.Add(pcm[start..]);
        return chunks;
    }

    internal static byte[] Wav(byte[] pcm)
    {
        using var stream = new MemoryStream(44 + pcm.Length);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + pcm.Length);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(WaveRecorder.SampleRate);
        writer.Write(WaveRecorder.SampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(pcm.Length);
        writer.Write(pcm);
        writer.Flush();
        return stream.ToArray();
    }

    // whisper marks silence and noise with bracketed tags ("[BLANK_AUDIO]", "(music)").
    private static string Clean(string text)
    {
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\[[^\]]*\]|\([^)]*\)", " ");
        return System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
    }
}

/// <summary>
/// A job object that kills its processes when CodexBar exits for any reason (including a crash
/// or being killed), so no whisper-server is left holding a model in memory.
/// </summary>
internal static class ChildProcessJob
{
    private static readonly Lazy<IntPtr> Job = new(Create);

    public static void Attach(Process process)
    {
        try
        {
            if (Job.Value != IntPtr.Zero)
            {
                AssignProcessToJobObject(Job.Value, process.Handle);
            }
        }
        catch (Exception exception)
        {
            DiagnosticsLog.Error("Voice", exception.Message);
        }
    }

    private static IntPtr Create()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var info = new ExtendedLimitInformation { BasicLimitInformation = new BasicLimitInformation { LimitFlags = 0x2000 } }; // KILL_ON_JOB_CLOSE
        var size = Marshal.SizeOf<ExtendedLimitInformation>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            SetInformationJobObject(job, 9 /* JobObjectExtendedLimitInformation */, buffer, (uint)size);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return job; // deliberately never closed: the handle dies with the process, taking the children
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}
