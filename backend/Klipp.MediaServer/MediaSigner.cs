using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Klipp.MediaServer;

public sealed class MediaSigner(MediaOptions options, TimeProvider time)
{
    private readonly byte[] _key = Convert.FromBase64String(options.SigningKeyBase64);

    public string Create(Guid ownerId, Guid clipId, string asset, TimeSpan lifetime)
    {
        var expires = time.GetUtcNow().Add(lifetime).ToUnixTimeSeconds();
        var signature = Sign(ownerId, clipId, asset, expires);
        return $"{options.PublicBaseUrl.TrimEnd('/')}/v1/assets/{ownerId:D}/{clipId:D}/{asset}?expires={expires}&signature={signature}";
    }

    public bool Verify(Guid ownerId, Guid clipId, string asset, string expiresValue, string signature)
    {
        if (ownerId == Guid.Empty || clipId == Guid.Empty || asset is not ("video" or "poster")
            || !long.TryParse(expiresValue, NumberStyles.None, CultureInfo.InvariantCulture, out var expires)
            || signature.Length != 64 || !signature.All(char.IsAsciiHexDigit)) return false;
        var now = time.GetUtcNow().ToUnixTimeSeconds();
        if (expires <= now || expires > now + 300) return false;
        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(signature), Convert.FromHexString(Sign(ownerId, clipId, asset, expires)));
    }

    private string Sign(Guid ownerId, Guid clipId, string asset, long expires) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(_key,
            Encoding.UTF8.GetBytes($"klipp-media-v1\n{ownerId:D}\n{clipId:D}\n{asset}\n{expires.ToString(CultureInfo.InvariantCulture)}")));
}
