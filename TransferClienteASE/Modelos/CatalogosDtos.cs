// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: TransferClienteASE/Modelos/CatalogosDtos.cs
// Capa: Cliente TransferClienteASE (WPF .NET)
// Propósito: Modelo/DTO del cliente: transporta datos entre servicios, ViewModels y vistas WPF.
// Interacción: Se llena desde JSON del backend o selección del usuario; los ViewModels lo usan como estado de negocio.
// Regla de mantenimiento: No renombrar propiedades enlazadas en XAML sin actualizar bindings.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System;
using System.Collections.Generic;

namespace TransferClienteASE.Modelos
{
    public sealed class AnioDto
    {
        public int Id { get; set; }
        public string Nombre { get; set; } = "";
    }

    public sealed class EnteDto
    {
        public int IdColabora { get; set; }
        public string EnteFiscalizado { get; set; } = "";

        public string IdAuditoria { get; set; } = "";

        public string Auditoria { get; set; } = "";
        public string Nombre { get; set; } = "";
    }

    public sealed class ActoDto
    {
        public int ID_Acto { get; set; }
        public string Nom_Acto { get; set; } = "";
    }

    public sealed class OficioDto
    {
        public int IdOficio { get; set; }
        public string Descripcion { get; set; } = "";
    }

    public sealed class RequerimientoDto
    {
        public int IdRequerimiento { get; set; }
        public string Clave { get; set; } = "";
        public string Descripcion { get; set; } = "";
        public int Total_Documentos { get; set; }
    }

    public sealed class AnexoCedulaDto
    {
        public int IdAnexo { get; set; }
        public string AnexoNombre { get; set; } = "";
        public int NumResultadosCedula { get; set; }
        public override string ToString() => AnexoNombre;
    }

    public sealed class ResultadoCedulaDto
    {
        public int IdResultado { get; set; }
        public int IdAnexo { get; set; }
        public int NumResultado { get; set; }
        public string Resumen { get; set; } = "";
        public int NumeroEvidencias { get; set; }
    }

    public sealed class PaginadoDto<T>
    {
        public int Total { get; set; }
        public IReadOnlyList<T> Items { get; set; } = Array.Empty<T>();
    }
}
