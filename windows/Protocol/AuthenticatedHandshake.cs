using System.Buffers.Binary;
using System.Security.Cryptography;

namespace LocalBridge.Protocol;

public sealed record AuthenticatedClientIdentity(
    Guid DeviceId,
    ECDsa SigningKey,
    Guid ExpectedServerDeviceId,
    byte[] ExpectedServerPublicKey);

public sealed record AuthenticatedServerIdentity(
    Guid DeviceId,
    ECDsa SigningKey,
    Func<Guid, byte[]?> ResolveClientPublicKey);

public sealed record AuthenticatedServerSession(Guid ClientDeviceId, byte[] SessionKey);

public static class AuthenticatedHandshake
{
    private static ReadOnlySpan<byte> Magic => "LBP2"u8;
    private static ReadOnlySpan<byte> ServerContext => "LocalBridge-LBP2-SERVER"u8;
    private static ReadOnlySpan<byte> HkdfContext => "LocalBridge LBP2 AES-256-GCM"u8;
    private const int FixedServerHelloLength = 4 + 16 + 65 + 65 + 32;
    private const int FixedClientHelloLength = 4 + 16 + 65 + 32;
    private const int MaxSignatureLength = 256;

    public static async Task<AuthenticatedServerSession> AcceptAsServerAsync(
        Stream stream,
        AuthenticatedServerIdentity identity,
        CancellationToken cancellationToken)
    {
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var serverFixed = new byte[FixedServerHelloLength];
        Magic.CopyTo(serverFixed);
        WriteGuid(identity.DeviceId, serverFixed.AsSpan(4, 16));
        Phase1Handshake.ExportX963(identity.SigningKey.ExportParameters(false)).CopyTo(serverFixed.AsSpan(20, 65));
        Phase1Handshake.ExportX963(ephemeral.ExportParameters(false)).CopyTo(serverFixed.AsSpan(85, 65));
        var serverNonce = RandomNumberGenerator.GetBytes(32);
        serverNonce.CopyTo(serverFixed.AsSpan(150, 32));
        var serverSigned = new byte[ServerContext.Length + serverFixed.Length];
        ServerContext.CopyTo(serverSigned);
        serverFixed.CopyTo(serverSigned.AsSpan(ServerContext.Length));
        var serverSignature = identity.SigningKey.SignData(
            serverSigned,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);
        if (serverSignature.Length > MaxSignatureLength) throw new CryptographicException("サーバー署名が上限を超えました。");
        var serverHello = new byte[serverFixed.Length + 2 + serverSignature.Length];
        serverFixed.CopyTo(serverHello);
        BinaryPrimitives.WriteUInt16BigEndian(serverHello.AsSpan(serverFixed.Length, 2), checked((ushort)serverSignature.Length));
        serverSignature.CopyTo(serverHello.AsSpan(serverFixed.Length + 2));
        await stream.WriteAsync(serverHello, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        var clientFixed = new byte[FixedClientHelloLength];
        await BinaryStream.ReadExactlyAsync(stream, clientFixed, cancellationToken).ConfigureAwait(false);
        if (!clientFixed.AsSpan(0, Magic.Length).SequenceEqual(Magic)) throw new InvalidDataException("クライアントがPhase 2に対応していません。");
        var clientId = ReadGuid(clientFixed.AsSpan(4, 16));
        var clientPublic = identity.ResolveClientPublicKey(clientId)
            ?? throw new CryptographicException("登録されていないクライアントです。");
        var signatureLengthBytes = new byte[2];
        await BinaryStream.ReadExactlyAsync(stream, signatureLengthBytes, cancellationToken).ConfigureAwait(false);
        var signatureLength = BinaryPrimitives.ReadUInt16BigEndian(signatureLengthBytes);
        if (signatureLength is 0 or > MaxSignatureLength) throw new InvalidDataException("クライアント署名が不正です。");
        var clientSignature = new byte[signatureLength];
        await BinaryStream.ReadExactlyAsync(stream, clientSignature, cancellationToken).ConfigureAwait(false);
        var clientSigned = new byte[serverHello.Length + clientFixed.Length];
        serverHello.CopyTo(clientSigned);
        clientFixed.CopyTo(clientSigned.AsSpan(serverHello.Length));
        using (var verifier = ECDsa.Create(Phase1Handshake.ImportX963(clientPublic)))
        {
            if (!verifier.VerifyData(clientSigned, clientSignature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
                throw new CryptographicException("クライアント署名を確認できませんでした。");
        }

        var clientEphemeral = clientFixed.AsSpan(20, 65).ToArray();
        var clientNonce = clientFixed.AsSpan(85, 32).ToArray();
        using var clientKey = ECDiffieHellman.Create(Phase1Handshake.ImportX963(clientEphemeral));
        var secret = ephemeral.DeriveRawSecretAgreement(clientKey.PublicKey);
        try
        {
            var salt = new byte[64];
            serverNonce.CopyTo(salt, 0);
            clientNonce.CopyTo(salt, 32);
            var transcriptHash = SHA256.HashData(clientSigned);
            var info = new byte[HkdfContext.Length + transcriptHash.Length];
            HkdfContext.CopyTo(info);
            transcriptHash.CopyTo(info.AsSpan(HkdfContext.Length));
            return new AuthenticatedServerSession(clientId, HkdfSha256.DeriveKey(secret, salt, info, 32));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    public static async Task<byte[]> ConnectAsClientAsync(
        Stream stream,
        AuthenticatedClientIdentity identity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity.SigningKey);
        if (identity.ExpectedServerPublicKey.Length != ProtocolConstants.PublicKeyLength)
        {
            throw new CryptographicException("登録済みiPhoneの公開鍵が不正です。");
        }

        var serverFixed = new byte[FixedServerHelloLength];
        await BinaryStream.ReadExactlyAsync(stream, serverFixed, cancellationToken).ConfigureAwait(false);
        if (!serverFixed.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new InvalidDataException("iPhoneがLocalBridge Phase 2に対応していません。");
        }

        var serverDeviceId = ReadGuid(serverFixed.AsSpan(4, 16));
        if (serverDeviceId != identity.ExpectedServerDeviceId)
        {
            throw new CryptographicException("登録したiPhoneとは異なる端末が応答しました。");
        }

        var serverSigningPublic = serverFixed.AsSpan(20, 65).ToArray();
        if (!CryptographicOperations.FixedTimeEquals(serverSigningPublic, identity.ExpectedServerPublicKey))
        {
            throw new CryptographicException("iPhoneの公開鍵が登録時と一致しません。");
        }

        var signatureLengthBytes = new byte[2];
        await BinaryStream.ReadExactlyAsync(stream, signatureLengthBytes, cancellationToken).ConfigureAwait(false);
        var serverSignatureLength = BinaryPrimitives.ReadUInt16BigEndian(signatureLengthBytes);
        if (serverSignatureLength is 0 or > MaxSignatureLength)
        {
            throw new InvalidDataException("iPhoneの署名サイズが不正です。");
        }

        var serverSignature = new byte[serverSignatureLength];
        await BinaryStream.ReadExactlyAsync(stream, serverSignature, cancellationToken).ConfigureAwait(false);
        var serverSigned = new byte[ServerContext.Length + serverFixed.Length];
        ServerContext.CopyTo(serverSigned);
        serverFixed.CopyTo(serverSigned.AsSpan(ServerContext.Length));
        using (var verifier = ECDsa.Create(Phase1Handshake.ImportX963(serverSigningPublic)))
        {
            if (!verifier.VerifyData(serverSigned, serverSignature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
            {
                throw new CryptographicException("iPhoneの本人確認に失敗しました。");
            }
        }

        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var clientFixed = new byte[FixedClientHelloLength];
        Magic.CopyTo(clientFixed);
        WriteGuid(identity.DeviceId, clientFixed.AsSpan(4, 16));
        Phase1Handshake.ExportX963(ephemeral.ExportParameters(false)).CopyTo(clientFixed.AsSpan(20, 65));
        var clientNonce = RandomNumberGenerator.GetBytes(32);
        clientNonce.CopyTo(clientFixed.AsSpan(85, 32));

        var serverHello = new byte[serverFixed.Length + 2 + serverSignature.Length];
        serverFixed.CopyTo(serverHello);
        signatureLengthBytes.CopyTo(serverHello.AsSpan(serverFixed.Length));
        serverSignature.CopyTo(serverHello.AsSpan(serverFixed.Length + 2));
        var clientSigned = new byte[serverHello.Length + clientFixed.Length];
        serverHello.CopyTo(clientSigned);
        clientFixed.CopyTo(clientSigned.AsSpan(serverHello.Length));
        var clientSignature = identity.SigningKey.SignData(
            clientSigned,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);
        if (clientSignature.Length > MaxSignatureLength)
        {
            throw new CryptographicException("Windowsの署名サイズが上限を超えました。");
        }

        var clientHello = new byte[clientFixed.Length + 2 + clientSignature.Length];
        clientFixed.CopyTo(clientHello);
        BinaryPrimitives.WriteUInt16BigEndian(clientHello.AsSpan(clientFixed.Length, 2), checked((ushort)clientSignature.Length));
        clientSignature.CopyTo(clientHello.AsSpan(clientFixed.Length + 2));
        await stream.WriteAsync(clientHello, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        var serverEphemeral = serverFixed.AsSpan(85, 65).ToArray();
        var serverNonce = serverFixed.AsSpan(150, 32).ToArray();
        using var serverKey = ECDiffieHellman.Create(Phase1Handshake.ImportX963(serverEphemeral));
        var secret = ephemeral.DeriveRawSecretAgreement(serverKey.PublicKey);
        try
        {
            var salt = new byte[64];
            serverNonce.CopyTo(salt, 0);
            clientNonce.CopyTo(salt, 32);
            var transcriptHash = SHA256.HashData(clientSigned);
            var info = new byte[HkdfContext.Length + transcriptHash.Length];
            HkdfContext.CopyTo(info);
            transcriptHash.CopyTo(info.AsSpan(HkdfContext.Length));
            return HkdfSha256.DeriveKey(secret, salt, info, 32);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    public static void WriteGuid(Guid value, Span<byte> destination)
    {
        if (!value.TryWriteBytes(destination, bigEndian: true, out var written) || written != 16)
        {
            throw new InvalidOperationException("端末IDを書き出せませんでした。");
        }
    }

    public static Guid ReadGuid(ReadOnlySpan<byte> source) => new(source, bigEndian: true);
}
