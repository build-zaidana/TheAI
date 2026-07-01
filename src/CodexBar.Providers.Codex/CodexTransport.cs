using System.Diagnostics;

namespace CodexBar.Providers.Codex;

internal interface ICodexTransport : IAsyncDisposable
{
    bool IsRunning { get; }
    Task StartAsync(CancellationToken cancellationToken);
    ValueTask WriteLineAsync(string line, CancellationToken cancellationToken);
    ValueTask<string?> ReadOutputLineAsync(CancellationToken cancellationToken);
    ValueTask<string?> ReadErrorLineAsync(CancellationToken cancellationToken);
    Task StopAsync();
}

internal sealed class ProcessCodexTransport(string executable) : ICodexTransport
{
    private Process? _process;
    private ProcessJob? _processJob;

    public bool IsRunning => _process is { HasExited: false };
    internal Process? Process => _process;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsRunning)
        {
            return Task.CompletedTask;
        }

        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "app-server --stdio",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        if (!_process.Start())
        {
            throw new InvalidOperationException("Codex App Server gagal dijalankan.");
        }

        _process.StandardInput.AutoFlush = true;
        _processJob = ProcessJob.Attach(_process);
        return Task.CompletedTask;
    }

    public ValueTask WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        if (!IsRunning || _process is null)
        {
            throw new InvalidOperationException("Codex App Server belum berjalan.");
        }

        return new ValueTask(_process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken));
    }

    public ValueTask<string?> ReadOutputLineAsync(CancellationToken cancellationToken) =>
        _process is null
            ? ValueTask.FromResult<string?>(null)
            : _process.StandardOutput.ReadLineAsync(cancellationToken);

    public ValueTask<string?> ReadErrorLineAsync(CancellationToken cancellationToken) =>
        _process is null
            ? ValueTask.FromResult<string?>(null)
            : _process.StandardError.ReadLineAsync(cancellationToken);

    public Task StopAsync()
    {
        if (_process is { } process)
        {
            try
            {
                process.StandardInput.Dispose();
            }
            catch
            {
            }

            try
            {
                if (!process.HasExited && !process.WaitForExit(1500))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(1500);
                }
            }
            catch
            {
            }
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _processJob?.Dispose();
        _processJob = null;
        _process?.Dispose();
        _process = null;
    }
}
