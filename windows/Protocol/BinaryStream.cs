using System.Buffers.Binary;

namespace LocalBridge.Protocol;

public static class BinaryStream
{
    public static async ValueTask ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                throw new EndOfStreamException("接続が途中で閉じられました。");
            }

            read += count;
        }
    }

    public static async ValueTask WriteUInt32Async(Stream stream, uint value, CancellationToken cancellationToken)
    {
        var buffer = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<uint> ReadUInt32Async(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[sizeof(uint)];
        await ReadExactlyAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt32BigEndian(buffer);
    }
}

