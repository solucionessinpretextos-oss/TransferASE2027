// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: FileXfer.Server.Full/Services/TotpService.cs
// Capa: Backend FileXfer.Server.Full (.NET 8)
// Propósito: Servicio de backend: concentra reglas de negocio, acceso a BuDi, seguridad auxiliar o integración interna.
// Interacción: Es invocado por controladores, endpoints gRPC u otros servicios; suele ser el punto donde se conectan DTOs, SQL y reglas de negocio.
// Regla de mantenimiento: Cualquier cambio aquí puede afectar varios flujos del cliente. Validar acto normal, acto 16 y acto 21 antes de publicar.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System.Security.Cryptography;
using System.Text;

namespace FileXfer.Server.Services;

public sealed class TotpService
{
    public string GenerateSecretBase32(int bytes = 20)
    {
        var key = RandomNumberGenerator.GetBytes(bytes);
        return Base32Encode(key);
    }

    public bool Verify(string base32Secret, string code, int timestep = 30, int digits = 6, int pastSteps = 1, int futureSteps = 1)
    {
        if (string.IsNullOrWhiteSpace(base32Secret) ||
            timestep <= 0 ||
            digits is < 6 or > 8 ||
            code is null ||
            code.Length != digits ||
            !code.All(char.IsAsciiDigit))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        try
        {
            var supplied = Encoding.ASCII.GetBytes(code);
            for (int w = -Math.Max(0, pastSteps); w <= Math.Max(0, futureSteps); w++)
            {
                var expected = Encoding.ASCII.GetBytes(
                    Compute(base32Secret, now + w * timestep, timestep, digits));
                if (CryptographicOperations.FixedTimeEquals(expected, supplied))
                    return true;
            }
        }
        catch (FormatException)
        {
            return false;
        }

        return false;
    }

    private static string Compute(string base32Secret, long nowSeconds, int timestep, int digits)
    {
        var key = Base32Decode(base32Secret);
        var counter = (ulong)(nowSeconds / timestep);
        Span<byte> msg = stackalloc byte[8];
        for (int i = 7; i >= 0; i--) { msg[i] = (byte)(counter & 0xff); counter >>= 8; }
        using var hmac = new HMACSHA1(key);
        var hash = hmac.ComputeHash(msg.ToArray());
        int offset = hash[^1] & 0x0f;
        int bin = ((hash[offset] & 0x7f) << 24) | ((hash[offset + 1] & 0xff) << 16) | ((hash[offset + 2] & 0xff) << 8) | (hash[offset + 3] & 0xff);
        var mod = (int)Math.Pow(10, digits);
        return (bin % mod).ToString().PadLeft(digits, '0');
    }

    private static string Base32Encode(byte[] data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        int charCount = (int)Math.Ceiling(data.Length / 5d) * 8;
        var result = new char[charCount];
        int buffer = data[0], next = 1, bitsLeft = 8, index = 0;
        while (index < charCount)
        {
            if (bitsLeft < 5)
            {
                if (next < data.Length)
                {
                    buffer <<= 8;
                    buffer |= data[next++] & 0xff;
                    bitsLeft += 8;
                }
                else
                {
                    int pad = 5 - bitsLeft;
                    buffer <<= pad;
                    bitsLeft += pad;
                }
            }
            result[index++] = alphabet[(buffer >> (bitsLeft - 5)) & 0x1f];
            bitsLeft -= 5;
        }
        return new string(result);
    }

    private static byte[] Base32Decode(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        int bitBuffer = 0, bitCount = 0;
        var bytes = new List<byte>(input.Length * 5 / 8);
        foreach (var ch in input.ToUpperInvariant())
        {
            if (char.IsWhiteSpace(ch) || ch == '=') continue;
            int idx = alphabet.IndexOf(ch);
            if (idx < 0) throw new FormatException("Invalid Base32");
            bitBuffer = (bitBuffer << 5) | idx;
            bitCount += 5;
            if (bitCount >= 8)
            {
                bytes.Add((byte)((bitBuffer >> (bitCount - 8)) & 0xff));
                bitCount -= 8;
            }
        }
        return bytes.ToArray();
    }
}
