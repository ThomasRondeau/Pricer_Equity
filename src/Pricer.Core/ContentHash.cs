using System.Security.Cryptography;
using System.Text.Json;

namespace Pricer.Core;

/// <summary>SHA-256 of an object's JSON form: equal inputs give equal hashes across processes.</summary>
public static class ContentHash
{
    public static string Of(object value)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value, value.GetType());
        return Convert.ToHexStringLower(SHA256.HashData(json));
    }
}
