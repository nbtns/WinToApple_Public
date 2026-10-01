using System.Security.Cryptography;
using LocalBridge.Protocol;

namespace LocalBridge.WindowsShared;

public sealed class WindowsDeviceIdentity : IDisposable
{
    public required Guid DeviceId { get; init; }
    public required string DisplayName { get; init; }
    public required ECDsa SigningKey { get; init; }
    public byte[] PublicKey => Phase1Handshake.ExportX963(SigningKey.ExportParameters(false));
    public void Dispose() => SigningKey.Dispose();
}

public static class WindowsIdentityStore
{
    public static WindowsDeviceIdentity LoadOrCreate()
    {
        AppPaths.EnsureDirectories();
        var stored = AtomicJsonFile.Read<StoredIdentity>(AppPaths.IdentityFile);
        if (stored is not null)
        {
            var key = ECDsa.Create();
            var privateKey = Dpapi.Unprotect(Convert.FromBase64String(stored.ProtectedPrivateKey));
            try
            {
                key.ImportPkcs8PrivateKey(privateKey, out _);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(privateKey);
            }
            return new WindowsDeviceIdentity
            {
                DeviceId = stored.DeviceId,
                DisplayName = stored.DisplayName,
                SigningKey = key,
            };
        }

        var createdKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pkcs8 = createdKey.ExportPkcs8PrivateKey();
        try
        {
            AtomicJsonFile.Write(AppPaths.IdentityFile, new StoredIdentity(
                Guid.NewGuid(),
                SanitizeDisplayName(Environment.MachineName),
                Convert.ToBase64String(Dpapi.Protect(pkcs8))));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
        return LoadOrCreate();
    }

    private static string SanitizeDisplayName(string value)
    {
        var cleaned = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return string.IsNullOrEmpty(cleaned) ? "Windows PC" : cleaned[..Math.Min(cleaned.Length, 64)];
    }

    private sealed record StoredIdentity(Guid DeviceId, string DisplayName, string ProtectedPrivateKey);
}

