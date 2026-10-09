// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: FileXfer.Server.Full/Security/Encryption64.cs
// Capa: Backend FileXfer.Server.Full (.NET 8)
// Propósito: Utilidad de seguridad/compatibilidad: encapsula operaciones criptográficas o validaciones reutilizadas.
// Interacción: Es llamada por servicios que necesitan proteger o leer datos sensibles persistidos en base.
// Regla de mantenimiento: No cambiar algoritmos o llaves sin plan de migración de datos históricos.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace FileXfer.Server.Security
{
    // Compatibilidad de solo lectura. Nunca usar para datos nuevos; AesGcmPathProtector
    // escribe exclusivamente el formato v2 autenticado.
    internal static class LegacyDesPathDecryptor
    {
        private static readonly byte[] Iv = { 0x12, 0x24, 0x56, 0x78, 0x90, 0xAB, 0xCD, 0xEF };

        public static string Decrypt(string stringToDecrypt, string encryptionKey)
        {
            if (string.IsNullOrEmpty(stringToDecrypt))
                return string.Empty;

            if (string.IsNullOrEmpty(encryptionKey) || encryptionKey.Length < 8)
                throw new ArgumentException("La llave de encriptación debe tener al menos 8 caracteres.", nameof(encryptionKey));

            var keyBytes = Encoding.UTF8.GetBytes(encryptionKey.Substring(0, 8));
            var inputByteArray = Convert.FromBase64String(stringToDecrypt);

            using var des = DES.Create();
            des.Mode = CipherMode.CBC;
            des.Padding = PaddingMode.PKCS7;
            using var ms = new MemoryStream();
            using (var cs = new CryptoStream(ms, des.CreateDecryptor(keyBytes, Iv), CryptoStreamMode.Write))
            {
                cs.Write(inputByteArray, 0, inputByteArray.Length);
                cs.FlushFinalBlock();
            }

            return Encoding.UTF8.GetString(ms.ToArray());
        }
    }
}
