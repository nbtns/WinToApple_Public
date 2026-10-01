using System.Security.Cryptography;

namespace LocalBridge.Protocol;

public sealed class EncryptedChannel : IDisposable
{
    private readonly Stream _stream;
    private readonly byte[] _key;
    private bool _disposed;

    public EncryptedChannel(Stream stream, ReadOnlySpan<byte> key)
    {
        if (key.Length != 32)
        {
            throw new ArgumentException("AES-256鍵は32 byteである必要があります。", nameof(key));
        }

        _stream = stream;
        _key = key.ToArray();
    }

    public async Task WriteRecordAsync(ReadOnlyMemory<byte> plaintext, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (plaintext.Length > ProtocolConstants.MaxRecordLength)
        {
            throw new InvalidDataException("送信レコードが上限を超えています。");
        }

        var nonce = RandomNumberGenerator.GetBytes(ProtocolConstants.GcmNonceLength);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[ProtocolConstants.GcmTagLength];
        using (var aes = new AesGcm(_key, ProtocolConstants.GcmTagLength))
        {
            aes.Encrypt(nonce, plaintext.Span, ciphertext, tag);
        }

        await BinaryStream.WriteUInt32Async(_stream, (uint)plaintext.Length, cancellationToken).ConfigureAwait(false);
        await _stream.WriteAsync(nonce, cancellationToken).ConfigureAwait(false);
        await _stream.WriteAsync(ciphertext, cancellationToken).ConfigureAwait(false);
        await _stream.WriteAsync(tag, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]> ReadRecordAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var length = await BinaryStream.ReadUInt32Async(_stream, cancellationToken).ConfigureAwait(false);
        if (length > ProtocolConstants.MaxRecordLength)
        {
            throw new InvalidDataException("受信レコードが上限を超えています。");
        }

        var nonce = new byte[ProtocolConstants.GcmNonceLength];
        var ciphertext = new byte[checked((int)length)];
        var tag = new byte[ProtocolConstants.GcmTagLength];
        await BinaryStream.ReadExactlyAsync(_stream, nonce, cancellationToken).ConfigureAwait(false);
        await BinaryStream.ReadExactlyAsync(_stream, ciphertext, cancellationToken).ConfigureAwait(false);
        await BinaryStream.ReadExactlyAsync(_stream, tag, cancellationToken).ConfigureAwait(false);

        var plaintext = new byte[ciphertext.Length];
        using (var aes = new AesGcm(_key, ProtocolConstants.GcmTagLength))
        {
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }

        return plaintext;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CryptographicOperations.ZeroMemory(_key);
    }
}

