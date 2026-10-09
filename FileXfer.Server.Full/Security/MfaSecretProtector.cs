using System.Security.Cryptography;
using System.Text;

namespace FileXfer.Server.Security;

public interface IMfaSecretProtector
{
    string Protect(string secret);

    string Unprotect(string protectedOrLegacySecret);

    bool IsProtected(string value);
}

public sealed class AesGcmMfaSecretProtector : IMfaSecretProtector
{
    private const string VersionPrefix = "v2:";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly byte[] AssociatedData = Encoding.UTF8.GetBytes("FileXfer.MfaSecret.v2");
    private readonly byte[] _key;

    public AesGcmMfaSecretProtector(IConfiguration configuration)
    {
        var encodedKey = configuration["Security:PathEncryptionKey"];
        if (string.IsNullOrWhiteSpace(encodedKey))
            throw new InvalidOperationException("Security:PathEncryptionKey es obligatoria para proteger secretos MFA.");

        try
        {
            _key = Convert.FromBase64String(encodedKey);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Security:PathEncryptionKey debe ser Base64 válido.", ex);
        }

        if (_key.Length != 32)
            throw new InvalidOperationException("Security:PathEncryptionKey debe decodificar exactamente 32 bytes.");
    }

    public bool IsProtected(string value) =>
        value?.StartsWith(VersionPrefix, StringComparison.Ordinal) == true;

    public string Protect(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
            throw new ArgumentException("El secreto MFA no puede ser vacío.", nameof(secret));

        var plaintext = Encoding.UTF8.GetBytes(secret);
        var payload = new byte[NonceSize + TagSize + plaintext.Length];
        var nonce = payload.AsSpan(0, NonceSize);
        var tag = payload.AsSpan(NonceSize, TagSize);
        var ciphertext = payload.AsSpan(NonceSize + TagSize);
        RandomNumberGenerator.Fill(nonce);

        try
        {
            using var aes = new AesGcm(_key, TagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData);
            return VersionPrefix + Convert.ToBase64String(payload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public string Unprotect(string protectedOrLegacySecret)
    {
        if (string.IsNullOrWhiteSpace(protectedOrLegacySecret))
            throw new CryptographicException("El secreto MFA está vacío.");

        // Compatibilidad controlada: secretos Base32 históricos se migran al cifrado v2
        // después de una verificación satisfactoria.
        if (!IsProtected(protectedOrLegacySecret))
            return protectedOrLegacySecret;

        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(protectedOrLegacySecret[VersionPrefix.Length..]);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("El secreto MFA protegido no contiene Base64 válido.", ex);
        }

        if (payload.Length <= NonceSize + TagSize)
            throw new CryptographicException("El secreto MFA protegido está incompleto.");

        var nonce = payload.AsSpan(0, NonceSize);
        var tag = payload.AsSpan(NonceSize, TagSize);
        var ciphertext = payload.AsSpan(NonceSize + TagSize);
        var plaintext = new byte[ciphertext.Length];

        try
        {
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, AssociatedData);
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(payload);
        }
    }
}
