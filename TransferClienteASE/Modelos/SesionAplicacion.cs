// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: TransferClienteASE/Modelos/SesionAplicacion.cs
// Capa: Cliente TransferClienteASE (WPF .NET)
// Propósito: Modelo/DTO del cliente: transporta datos entre servicios, ViewModels y vistas WPF.
// Interacción: Se llena desde JSON del backend o selección del usuario; los ViewModels lo usan como estado de negocio.
// Regla de mantenimiento: No renombrar propiedades enlazadas en XAML sin actualizar bindings.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System;

namespace TransferClienteASE.Modelos
{
    public static class SesionAplicacion
    {
        public static string Rfc { get; set; } = string.Empty;
        public static string NombreUsuario { get; set; } = string.Empty;

        public static string TokenJwt { get; set; } = string.Empty;

        public static string RefreshToken { get; set; } = string.Empty;

        public static DateTimeOffset? RefreshTokenExpiraUtc { get; set; }

        public static string DeviceId { get; set; } = string.Empty;

        public static bool EstaAutenticado => !string.IsNullOrWhiteSpace(TokenJwt);

        public static bool RefreshTokenValido
        {
            get
            {
                if (string.IsNullOrWhiteSpace(RefreshToken))
                    return false;

                if (!RefreshTokenExpiraUtc.HasValue)
                    return true;

                return RefreshTokenExpiraUtc.Value > DateTimeOffset.UtcNow.AddMinutes(-1);
            }
        }

        public static void Limpiar()
        {
            Rfc = string.Empty;
            TokenJwt = string.Empty;
            RefreshToken = string.Empty;
            RefreshTokenExpiraUtc = null;
            DeviceId = string.Empty;
        }
    }
}
