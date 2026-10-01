using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace LocalBridge.Protocol;

public sealed record AuthenticatedFileMetadata(
    int ProtocolVersion,
    string FileId,
    string FileName,
    long Size,
    DateTimeOffset ModifiedUtc,
    string MimeType,
    string Sha256,
    bool FolderArchive);

public sealed class AuthenticatedFileSender
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<TransferResult> SendAsync(
        string filePath,
        bool folderArchive,
        string? displayName,
        string host,
        int port,
        AuthenticatedClientIdentity identity,
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
            throw new InvalidDataException("ファイル上限20 GiBを超えています。");
        }

        var hash = await ComputeSha256Async(file.FullName, cancellationToken).ConfigureAwait(false);
        var hashText = Convert.ToHexStringLower(hash);
        var metadata = new AuthenticatedFileMetadata(
            2,
            hashText,
            string.IsNullOrWhiteSpace(displayName) ? file.Name : Path.GetFileName(displayName),
            file.Length,
            file.LastWriteTimeUtc,
            GetMimeType(file.Extension),
            hashText,
            folderArchive);

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        await using var stream = tcp.GetStream();
        var sessionKey = await AuthenticatedHandshake.ConnectAsClientAsync(stream, identity, cancellationToken).ConfigureAwait(false);
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

            var resume = await channel.ReadRecordAsync(cancellationToken).ConfigureAwait(false);
            if (resume.Length != 9 || resume[0] != (byte)MessageType.Resume)
            {
                throw new InvalidDataException("iPhoneから再開位置を受信できませんでした。");
            }
            var offset = checked((long)BinaryPrimitives.ReadUInt64BigEndian(resume.AsSpan(1)));
            if (offset < 0 || offset > file.Length)
            {
                throw new InvalidDataException("iPhoneの再開位置が不正です。");
            }

            await using var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, ProtocolConstants.ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            input.Position = offset;
            progress?.Report((offset, file.Length));
            var buffer = new byte[ProtocolConstants.ChunkSize];
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                var message = new byte[1 + sizeof(ulong) + read];
                message[0] = (byte)MessageType.Chunk;
                BinaryPrimitives.WriteUInt64BigEndian(message.AsSpan(1, sizeof(ulong)), checked((ulong)offset));
                buffer.AsSpan(0, read).CopyTo(message.AsSpan(9));
                await channel.WriteRecordAsync(message, cancellationToken).ConfigureAwait(false);
                offset += read;
                progress?.Report((offset, file.Length));
            }

            await channel.WriteRecordAsync(new[] { (byte)MessageType.Complete }, cancellationToken).ConfigureAwait(false);
            var result = await channel.ReadRecordAsync(cancellationToken).ConfigureAwait(false);
            return ParseResult(result);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sessionKey);
        }
    }

    private static TransferResult ParseResult(ReadOnlySpan<byte> message)
    {
        if (message.Length < 2 || message[0] != (byte)MessageType.Result)
            throw new InvalidDataException("iPhoneから不正な完了応答を受信しました。");
        var result = JsonSerializer.Deserialize<TransferResult>(message[2..], JsonOptions)
            ?? throw new InvalidDataException("iPhoneからの完了応答を解釈できません。");
        return result with { Success = message[1] == 0 && result.Success };
    }

    private static async Task<byte[]> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
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
        ".flac" => "audio/flac",
        ".pdf" => "application/pdf",
        ".zip" => "application/zip",
        _ => "application/octet-stream",
    };
}
