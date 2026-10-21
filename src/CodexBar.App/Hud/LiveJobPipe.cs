using System.IO;
using System.IO.Pipes;
using System.Text;
using CodexBar.Core;

namespace CodexBar.App.Hud;

/// <summary>
/// The live activity API: a named pipe any script of the same Windows user can write JSON lines
/// to (<c>\\.\pipe\theai-&lt;user&gt;</c>), and the <c>TheAI.exe activity …</c> command that
/// does it for you. No port, no network: the pipe only accepts the user's own processes.
/// </summary>
internal sealed class LiveJobPipe : IDisposable
{
    public static string Name => "theai-" + Environment.UserName.ToLowerInvariant();

    private const int MaxLine = 4096;
    private readonly CancellationTokenSource _stop = new();

    /// <summary>An update arrived (raised on a worker thread).</summary>
    public event Action<LiveJobUpdate>? Received;

    public void Start() => _ = Task.Run(() => AcceptLoopAsync(_stop.Token));

    public void Dispose() => _stop.Cancel();

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = new NamedPipeServerStream(Name, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                // Another TheAI of this user holds the name, or the pipe can't be made: try again later.
                DiagnosticsLog.Error("LiveJobs", exception.Message);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            _ = Task.Run(() => ReadAsync(server, token), token);
        }
    }

    private async Task ReadAsync(NamedPipeServerStream server, CancellationToken token)
    {
        await using var _ = server;
        try
        {
            using var reader = new StreamReader(server, Encoding.UTF8);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(30));   // a script may keep the pipe open and stream updates
            while (await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false) is { } line)
            {
                if (line.Length is > 0 and <= MaxLine && LiveJobUpdate.Parse(line) is { } update)
                {
                    Received?.Invoke(update);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
        }
    }

    /// <summary>Client side: send one update to the running TheAI. Returns an error message or null.</summary>
    public static string? Send(LiveJobUpdate update)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", Name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(1500);
            var bytes = Encoding.UTF8.GetBytes(update.ToJson() + "\n");
            client.Write(bytes);
            client.Flush();
            return null;
        }
        catch (TimeoutException)
        {
            return "TheAI is not running.";
        }
        catch (Exception exception)
        {
            return exception.Message;
        }
    }
}
