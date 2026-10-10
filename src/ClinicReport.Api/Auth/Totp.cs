using System.Security.Cryptography;
using System.Text;

namespace ClinicReport.Api.Auth;

/// <summary>
/// Time-based one-time passwords (RFC 6238: HMAC-SHA1, 6 digits, 30-second steps), the codes shown by
/// Google Authenticator, Microsoft Authenticator, 1Password and similar apps.
/// </summary>
public static class Totp
{
    public const int StepSeconds = 30;
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string NewSecret() => Base32Encode(RandomNumberGenerator.GetBytes(20));

    public static string OtpAuthUri(string issuer, string account, string secret) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}" +
        $"?secret={secret}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits=6&period={StepSeconds}";

    public static long StepAt(DateTimeOffset time) => time.ToUnixTimeSeconds() / StepSeconds;

    public static string Code(string secret, long step)
    {
        var counter = BitConverter.GetBytes(step);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(counter);
        var hash = HMACSHA1.HashData(Base32Decode(secret), counter);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    /// <summary>
    /// Checks a code against the current step and one step either side (clock drift). Returns the matched
    /// step, or null. Steps at or before <paramref name="lastUsedStep"/> are refused so a code works once.
    /// </summary>
    public static long? Verify(string secret, string? code, DateTimeOffset now, long lastUsedStep)
    {
        code = new string((code ?? "").Where(char.IsDigit).ToArray());
        if (code.Length != 6)
            return null;
        var current = StepAt(now);
        for (var step = current - 1; step <= current + 1; step++)
        {
            if (step <= lastUsedStep)
                continue;
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(secret, step)), Encoding.ASCII.GetBytes(code)))
                return step;
        }
        return null;
    }

    public static string Base32Encode(byte[] data)
    {
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Base32Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0)
            sb.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static byte[] Base32Decode(string text)
    {
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in text.TrimEnd('=').ToUpperInvariant())
        {
            var value = Base32Alphabet.IndexOf(c);
            if (value < 0)
                throw new FormatException("Not base32.");
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }
        return [.. bytes];
    }
}
