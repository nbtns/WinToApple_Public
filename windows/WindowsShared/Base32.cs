namespace LocalBridge.WindowsShared;

public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static byte[] Decode(string encoded)
    {
        var cleaned = new string(encoded
            .Where(character => !char.IsWhiteSpace(character) && character != '-')
            .Select(char.ToUpperInvariant)
            .ToArray());
        if (cleaned.Length != 26)
            throw new FormatException("機器登録コードは26文字で入力してください。");

        var output = new List<byte>(cleaned.Length * 5 / 8);
        var buffer = 0;
        var bits = 0;
        foreach (var character in cleaned)
        {
            var value = Alphabet.IndexOf(character);
            if (value < 0) throw new FormatException("機器登録コードに使用できない文字があります。");
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                output.Add((byte)(buffer >> bits));
                buffer &= (1 << bits) - 1;
            }
        }
        if (output.Count != 16 || bits != 2 || buffer != 0)
            throw new FormatException("機器登録コードの末尾が正しくありません。");
        return output.ToArray();
    }
}
