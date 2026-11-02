using System.Security.Cryptography;
using System.Text;

namespace SuperMOOO.Publishing.OAuth;

public static class Pkce
{
    public static string NewState() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string NewVerifier() => Base64Url(RandomNumberGenerator.GetBytes(48));

    public static string ChallengeS256(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    // TikTok yêu cầu SHA-256 dạng hex thay vì base64url.
    public static string ChallengeHex(string verifier) => Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
