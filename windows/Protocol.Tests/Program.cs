using System.Security.Cryptography;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LocalBridge.Protocol;
using LocalBridge.WindowsShared;

var tests = new (string Name, Func<Task> Run)[]
{
    ("RFC 5869 HKDF vector", TestHkdfAsync),
    ("P-256 X9.63 key exchange", TestKeyExchangeAsync),
    ("AES-GCM record round trip", TestEncryptedRecordAsync),
    ("Oversized record rejection", TestOversizedRecordAsync),
    ("File sender loopback", TestFileSenderLoopbackAsync),
    ("Authenticated Phase 2 handshake", TestAuthenticatedHandshakeAsync),
    ("Authenticated transfer resume", TestAuthenticatedTransferResumeAsync),
    ("Pairing code validation", TestPairingCodeValidationAsync),
};

foreach (var test in tests)
{
    await test.Run();
    Console.WriteLine($"PASS: {test.Name}");
}

return;

static Task TestHkdfAsync()
{
    var ikm = Enumerable.Repeat((byte)0x0B, 22).ToArray();
    var salt = Convert.FromHexString("000102030405060708090A0B0C");
    var info = Convert.FromHexString("F0F1F2F3F4F5F6F7F8F9");
    var expected = "3CB25F25FAACD57A90434F64D0362F2A2D2D0A90CF1A5A4C5DB02D56ECC4C5BF34007208D5B887185865";
    AssertEqual(expected, Convert.ToHexString(HkdfSha256.DeriveKey(ikm, salt, info, 42)));
    return Task.CompletedTask;
}

static Task TestKeyExchangeAsync()
{
    using var first = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    using var second = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    using var importedFirst = ECDiffieHellman.Create(Phase1Handshake.ImportX963(Phase1Handshake.ExportX963(first.ExportParameters(false))));
    using var importedSecond = ECDiffieHellman.Create(Phase1Handshake.ImportX963(Phase1Handshake.ExportX963(second.ExportParameters(false))));
    AssertEqual(
        Convert.ToHexString(first.DeriveRawSecretAgreement(importedSecond.PublicKey)),
        Convert.ToHexString(second.DeriveRawSecretAgreement(importedFirst.PublicKey)));
    return Task.CompletedTask;
}

static async Task TestEncryptedRecordAsync()
{
    var key = RandomNumberGenerator.GetBytes(32);
    await using var stream = new MemoryStream();
    using (var writer = new EncryptedChannel(stream, key))
    {
        await writer.WriteRecordAsync("LocalBridge暗号テスト"u8.ToArray(), CancellationToken.None);
    }

    stream.Position = 0;
    using var reader = new EncryptedChannel(stream, key);
    var plaintext = await reader.ReadRecordAsync(CancellationToken.None);
    AssertEqual("LocalBridge暗号テスト", System.Text.Encoding.UTF8.GetString(plaintext));
}

static async Task TestOversizedRecordAsync()
{
    await using var stream = new MemoryStream();
    using var channel = new EncryptedChannel(stream, RandomNumberGenerator.GetBytes(32));
    try
    {
        await channel.WriteRecordAsync(new byte[ProtocolConstants.MaxRecordLength + 1], CancellationToken.None);
        throw new Exception("上限超過が拒否されませんでした。");
    }
    catch (InvalidDataException)
    {
    }
}

static async Task TestFileSenderLoopbackAsync()
{
    var filePath = Path.Combine(Environment.CurrentDirectory, $".localbridge-test-{Guid.NewGuid():N}.txt");
    var expected = Encoding.UTF8.GetBytes("日本語とemoji 🎉を含むPhase 1転送テスト");
    await File.WriteAllBytesAsync(filePath, expected);
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    try
    {
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var receiverTask = ReceiveOneFileAsync(listener, expected);
        var result = await new FileSender().SendAsync(filePath, IPAddress.Loopback.ToString(), endpoint.Port, null, CancellationToken.None);
        await receiverTask;
        AssertEqual(true, result.Success);
        AssertEqual(Path.GetFileName(filePath), result.SavedName!);
    }
    finally
    {
        listener.Stop();
        File.Delete(filePath);
    }
}

static async Task ReceiveOneFileAsync(TcpListener listener, byte[] expected)
{
    using var client = await listener.AcceptTcpClientAsync();
    await using var stream = client.GetStream();
    var key = await Phase1Handshake.AcceptAsServerAsync(stream, CancellationToken.None);
    try
    {
        using var channel = new EncryptedChannel(stream, key);
        var metadataMessage = await channel.ReadRecordAsync(CancellationToken.None);
        AssertEqual((byte)MessageType.Metadata, metadataMessage[0]);
        using var metadata = JsonDocument.Parse(metadataMessage.AsMemory(1));
        AssertEqual((long)expected.Length, metadata.RootElement.GetProperty("size").GetInt64());

        await using var received = new MemoryStream();
        while (true)
        {
            var message = await channel.ReadRecordAsync(CancellationToken.None);
            if (message[0] == (byte)MessageType.Complete)
            {
                break;
            }

            AssertEqual((byte)MessageType.Chunk, message[0]);
            var offset = BinaryPrimitives.ReadUInt64BigEndian(message.AsSpan(1, 8));
            AssertEqual((ulong)received.Length, offset);
            await received.WriteAsync(message.AsMemory(9));
        }

        AssertEqual(Convert.ToHexString(expected), Convert.ToHexString(received.ToArray()));
        var resultJson = Encoding.UTF8.GetBytes($"{{\"success\":true,\"savedName\":{JsonSerializer.Serialize(metadata.RootElement.GetProperty("fileName").GetString())},\"error\":null}}");
        var resultMessage = new byte[resultJson.Length + 2];
        resultMessage[0] = (byte)MessageType.Result;
        resultMessage[1] = 0;
        resultJson.CopyTo(resultMessage.AsSpan(2));
        await channel.WriteRecordAsync(resultMessage, CancellationToken.None);
    }
    finally
    {
        CryptographicOperations.ZeroMemory(key);
    }
}

static async Task TestAuthenticatedHandshakeAsync()
{
    using var serverSigning = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    using var clientSigning = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var serverId = Guid.NewGuid();
    var clientId = Guid.NewGuid();
    var serverPublic = Phase1Handshake.ExportX963(serverSigning.ExportParameters(false));
    var clientPublic = Phase1Handshake.ExportX963(clientSigning.ExportParameters(false));
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    try
    {
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var serverTask = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptTcpClientAsync();
            await using var serverStream = accepted.GetStream();
            var session = await AuthenticatedHandshake.AcceptAsServerAsync(
                serverStream,
                new AuthenticatedServerIdentity(serverId, serverSigning, id => id == clientId ? clientPublic : null),
                CancellationToken.None);
            AssertEqual(clientId, session.ClientDeviceId);
            using var channel = new EncryptedChannel(serverStream, session.SessionKey);
            var message = await channel.ReadRecordAsync(CancellationToken.None);
            AssertEqual("authenticated", Encoding.UTF8.GetString(message));
            CryptographicOperations.ZeroMemory(session.SessionKey);
        });

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, endpoint.Port);
        await using var clientStream = client.GetStream();
        var key = await AuthenticatedHandshake.ConnectAsClientAsync(
            clientStream,
            new AuthenticatedClientIdentity(clientId, clientSigning, serverId, serverPublic),
            CancellationToken.None);
        using (var channel = new EncryptedChannel(clientStream, key))
        {
            await channel.WriteRecordAsync(Encoding.UTF8.GetBytes("authenticated"), CancellationToken.None);
        }
        CryptographicOperations.ZeroMemory(key);
        await serverTask;
    }
    finally
    {
        listener.Stop();
    }
}

static async Task TestAuthenticatedTransferResumeAsync()
{
    using var serverSigning = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    using var clientSigning = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var serverId = Guid.NewGuid();
    var clientId = Guid.NewGuid();
    var serverPublic = Phase1Handshake.ExportX963(serverSigning.ExportParameters(false));
    var clientPublic = Phase1Handshake.ExportX963(clientSigning.ExportParameters(false));
    var expected = RandomNumberGenerator.GetBytes(256 * 1024 + 37);
    var resumeOffset = 73_123;
    var filePath = Path.Combine(Environment.CurrentDirectory, $".localbridge-resume-{Guid.NewGuid():N}.bin");
    await File.WriteAllBytesAsync(filePath, expected);
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    try
    {
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var serverTask = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptTcpClientAsync();
            await using var stream = accepted.GetStream();
            var session = await AuthenticatedHandshake.AcceptAsServerAsync(
                stream,
                new AuthenticatedServerIdentity(serverId, serverSigning, id => id == clientId ? clientPublic : null),
                CancellationToken.None);
            try
            {
                using var channel = new EncryptedChannel(stream, session.SessionKey);
                var metadataMessage = await channel.ReadRecordAsync(CancellationToken.None);
                AssertEqual((byte)MessageType.Metadata, metadataMessage[0]);
                using var metadata = JsonDocument.Parse(metadataMessage.AsMemory(1));
                AssertEqual(2, metadata.RootElement.GetProperty("protocolVersion").GetInt32());
                AssertEqual(expected.LongLength, metadata.RootElement.GetProperty("size").GetInt64());

                var resume = new byte[9];
                resume[0] = (byte)MessageType.Resume;
                BinaryPrimitives.WriteUInt64BigEndian(resume.AsSpan(1), (ulong)resumeOffset);
                await channel.WriteRecordAsync(resume, CancellationToken.None);

                await using var received = new MemoryStream();
                await received.WriteAsync(expected.AsMemory(0, resumeOffset));
                while (true)
                {
                    var message = await channel.ReadRecordAsync(CancellationToken.None);
                    if (message[0] == (byte)MessageType.Complete) break;
                    AssertEqual((byte)MessageType.Chunk, message[0]);
                    var offset = BinaryPrimitives.ReadUInt64BigEndian(message.AsSpan(1, 8));
                    AssertEqual((ulong)received.Length, offset);
                    await received.WriteAsync(message.AsMemory(9));
                }

                AssertEqual(Convert.ToHexString(expected), Convert.ToHexString(received.ToArray()));
                var resultJson = Encoding.UTF8.GetBytes("{\"success\":true,\"savedName\":\"resume.bin\",\"error\":null}");
                var resultMessage = new byte[resultJson.Length + 2];
                resultMessage[0] = (byte)MessageType.Result;
                resultMessage[1] = 0;
                resultJson.CopyTo(resultMessage.AsSpan(2));
                await channel.WriteRecordAsync(resultMessage, CancellationToken.None);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(session.SessionKey);
            }
        });

        var result = await new AuthenticatedFileSender().SendAsync(
            filePath,
            false,
            "resume.bin",
            IPAddress.Loopback.ToString(),
            endpoint.Port,
            new AuthenticatedClientIdentity(clientId, clientSigning, serverId, serverPublic),
            null,
            CancellationToken.None);
        AssertEqual(true, result.Success);
        AssertEqual("resume.bin", result.SavedName!);
        await serverTask;
    }
    finally
    {
        listener.Stop();
        File.Delete(filePath);
    }
}

static Task TestPairingCodeValidationAsync()
{
    var decoded = Base32.Decode(new string('A', 26));
    AssertEqual(16, decoded.Length);
    AssertEqual(true, decoded.All(value => value == 0));
    try
    {
        _ = Base32.Decode(new string('A', 25) + "B");
        throw new Exception("Base32末尾の余分なbitが拒否されませんでした。");
    }
    catch (FormatException)
    {
    }
    return Task.CompletedTask;
}

static void AssertEqual<T>(T expected, T actual) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new Exception($"Expected: {expected}; Actual: {actual}");
    }
}
