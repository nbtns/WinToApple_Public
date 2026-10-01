using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalBridge.Protocol;

namespace LocalBridge.WindowsShared;

public sealed record PairingRequest(
    string WindowsDeviceId,
    string WindowsDisplayName,
    string WindowsPublicKey,
    long Timestamp,
    string Nonce,
    string Mac);

public sealed record PairingResponse(
    bool Success,
    string Message,
    string? IPhoneDeviceId,
    string? IPhoneDisplayName,
    string? IPhonePublicKey,
    string RequestNonce,
    string Mac);

public sealed class PairingClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<PairedIPhone> PairAsync(string code, CancellationToken cancellationToken)
    {
        var secret = Base32.Decode(code);
        try
        {
            var service = await new MdnsDiscovery().FindAsync(
                MdnsDiscovery.PairingServiceType,
                TimeSpan.FromSeconds(10),
                cancellationToken).ConfigureAwait(false)
                ?? throw new IOException("iPhoneが見つかりません。iPhoneでLocalBridgeの機器登録画面を開いてください。");

            using var identity = WindowsIdentityStore.LoadOrCreate();
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var publicKey = Convert.ToBase64String(identity.PublicKey);
            var unsigned = CanonicalRequest(identity.DeviceId, identity.DisplayName, publicKey, timestamp, nonce);
            var mac = ComputeMac(secret, unsigned);
            var request = new PairingRequest(
                identity.DeviceId.ToString("D").ToLowerInvariant(),
                identity.DisplayName,
                publicKey,
                timestamp,
                nonce,
                mac);

            using var client = new TcpClient();
            await client.ConnectAsync(service.Address, service.Port, cancellationToken).ConfigureAwait(false);
            await using var stream = client.GetStream();
            await stream.WriteAsync("LBP2PAIR"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            var json = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
            await BinaryStream.WriteUInt32Async(stream, checked((uint)json.Length), cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(json, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

            var responseLength = await BinaryStream.ReadUInt32Async(stream, cancellationToken).ConfigureAwait(false);
            if (responseLength is 0 or > 64 * 1024) throw new InvalidDataException("iPhoneの機器登録応答が不正です。");
            var responseBytes = new byte[responseLength];
            await BinaryStream.ReadExactlyAsync(stream, responseBytes, cancellationToken).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<PairingResponse>(responseBytes, JsonOptions)
                ?? throw new InvalidDataException("iPhoneの機器登録応答を読めませんでした。");
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromBase64String(response.Mac),
                    Convert.FromBase64String(ComputeMac(secret, CanonicalResponse(response)))))
            {
                throw new CryptographicException("iPhoneの機器登録応答を確認できませんでした。");
            }
            if (!string.Equals(response.RequestNonce, nonce, StringComparison.Ordinal))
                throw new CryptographicException("古い機器登録応答を拒否しました。");
            if (!response.Success)
                throw new InvalidOperationException(response.Message);
            if (!Guid.TryParse(response.IPhoneDeviceId, out var iphoneId) ||
                string.IsNullOrWhiteSpace(response.IPhoneDisplayName) ||
                string.IsNullOrWhiteSpace(response.IPhonePublicKey))
                throw new InvalidDataException("iPhoneの機器情報が不足しています。");
            var key = Convert.FromBase64String(response.IPhonePublicKey);
            using (var verifier = ECDsa.Create(Phase1Handshake.ImportX963(key))) { }

            var paired = new PairedIPhone(iphoneId, response.IPhoneDisplayName, response.IPhonePublicKey, DateTimeOffset.UtcNow);
            PairingStore.Save(paired);
            return paired;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    public static string CanonicalRequest(Guid deviceId, string name, string publicKey, long timestamp, string nonce) =>
        $"LBPAIR1\n{deviceId:D}\n{name}\n{publicKey}\n{timestamp}\n{nonce}";

    public static string CanonicalResponse(PairingResponse response) =>
        $"LBPAIR1-RESPONSE\n{(response.Success ? 1 : 0)}\n{response.Message}\n{response.IPhoneDeviceId ?? string.Empty}\n{response.IPhoneDisplayName ?? string.Empty}\n{response.IPhonePublicKey ?? string.Empty}\n{response.RequestNonce}";

    public static string ComputeMac(byte[] secret, string canonical)
    {
        using var hmac = new HMACSHA256(secret);
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical)));
    }
}

