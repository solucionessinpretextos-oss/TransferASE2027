// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: FileXfer.Server.Full/Dtos/Auth/CertificadoInfoDto.cs
// Capa: Backend FileXfer.Server.Full (.NET 8)
// Propósito: Contrato de datos del backend: define la forma exacta del JSON que viaja entre servidor y cliente.
// Interacción: Los controladores devuelven estos objetos y el cliente los deserializa; cambiar nombres rompe bindings o mapeos.
// Regla de mantenimiento: Mantener compatibilidad hacia atrás durante updates; agregar campos es más seguro que renombrar o eliminar.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

namespace FileXfer.Server.Dtos.Auth
{
    public sealed class CertificadoInfoDto
    {
        public string? Rfc { get; set; }
        public string? Curp { get; set; }
        public string? NombreCompleto { get; set; }
        public string? Serie { get; set; }
        public DateTime NotBefore { get; set; }
        public DateTime NotAfter { get; set; }
        public string? Emisor { get; set; }
        public string? Subject { get; set; }
    }
}
