using System.IO;
using System.IO.Pipes;
using System.Text;

namespace OpenPin.Core;

/// <summary>
/// Ensures one OpenPin per user session. A second launch forwards its command line to the running instance
/// through a named pipe and exits — so <c>OpenPin.exe --action capture</c> can be bound to anything (scripts, mouse software…).
/// </summary>
public sealed class SingleInstance : IDisposable
{
    // OPENPIN_INSTANCE lets developers run several isolated builds side by side (see CONTRIBUTING.md).
    private static readonly string Id =
        $"OpenPin-{Environment.UserName}-{System.Diagnostics.Process.GetCurrentProcess().SessionId}" +
        (Environment.GetEnvironmentVariable("OPENPIN_INSTANCE") is { Length: > 0 } suffix ? "-" + suffix : "");
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cts = new();

    public bool IsFirstInstance { get; }

    public SingleInstance()
    {
        _mutex = new Mutex(initiallyOwned: true, @"Local\" + Id, out bool createdNew);
        IsFirstInstance = createdNew;
    }

    /// <summary>Forward args to the running instance. Returns false if it could not be reached.</summary>
    public static bool SendToFirstInstance(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", Id, PipeDirection.Out);
            client.Connect(2000);
            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            writer.Write(string.Join("\n", args));
            writer.Flush();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not forward args to running instance: {ex.Message}");
            return false;
        }
    }

    /// <summary>Listen for forwarded command lines; <paramref name="onArgs"/> is invoked on a background thread.</summary>
    public void StartListening(Action<string[]> onArgs)
    {
        var token = _cts.Token;
        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(Id, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(token);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var text = await reader.ReadToEndAsync(token);
                    onArgs(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Log.Warn($"Single-instance pipe error: {ex.Message}");
                    await Task.Delay(500, CancellationToken.None);
                }
            }
        }, token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { if (IsFirstInstance) _mutex.ReleaseMutex(); } catch { /* ignore */ }
        _mutex.Dispose();
    }
}
