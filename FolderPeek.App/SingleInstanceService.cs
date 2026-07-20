using System.IO.Pipes;
using System.IO;
using System.Text;

namespace FolderPeek.App;

public sealed class SingleInstanceService : IDisposable
{
    private const string MutexName = "Local\\FolderPeek.App.SingleInstance";
    private const string PipeName = "FolderPeek.App.OpenFolder";

    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private Mutex? _mutex;
    private Task? _serverTask;
    private bool _disposed;

    public event EventHandler<string>? OpenFolderRequested;

    public bool TryAcquirePrimary()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (createdNew)
        {
            return true;
        }

        _mutex.Dispose();
        _mutex = null;
        return false;
    }

    public void StartServer()
    {
        if (_serverTask is null)
        {
            _serverTask = Task.Run(ListenAsync);
        }
    }

    public bool TryForwardOpenFolder(string[] args)
    {
        var folderPath = TryParseOpenFolderArgument(args);
        if (folderPath is null)
        {
            return true;
        }

        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeout: 2000);
            using var writer = new StreamWriter(client, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 1024, leaveOpen: false);
            writer.Write(folderPath);
            writer.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string? TryParseOpenFolderArgument(string[] args)
    {
        return args.Length == 2 &&
               string.Equals(args[0], "--open-folder", StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(args[1])
            ? args[1]
            : null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _cancellationTokenSource.Cancel();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        _cancellationTokenSource.Dispose();
        _disposed = true;
    }

    private async Task ListenAsync()
    {
        while (!_cancellationTokenSource.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(_cancellationTokenSource.Token);
                using var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
                var folderPath = await reader.ReadToEndAsync(_cancellationTokenSource.Token);
                if (!string.IsNullOrWhiteSpace(folderPath))
                {
                    OpenFolderRequested?.Invoke(this, folderPath);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                await Task.Delay(100, _cancellationTokenSource.Token);
            }
        }
    }
}
