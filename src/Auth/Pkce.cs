using System.Security.Cryptography;
using System.Text;

namespace VanityStudio.Auth;

/// <summary>RFC 7636 helpers for the authorization-code flows.</summary>
public static class Pkce
{
    public static string GenerateVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(64);
        return Base64Url(bytes);
    }

    public static string ComputeChallenge(string verifier)
        => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
