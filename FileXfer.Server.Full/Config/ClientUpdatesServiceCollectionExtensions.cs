// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: FileXfer.Server.Full/Config/ClientUpdatesServiceCollectionExtensions.cs
// Capa: Backend FileXfer.Server.Full (.NET 8)
// Propósito: Extensión de configuración del backend: registra servicios/opciones necesarios para una capacidad concreta.
// Interacción: Program.cs invoca este archivo para centralizar configuraciones repetibles.
// Regla de mantenimiento: No esconder reglas de negocio aquí; debe limitarse a wiring de dependencias.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using FileXfer.Server.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FileXfer.Server.Config
{
    public static class ClientUpdatesServiceCollectionExtensions
    {
        public static IServiceCollection AddClientUpdates(
            this IServiceCollection services,
            IConfiguration configuration)
        {
            services.Configure<ClientUpdatesOptions>(
                configuration.GetSection("ClientUpdates"));

            return services;
        }
    }
}
