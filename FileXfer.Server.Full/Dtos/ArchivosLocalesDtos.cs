// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: FileXfer.Server.Full/Dtos/ArchivosLocalesDtos.cs
// Capa: Backend FileXfer.Server.Full (.NET 8)
// Propósito: Contrato de datos del backend: define la forma exacta del JSON que viaja entre servidor y cliente.
// Interacción: Los controladores devuelven estos objetos y el cliente los deserializa; cambiar nombres rompe bindings o mapeos.
// Regla de mantenimiento: Mantener compatibilidad hacia atrás durante updates; agregar campos es más seguro que renombrar o eliminar.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System.Collections.Generic;

namespace FileXfer.Server.Dtos.ArchivosLocales
{
    public sealed class EliminarArchivosLocalesRequestDto
    {
        public string RfcUsuario { get; set; } = string.Empty;
        public int IdRequerimiento { get; set; }
        public int IdColabora { get; set; }
        public List<int> IdsArchivos { get; set; } = new();
    }

    public sealed class EliminarArchivosLocalesResponseDto
    {
        public int Eliminados { get; set; }
    }
}
