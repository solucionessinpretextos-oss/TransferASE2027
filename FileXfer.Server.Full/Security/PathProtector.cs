using System.Security.Cryptography;
using System.Text;

namespace FileXfer.Server.Security;

public interface IPathProtector
{
    string Protect(string plainPath);

    string Unprotect(string protectedPath);
}

public sealed class AesGcmPathProtector : IPathProtector
{
    private const string VersionPrefix = "v2:";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly byte[] AssociatedData = Encoding.UTF8.GetBytes("FileXfer.Path.v2");

    private readonly byte[] _key;
    private readonly string? _legacyKey;

    public AesGcmPathProtector(IConfiguration configuration)
    {
        var encodedKey = configuration["Security:PathEncryptionKey"];
        if (string.IsNullOrWhiteSpace(encodedKey))
        {
            throw new InvalidOperationException(
                "Security:PathEncryptionKey es obligatoria y debe suministrarse como secreto externo.");
        }

        try
        {
            _key = Convert.FromBase64String(encodedKey);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                "Security:PathEncryptionKey debe ser Base64 válido.",
                ex);
        }

        if (_key.Length != 32)
            throw new InvalidOperationException("Security:PathEncryptionKey debe decodificar exactamente 32 bytes.");

        _legacyKey = configuration["Security:LegacyPathEncryptionKey"];
    }

    public string Protect(string plainPath)
    {
        if (string.IsNullOrWhiteSpace(plainPath))
            throw new ArgumentException("La ruta no puede ser vacía.", nameof(plainPath));

        var plaintext = Encoding.UTF8.GetBytes(plainPath);
        var payload = new byte[NonceSize + TagSize + plaintext.Length];
        var nonce = payload.AsSpan(0, NonceSize);
        var tag = payload.AsSpan(NonceSize, TagSize);
        var ciphertext = payload.AsSpan(NonceSize + TagSize);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData);
        CryptographicOperations.ZeroMemory(plaintext);

        return VersionPrefix + Convert.ToBase64String(payload);
    }

    public string Unprotect(string protectedPath)
    {
        if (string.IsNullOrWhiteSpace(protectedPath))
            throw new ArgumentException("La ruta protegida no puede ser vacía.", nameof(protectedPath));

        if (!protectedPath.StartsWith(VersionPrefix, StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(_legacyKey))
            {
                throw new InvalidOperationException(
                    "Se encontró una ruta con cifrado legado, pero Security:LegacyPathEncryptionKey no está configurada.");
            }

            return LegacyDesPathDecryptor.Decrypt(protectedPath, _legacyKey);
        }

        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(protectedPath[VersionPrefix.Length..]);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("La ruta protegida v2 no contiene Base64 válido.", ex);
        }

        if (payload.Length <= NonceSize + TagSize)
            throw new CryptographicException("La ruta protegida v2 está incompleta.");

        var nonce = payload.AsSpan(0, NonceSize);
        var tag = payload.AsSpan(NonceSize, TagSize);
        var ciphertext = payload.AsSpan(NonceSize + TagSize);
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, AssociatedData);

        try
        {
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
