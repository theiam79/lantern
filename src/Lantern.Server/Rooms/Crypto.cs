using System.Security.Cryptography;
using System.Text;

namespace Lantern.Server.Rooms;

internal static class Crypto
{
    private const string Crockford = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>8-char Crockford base32 room code from a CSPRNG (~40 bits).</summary>
    public static string NewRoomCode()
    {
        Span<byte> b = stackalloc byte[8];
        RandomNumberGenerator.Fill(b);
        var sb = new StringBuilder(8);
        for (var i = 0; i < 8; i++) sb.Append(Crockford[b[i] & 31]);
        return sb.ToString();
    }

    /// <summary>256-bit URL-safe token (returned once to the client; only its hash is stored).</summary>
    public static string NewToken()
    {
        Span<byte> b = stackalloc byte[32];
        RandomNumberGenerator.Fill(b);
        return Base64Url(b);
    }

    public static string Hash(string value) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static bool FixedTimeEquals(string? a, string? b)
    {
        if (a is null || b is null) return false;
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(ba, bb);
    }

    public static ulong NewSeed()
    {
        Span<byte> b = stackalloc byte[8];
        RandomNumberGenerator.Fill(b);
        return BitConverter.ToUInt64(b);
    }

    private static string Base64Url(ReadOnlySpan<byte> b) =>
        Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
