using System.Security.Cryptography;

namespace LocalBridge.Protocol;

public static class HkdfSha256
{
    public static byte[] DeriveKey(ReadOnlySpan<byte> inputKeyMaterial, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> info, int outputLength)
    {
        if (outputLength <= 0 || outputLength > 255 * 32)
        {
            throw new ArgumentOutOfRangeException(nameof(outputLength));
        }

        var effectiveSalt = salt.IsEmpty ? new byte[32] : salt.ToArray();
        byte[] pseudoRandomKey;
        using (var extract = new HMACSHA256(effectiveSalt))
        {
            pseudoRandomKey = extract.ComputeHash(inputKeyMaterial.ToArray());
        }

        try
        {
            var output = new byte[outputLength];
            var previous = Array.Empty<byte>();
            var written = 0;
            byte counter = 1;

            using var expand = new HMACSHA256(pseudoRandomKey);
            while (written < outputLength)
            {
                var material = new byte[previous.Length + info.Length + 1];
                previous.CopyTo(material, 0);
                info.CopyTo(material.AsSpan(previous.Length));
                material[^1] = counter;
                previous = expand.ComputeHash(material);
                var take = Math.Min(previous.Length, outputLength - written);
                previous.AsSpan(0, take).CopyTo(output.AsSpan(written));
                written += take;
                counter++;
            }

            CryptographicOperations.ZeroMemory(previous);
            return output;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pseudoRandomKey);
        }
    }
}

