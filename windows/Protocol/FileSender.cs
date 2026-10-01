using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace LocalBridge.Protocol;

public sealed class FileSender
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<TransferResult> SendAsync(
        string filePath,
        string host,
        int port,
        IProgress<(long Sent, long Total)>? progress,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo(filePath);
        if (!file.Exists)
        {
            throw new FileNotFoundException("送信するファイルが見つかりません。", filePath);
        }

        if (file.Length > ProtocolConstants.MaxFileLength)
        {
            throw new InvalidDataException("Phase 1のファイル上限20 GiBを超えています。");
        }

        var hash = await ComputeSha256Async(file.FullName, cancellationToken).ConfigureAwait(false);
        var metadata = new FileTransferMetadata(
            1,
            file.Name,
            file.Length,
            file.LastWriteTimeUtc,
            GetMimeType(file.Extension),
            Convert.ToHexStringLower(hash));

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        await using var stream = tcp.GetStream();
        var sessionKey = await Phase1Handshake.ConnectAsClientAsync(stream, cancellationToken).ConfigureAwait(false);
        try
        {
            using var channel = new EncryptedChannel(stream, sessionKey);
            var metadataJson = JsonSerializer.SerializeToUtf8Bytes(metadata, JsonOptions);
            if (metadataJson.Length > ProtocolConstants.MaxMetadataLength)
            {
                throw new InvalidDataException("ファイル情報が上限を超えています。");
            }

            var metadataMessage = new byte[metadataJson.Length + 1];
            metadataMessage[0] = (byte)MessageType.Metadata;
            metadataJson.CopyTo(metadataMessage.AsSpan(1));
            await channel.WriteRecordAsync(metadataMessage, cancellationToken).ConfigureAwait(false);

            await using var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, ProtocolConstants.ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[ProtocolConstants.ChunkSize];
            long offset = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                var message = new byte[1 + sizeof(ulong) + read];
                message[0] = (byte)MessageType.Chunk;
                BinaryPrimitives.WriteUInt64BigEndian(message.AsSpan(1, sizeof(ulong)), checked((ulong)offset));
                buffer.AsSpan(0, read).CopyTo(message.AsSpan(1 + sizeof(ulong)));
                await channel.WriteRecordAsync(message, cancellationToken).ConfigureAwait(false);
                offset += read;
                progress?.Report((offset, file.Length));
            }

            await channel.WriteRecordAsync(new[] { (byte)MessageType.Complete }, cancellationToken).ConfigureAwait(false);
            var resultMessage = await channel.ReadRecordAsync(cancellationToken).ConfigureAwait(false);
            return ParseResult(resultMessage);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sessionKey);
        }
    }

    private static async Task<byte[]> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static TransferResult ParseResult(ReadOnlySpan<byte> message)
    {
        if (message.Length < 2 || message[0] != (byte)MessageType.Result)
        {
            throw new InvalidDataException("iPhoneから不正な完了応答を受信しました。");
        }

        var payload = JsonSerializer.Deserialize<TransferResult>(message[2..], JsonOptions)
            ?? throw new InvalidDataException("iPhoneからの完了応答を解釈できません。");
        return payload with { Success = message[1] == 0 && payload.Success };
    }

    private static string GetMimeType(string extension) => extension.ToLowerInvariant() switch
    {
        ".txt" => "text/plain",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".heic" => "image/heic",
        ".gif" => "image/gif",
        ".mp4" => "video/mp4",
        ".mov" => "video/quicktime",
        ".wav" => "audio/wav",
        ".mp3" => "audio/mpeg",
        ".pdf" => "application/pdf",
        ".zip" => "application/zip",
        _ => "application/octet-stream",
    };
}

