// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: TransferClienteASE/Infraestructura/ManejadorErroresCliente.cs
// Capa: Cliente TransferClienteASE (WPF .NET)
// Propósito: Infraestructura MVVM/cliente: comandos, notificación de propiedades, diálogos y manejo estándar de errores.
// Interacción: Es usada por todos los ViewModels para mantener patrones consistentes de UI.
// Regla de mantenimiento: Cambios aquí son transversales; probar login, selección, carga y actualización.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System;
using System.IO;
using System.Net.Http;
using Grpc.Core;

namespace TransferClienteASE.Infraestructura
{
    public static class ManejadorErroresCliente
    {
        public static string ObtenerMensajeUsuario(Exception ex, string operacion)
        {
            if (ex is OperationCanceledException)
            {
                return "La operación fue cancelada por el usuario.";
            }

            if (ex is InvalidOperationException inv)
            {
                var msg = inv.Message ?? string.Empty;

                if (msg.Contains("ContextoRequerimiento incompleto", StringComparison.OrdinalIgnoreCase))
                {
                    return "No se puede iniciar la carga porque falta información del contexto "
                         + "(ente, auditoría, acto, oficio o requerimiento). "
                         + "Regrese a la pantalla anterior, seleccione nuevamente el requerimiento "
                         + "y vuelva a intentar.";
                }

                if (msg.Contains("No se encontr", StringComparison.OrdinalIgnoreCase)
                    && msg.Contains("TokenJwt", StringComparison.OrdinalIgnoreCase))
                {
                    return "No hay una sesión válida para realizar la operación. "
                         + "Vuelva a iniciar sesión con su e.firma y después intente de nuevo.";
                }

                if (msg.StartsWith("Error al subir", StringComparison.OrdinalIgnoreCase))
                {
                    return msg + Environment.NewLine +
                           "El servidor rechazó el archivo. Si el problema persiste, contacte al área de soporte.";
                }
            }

            if (ex is RpcException rpcEx)
            {
                return rpcEx.StatusCode switch
                {
                    StatusCode.Unauthenticated =>
                        "Su sesión expiró o no es válida. Vuelva a iniciar sesión.",
                    StatusCode.PermissionDenied =>
                        "Su usuario no tiene permisos para realizar esta operación.",
                    StatusCode.Unavailable =>
                        "No fue posible conectar con el servicio de transferencia. "
                        + "Verifique su conexión a la red y vuelva a intentar.",
                    StatusCode.DeadlineExceeded =>
                        "El servidor tardó demasiado en responder. Intente nuevamente en unos minutos.",
                    _ =>
                        $"Ocurrió un error al {operacion} (código gRPC: {rpcEx.StatusCode}). "
                        + "Si el problema persiste, contacte al área de soporte."
                };
            }

            if (ex is HttpRequestException)
            {
                return "No fue posible conectar con el servidor. "
                     + "Verifique su conexión a Internet o a la red institucional.";
            }

            if (ex is IOException ioEx)
            {
                return "No se pudo acceder a uno de los archivos o carpetas involucradas. "
                     + "Verifique que no estén en uso y que tenga permisos suficientes."
                     + Environment.NewLine + ioEx.Message;
            }

            if (ex is UnauthorizedAccessException)
            {
                return "No se tienen permisos suficientes para acceder a uno de los archivos o carpetas seleccionadas.";
            }

            var detalle = ex.Message;
            return $"Ocurrió un error al {operacion}. "
                 + "Si el problema persiste, contacte al área de soporte."
                 + (!string.IsNullOrWhiteSpace(detalle)
                        ? Environment.NewLine + "Detalle técnico: " + detalle
                        : string.Empty);
        }
    }
}
