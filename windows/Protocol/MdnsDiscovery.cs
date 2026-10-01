using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LocalBridge.Protocol;

public sealed record DiscoveredService(string InstanceName, string HostName, IPAddress Address, int Port);

public sealed class MdnsDiscovery
{
    public const string TransferServiceType = "_localbridge._tcp.local";
    public const string PairingServiceType = "_localbridge-pair._tcp.local";
    private static readonly IPEndPoint MdnsEndpoint = new(IPAddress.Parse("224.0.0.251"), 5353);

    public async Task<DiscoveredService?> FindAsync(TimeSpan timeout, CancellationToken cancellationToken)
        => await FindAsync(TransferServiceType, timeout, cancellationToken).ConfigureAwait(false);

    public async Task<DiscoveredService?> FindAsync(string serviceType, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(serviceType) || !serviceType.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Bonjourサービス種別が不正です。", nameof(serviceType));
        }

        using var socket = new UdpClient(AddressFamily.InterNetwork);
        socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var query = BuildQuery(serviceType, 12);
        await socket.SendAsync(query, MdnsEndpoint, cancellationToken).ConfigureAwait(false);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var records = new List<DnsRecord>();
        string? queriedInstance = null;
        string? queriedHost = null;
        try
        {
            while (!timeoutSource.IsCancellationRequested)
            {
                var response = await socket.ReceiveAsync(timeoutSource.Token).ConfigureAwait(false);
                records.AddRange(ParseRecords(response.Buffer));
                var result = TryBuildService(records, serviceType);
                if (result is not null)
                {
                    return result;
                }

                var instance = records.FirstOrDefault(r => r.Type == 12 && NameEquals(r.Name, serviceType))?.Target;
                if (instance is not null && !NameEquals(instance, queriedInstance ?? string.Empty))
                {
                    queriedInstance = instance;
                    await socket.SendAsync(BuildQuery(instance, 33), MdnsEndpoint, timeoutSource.Token).ConfigureAwait(false);
                }

                var host = instance is null
                    ? null
                    : records.FirstOrDefault(r => r.Type == 33 && NameEquals(r.Name, instance))?.Target;
                if (host is not null && !NameEquals(host, queriedHost ?? string.Empty))
                {
                    queriedHost = host;
                    await socket.SendAsync(BuildQuery(host, 1), MdnsEndpoint, timeoutSource.Token).ConfigureAwait(false);
                    await socket.SendAsync(BuildQuery(host, 28), MdnsEndpoint, timeoutSource.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        return null;
    }

    private static DiscoveredService? TryBuildService(IReadOnlyList<DnsRecord> records, string serviceType)
    {
        var instance = records.FirstOrDefault(r => r.Type == 12 && NameEquals(r.Name, serviceType))?.Target;
        if (instance is null)
        {
            return null;
        }

        var srv = records.FirstOrDefault(r => r.Type == 33 && NameEquals(r.Name, instance));
        if (srv?.Target is null || srv.Port is null)
        {
            return null;
        }

        var address = records.FirstOrDefault(r => (r.Type == 1 || r.Type == 28) && NameEquals(r.Name, srv.Target))?.Address;
        return address is null ? null : new DiscoveredService(instance, srv.Target, address, srv.Port.Value);
    }

    private static byte[] BuildQuery(string name, ushort type)
    {
        using var stream = new MemoryStream();
        stream.Write(new byte[12]);
        var header = stream.GetBuffer();
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4, 2), 1);
        foreach (var label in name.Split('.'))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            if (bytes.Length is 0 or > 63)
            {
                throw new InvalidDataException("Bonjourサービス名が不正です。");
            }

            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes);
        }

        stream.WriteByte(0);
        Span<byte> suffix = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(suffix, type);
        BinaryPrimitives.WriteUInt16BigEndian(suffix[2..], 0x8001); // unicast response requested
        stream.Write(suffix);
        return stream.ToArray();
    }

    private static IReadOnlyList<DnsRecord> ParseRecords(byte[] packet)
    {
        if (packet.Length < 12)
        {
            return Array.Empty<DnsRecord>();
        }

        var questionCount = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(4, 2));
        var answerCount = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(6, 2));
        var authorityCount = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(8, 2));
        var additionalCount = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(10, 2));
        var offset = 12;

        try
        {
            for (var index = 0; index < questionCount; index++)
            {
                _ = ReadName(packet, ref offset);
                offset = checked(offset + 4);
            }

            var count = answerCount + authorityCount + additionalCount;
            var output = new List<DnsRecord>(count);
            for (var index = 0; index < count; index++)
            {
                var name = ReadName(packet, ref offset);
                EnsureAvailable(packet, offset, 10);
                var type = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset, 2));
                var dataLength = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset + 8, 2));
                offset += 10;
                EnsureAvailable(packet, offset, dataLength);
                var dataOffset = offset;
                offset += dataLength;

                string? target = null;
                int? port = null;
                IPAddress? address = null;
                if (type == 12)
                {
                    var cursor = dataOffset;
                    target = ReadName(packet, ref cursor);
                }
                else if (type == 33 && dataLength >= 6)
                {
                    port = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(dataOffset + 4, 2));
                    var cursor = dataOffset + 6;
                    target = ReadName(packet, ref cursor);
                }
                else if (type == 1 && dataLength == 4)
                {
                    address = new IPAddress(packet.AsSpan(dataOffset, 4));
                }
                else if (type == 28 && dataLength == 16)
                {
                    address = new IPAddress(packet.AsSpan(dataOffset, 16));
                }

                output.Add(new DnsRecord(name, type, target, port, address));
            }

            return output;
        }
        catch (InvalidDataException)
        {
            return Array.Empty<DnsRecord>();
        }
        catch (OverflowException)
        {
            return Array.Empty<DnsRecord>();
        }
    }

    private static string ReadName(byte[] packet, ref int offset)
    {
        var labels = new List<string>();
        var cursor = offset;
        var jumped = false;
        var jumps = 0;
        while (true)
        {
            EnsureAvailable(packet, cursor, 1);
            var length = packet[cursor++];
            if (length == 0)
            {
                if (!jumped)
                {
                    offset = cursor;
                }

                return string.Join('.', labels);
            }

            if ((length & 0xC0) == 0xC0)
            {
                EnsureAvailable(packet, cursor, 1);
                var pointer = ((length & 0x3F) << 8) | packet[cursor++];
                if (!jumped)
                {
                    offset = cursor;
                    jumped = true;
                }

                if (++jumps > 16 || pointer >= packet.Length)
                {
                    throw new InvalidDataException("DNS名の圧縮参照が不正です。");
                }

                cursor = pointer;
                continue;
            }

            if ((length & 0xC0) != 0 || length > 63)
            {
                throw new InvalidDataException("DNSラベルが不正です。");
            }

            EnsureAvailable(packet, cursor, length);
            labels.Add(Encoding.UTF8.GetString(packet, cursor, length));
            cursor += length;
            if (!jumped)
            {
                offset = cursor;
            }
        }
    }

    private static void EnsureAvailable(byte[] packet, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > packet.Length - length)
        {
            throw new InvalidDataException("DNS応答が途中で切れています。");
        }
    }

    private static bool NameEquals(string left, string right) =>
        string.Equals(left.TrimEnd('.'), right.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    private sealed record DnsRecord(string Name, ushort Type, string? Target, int? Port, IPAddress? Address);
}
