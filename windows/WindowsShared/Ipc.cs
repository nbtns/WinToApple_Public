using System.IO.Pipes;
using System.Text.Json;

namespace LocalBridge.WindowsShared;

public sealed record SendRequest(string[] Paths, string RequestId, DateTimeOffset CreatedAt)
{
    public static SendRequest Create(IEnumerable<string> paths) => new(
        paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
        Guid.NewGuid().ToString("N"),
        DateTimeOffset.UtcNow);
}

public static class IpcClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task SendAsync(SendRequest request, CancellationToken cancellationToken)
    {
        await using var pipe = new NamedPipeClientStream(".", AppPaths.PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(2000, cancellationToken).ConfigureAwait(false);
        var payload = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
        if (payload.Length > 1024 * 1024) throw new InvalidDataException("送信要求が大きすぎます。");
        var length = BitConverter.GetBytes(payload.Length);
        await pipe.WriteAsync(length, cancellationToken).ConfigureAwait(false);
        await pipe.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

