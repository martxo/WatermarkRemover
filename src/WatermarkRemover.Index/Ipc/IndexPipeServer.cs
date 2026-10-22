using System.IO.Pipes;
using System.Text;
using WatermarkRemover.Index.Contracts;
using WatermarkRemover.Index.Indexing;

namespace WatermarkRemover.Index.Ipc;

/// <summary>
/// Named-pipe server, the counterpart of Everything's "\\.\PIPE\Everything Service".
/// The host runs elevated; its pipe is opened up to the same user at medium integrity (see
/// <see cref="PrivilegedPipe"/>), so the un-elevated app can talk to it.
/// </summary>
public sealed class IndexPipeServer
{
    public const string PipeName = "WatermarkRemover.Index.v1";
    private const int MaxInstances = 4;

    private readonly IndexHostService _service;
    private readonly Action? _onShutdown;

    public IndexPipeServer(IndexHostService service, Action? onShutdown = null)
    {
        _service = service;
        _onShutdown = onShutdown;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                // Explicit descriptor: this user only, with a medium integrity label so the
                // un-elevated app may write to the high-integrity host's pipe.
                server = PrivilegedPipe.CreateServerStream(PipeName, MaxInstances);

                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                NamedPipeServerStream connection = server;
                server = null;
                _ = HandleAsync(connection, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                server?.Dispose();
                break;
            }
            catch (IOException)
            {
                server?.Dispose();
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleAsync(NamedPipeServerStream connection, CancellationToken cancellationToken)
    {
        await using (connection.ConfigureAwait(false))
        {
            try
            {
                using var reader = new StreamReader(
                    connection, new UTF8Encoding(false), false, 1024, leaveOpen: true);
                using var writer = new StreamWriter(
                    connection, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };

                string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    await writer.WriteLineAsync(IndexJson.Serialize(IndexResponse.Failure("空请求")))
                        .ConfigureAwait(false);
                    return;
                }

                IndexRequest? request = IndexJson.Deserialize<IndexRequest>(line);
                if (request is null)
                {
                    await writer.WriteLineAsync(IndexJson.Serialize(IndexResponse.Failure("请求格式错误")))
                        .ConfigureAwait(false);
                    return;
                }

                IndexResponse response = _service.Handle(request);
                await writer.WriteLineAsync(IndexJson.Serialize(response)).ConfigureAwait(false);

                if (request.Cmd == "shutdown") _onShutdown?.Invoke();
            }
            catch (Exception ex)
            {
                try
                {
                    using var writer = new StreamWriter(
                        connection, new UTF8Encoding(false), 1024, leaveOpen: true);
                    await writer.WriteLineAsync(IndexJson.Serialize(IndexResponse.Failure(ex.Message)))
                        .ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // The client vanished mid-response; nothing left to report to.
                }
            }
        }
    }
}
