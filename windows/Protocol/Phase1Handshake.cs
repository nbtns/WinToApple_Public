using System.Security.Cryptography;

namespace LocalBridge.Protocol;

public static class Phase1Handshake
{
    public static async Task<byte[]> AcceptAsServerAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var serverKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var serverPublic = ExportX963(serverKey.ExportParameters(false));
        var serverNonce = RandomNumberGenerator.GetBytes(ProtocolConstants.HandshakeNonceLength);
        var serverHello = new byte[ProtocolConstants.Magic.Length + ProtocolConstants.PublicKeyLength + ProtocolConstants.HandshakeNonceLength];
        ProtocolConstants.Magic.CopyTo(serverHello);
        serverPublic.CopyTo(serverHello.AsSpan(ProtocolConstants.Magic.Length));
        serverNonce.CopyTo(serverHello.AsSpan(ProtocolConstants.Magic.Length + ProtocolConstants.PublicKeyLength));
        await stream.WriteAsync(serverHello, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        var clientHello = new byte[serverHello.Length];
        await BinaryStream.ReadExactlyAsync(stream, clientHello, cancellationToken).ConfigureAwait(false);
        ValidateMagic(clientHello);
        var clientPublic = clientHello.AsSpan(ProtocolConstants.Magic.Length, ProtocolConstants.PublicKeyLength).ToArray();
        var clientNonce = clientHello.AsSpan(ProtocolConstants.Magic.Length + ProtocolConstants.PublicKeyLength, ProtocolConstants.HandshakeNonceLength).ToArray();

        using var clientKey = ECDiffieHellman.Create(ImportX963(clientPublic));
        var sharedSecret = serverKey.DeriveRawSecretAgreement(clientKey.PublicKey);
        try
        {
            var salt = new byte[ProtocolConstants.HandshakeNonceLength * 2];
            serverNonce.CopyTo(salt, 0);
            clientNonce.CopyTo(salt, ProtocolConstants.HandshakeNonceLength);
            return HkdfSha256.DeriveKey(sharedSecret, salt, ProtocolConstants.HkdfInfo, 32);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sharedSecret);
        }
    }

    public static async Task<byte[]> ConnectAsClientAsync(Stream stream, CancellationToken cancellationToken)
    {
        var serverHello = new byte[ProtocolConstants.Magic.Length + ProtocolConstants.PublicKeyLength + ProtocolConstants.HandshakeNonceLength];
        await BinaryStream.ReadExactlyAsync(stream, serverHello, cancellationToken).ConfigureAwait(false);
        ValidateMagic(serverHello);

        var serverPublic = serverHello.AsSpan(ProtocolConstants.Magic.Length, ProtocolConstants.PublicKeyLength).ToArray();
        var serverNonce = serverHello.AsSpan(ProtocolConstants.Magic.Length + ProtocolConstants.PublicKeyLength, ProtocolConstants.HandshakeNonceLength).ToArray();

        using var clientKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var clientPublic = ExportX963(clientKey.ExportParameters(false));
        var clientNonce = RandomNumberGenerator.GetBytes(ProtocolConstants.HandshakeNonceLength);

        var clientHello = new byte[serverHello.Length];
        ProtocolConstants.Magic.CopyTo(clientHello);
        clientPublic.CopyTo(clientHello.AsSpan(ProtocolConstants.Magic.Length));
        clientNonce.CopyTo(clientHello.AsSpan(ProtocolConstants.Magic.Length + ProtocolConstants.PublicKeyLength));
        await stream.WriteAsync(clientHello, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        using var serverKey = ECDiffieHellman.Create(ImportX963(serverPublic));
        var sharedSecret = clientKey.DeriveRawSecretAgreement(serverKey.PublicKey);
        try
        {
            var salt = new byte[ProtocolConstants.HandshakeNonceLength * 2];
            serverNonce.CopyTo(salt);
            clientNonce.CopyTo(salt.AsSpan(ProtocolConstants.HandshakeNonceLength));
            return HkdfSha256.DeriveKey(sharedSecret, salt, ProtocolConstants.HkdfInfo, 32);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sharedSecret);
        }
    }

    public static byte[] ExportX963(ECParameters parameters)
    {
        if (parameters.Q.X is not { Length: 32 } x || parameters.Q.Y is not { Length: 32 } y)
        {
            throw new CryptographicException("P-256公開鍵を出力できませんでした。");
        }

        var output = new byte[ProtocolConstants.PublicKeyLength];
        output[0] = 0x04;
        x.CopyTo(output, 1);
        y.CopyTo(output, 33);
        return output;
    }

    public static ECParameters ImportX963(ReadOnlySpan<byte> key)
    {
        if (key.Length != ProtocolConstants.PublicKeyLength || key[0] != 0x04)
        {
            throw new CryptographicException("相手のP-256公開鍵が不正です。");
        }

        return new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = key.Slice(1, 32).ToArray(),
                Y = key.Slice(33, 32).ToArray(),
            },
        };
    }

    private static void ValidateMagic(ReadOnlySpan<byte> hello)
    {
        if (!hello[..ProtocolConstants.Magic.Length].SequenceEqual(ProtocolConstants.Magic))
        {
            throw new InvalidDataException("LocalBridge Phase 1ではない接続です。");
        }
    }
}
