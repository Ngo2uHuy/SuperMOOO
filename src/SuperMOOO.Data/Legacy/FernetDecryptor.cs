using System.Security.Cryptography;
using System.Text;

namespace SuperMOOO.Data.Legacy;

/// <summary>Giải mã token Fernet (AES-128-CBC + HMAC-SHA256) của bản Python cũ, không cần gói ngoài.</summary>
public static class FernetDecryptor
{
    public static string Decrypt(string key, string token)
    {
        var keyBytes = FromUrlSafe(key.Trim());
        if (keyBytes.Length != 32) throw new FormatException("Khoá Fernet phải dài 32 byte.");
        var data = FromUrlSafe(token.Trim());
        if (data.Length < 1 + 8 + 16 + 16 + 32 || data[0] != 0x80) throw new FormatException("Token Fernet không hợp lệ.");

        var signed = data.AsSpan(0, data.Length - 32);
        var expected = HMACSHA256.HashData(keyBytes.AsSpan(0, 16), signed);
        if (!CryptographicOperations.FixedTimeEquals(expected, data.AsSpan(data.Length - 32)))
            throw new CryptographicException("Sai khoá hoặc dữ liệu Fernet đã bị sửa.");

        using var aes = Aes.Create();
        aes.Key = keyBytes[16..];
        var iv = data.AsSpan(9, 16).ToArray();
        var cipher = data.AsSpan(25, data.Length - 25 - 32);
        return Encoding.UTF8.GetString(aes.DecryptCbc(cipher, iv, PaddingMode.PKCS7));
    }

    private static byte[] FromUrlSafe(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight((s.Length + 3) / 4 * 4, '='));
    }
}
