// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: FileXfer.Server.Full/Dtos/CombosDtos.cs
// Capa: Backend FileXfer.Server.Full (.NET 8)
// Propósito: Contrato de datos del backend: define la forma exacta del JSON que viaja entre servidor y cliente.
// Interacción: Los controladores devuelven estos objetos y el cliente los deserializa; cambiar nombres rompe bindings o mapeos.
// Regla de mantenimiento: Mantener compatibilidad hacia atrás durante updates; agregar campos es más seguro que renombrar o eliminar.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System;
using System.Collections.Generic;


// ============================================================================
//  DTOs compartidos con el cliente Transfer ASE
// ============================================================================
//  Estos modelos definen el contrato JSON entre FileXfer.Server y el cliente WPF.
//  Cuando se agregue una propiedad, debe verificarse que el cliente tenga una propiedad
//  equivalente o que el serializador pueda ignorarla sin romper compatibilidad.
// ============================================================================
namespace FileXfer.Server.Dtos.Combos
{
    public sealed class AnioDto
    {
        public int Id { get; set; }
        public string Nombre { get; set; } = string.Empty;

        public int Valor { get; internal set; }
        public string Texto { get; internal set; } = string.Empty;
        public int Anio { get; internal set; }
    }

    public sealed class EnteDto
    {
        public int IdColabora { get; set; }

        public string IdAuditoria { get; set; } = string.Empty;

        public string EnteFiscalizado { get; set; } = string.Empty;

        public string Auditoria { get; set; } = string.Empty;

        public string Nombre { get; set; } = string.Empty;
    }

    public sealed class AuditoriaDto
    {
        public int IdAuditoria { get; set; }
        public string Descripcion { get; set; } = string.Empty;
    }

    public sealed class ActoDto
    {
        public int ID_Acto { get; set; }

        public string Nom_Acto { get; set; } = string.Empty;
    }

    public sealed class OficioDto
    {
        public int IdOficio { get; set; }

        public string Numero { get; set; } = string.Empty;

        public string Descripcion { get; set; } = string.Empty;
    }

    public sealed class RequerimientoDto
    {
        public int IdRequerimiento { get; set; }

        public string Clave { get; set; } = string.Empty;

        public string Descripcion { get; set; } = string.Empty;

        public int Total_Documentos { get; set; }

        public int NumRequerimiento { get; set; }

        public DateTime? FechaPlazo { get; set; }
    }

    public sealed class ArchivoLocalDto
    {
        public int IdArchivoLocal { get; set; }

        public string NombreArchivo { get; set; } = string.Empty;

        public string Extension { get; set; } = string.Empty;

        public string Tamano { get; set; } = string.Empty;

        public DateTime FechaAlta { get; set; }

        public string RutaFisica { get; set; } = string.Empty;
    }

    public sealed class AnexoCedulaDto
    {
        public int IdAnexo { get; set; }

        public string AnexoNombre { get; set; } = string.Empty;

        public int NumResultadosCedula { get; set; }
    }

    public sealed class ResultadoCedulaDto
    {
        public int IdResultado { get; set; }

        public int? IdAnexo { get; set; }

        public int NumResultado { get; set; }

        public string Resumen { get; set; } = string.Empty;

        public int NumeroEvidencias { get; set; }
    }


    /// <summary>
    /// Resultado mostrado en el acto 21 Informe Individual de Auditoría.
    ///
    /// IdResultado SIEMPRE corresponde a BUZ_Resultados_CedulaEvaluacion.ID.
    /// IdResultadoActo16 es solo referencia histórica a BUZ_Resultados_Cedulas.ID cuando
    /// el resultado proviene del acto 16. Si es NULL, el resultado fue capturado manualmente
    /// durante el acto 21.
    /// </summary>
    public sealed class ResultadoInformeDto
    {
        public int IdResultado { get; set; }

        public int? IdOficio { get; set; }

        public int? IdAuditoria { get; set; }

        public int? IdColabora { get; set; }

        public int NumResultado { get; set; }

        public string TextoResultado { get; set; } = string.Empty;

        public string ApartadoRubro { get; set; } = string.Empty;

        public int NumeroEvidencias { get; set; }

        public int? IdResultadoActo16 { get; set; }

        public bool EsCapturadoManual { get; set; }

        public string OrigenResultado { get; set; } = string.Empty;
    }

    public sealed class PagedResult<T>
    {
        public IReadOnlyList<T> Items { get; init; } = Array.Empty<T>();
        public int Total { get; init; }

        public PagedResult() { }

        public PagedResult(IReadOnlyList<T> items, int total)
        {
            Items = items ?? Array.Empty<T>();
            Total = total;
        }
    }
}
