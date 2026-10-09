// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: FileXfer.Server.Full/Dtos/ActualizacionesDtos.cs
// Capa: Backend FileXfer.Server.Full (.NET 8)
// Propósito: Contrato de datos del backend: define la forma exacta del JSON que viaja entre servidor y cliente.
// Interacción: Los controladores devuelven estos objetos y el cliente los deserializa; cambiar nombres rompe bindings o mapeos.
// Regla de mantenimiento: Mantener compatibilidad hacia atrás durante updates; agregar campos es más seguro que renombrar o eliminar.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

namespace FileXfer.Server.Dtos.Actualizaciones
{
    public sealed class ActualizacionClienteDto
    {
        public string ClienteId { get; set; } = string.Empty;

        public string Version { get; set; } = string.Empty;

        public string UrlDescarga { get; set; } = string.Empty;

        public string Notas { get; set; } = string.Empty;

        public bool Obligatoria { get; set; }
    }
}
