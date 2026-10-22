using System.IO.Pipes;
using System.Text;
using WatermarkRemover.Index.Contracts;

namespace WatermarkRemover.Index.Ipc;

/// <summary>App-side pipe access. Implemented as an interface so view-model tests can fake it.</summary>
public interface IIndexClient
{
    Task<IndexResponse?> SendAsync(
        IndexRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public sealed class IndexPipeClient : IIndexClient
{
    private readonly string _pipeName;

    public IndexPipeClient(string? pipeName = null)
    {
        _pipeName = pipeName ?? IndexPipeServer.PipeName;
    }

    public async Task<IndexResponse?> SendAsync(
        IndexRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

            await pipe.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);

            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true)
            {
                AutoFlush = true,
            };

            await writer.WriteLineAsync(IndexJson.Serialize(request)).ConfigureAwait(false);
            string? line = await reader.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false);
            return line is null ? null : IndexJson.Deserialize<IndexResponse>(line);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or TimeoutException)
        {
            return null;
        }
    }
}
