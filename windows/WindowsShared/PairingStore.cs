namespace LocalBridge.WindowsShared;

public sealed record PairedIPhone(Guid DeviceId, string DisplayName, string PublicKeyBase64, DateTimeOffset PairedAt)
{
    public byte[] PublicKey => Convert.FromBase64String(PublicKeyBase64);
}

public static class PairingStore
{
    public static PairedIPhone? Load() => AtomicJsonFile.Read<PairedIPhone>(AppPaths.PairingFile);

    public static void Save(PairedIPhone device) => AtomicJsonFile.Write(AppPaths.PairingFile, device);

    public static void Delete()
    {
        if (File.Exists(AppPaths.PairingFile)) File.Delete(AppPaths.PairingFile);
    }
}

