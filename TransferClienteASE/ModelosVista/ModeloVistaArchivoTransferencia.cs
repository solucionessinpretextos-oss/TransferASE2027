// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: TransferClienteASE/ModelosVista/ModeloVistaArchivoTransferencia.cs
// Capa: Cliente TransferClienteASE (WPF .NET)
// Propósito: ViewModel MVVM: coordina estado de pantalla, comandos, validaciones y llamadas a servicios del cliente.
// Interacción: La vista XAML se enlaza a sus propiedades/comandos; los servicios HTTP/gRPC ejecutan operaciones externas.
// Regla de mantenimiento: Respetar INotifyPropertyChanged y no bloquear UI thread; errores aquí se manifiestan como pantallas congeladas o bindings rotos.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System;
using System.IO;
using TransferClienteASE.Infraestructura;
using TransferClienteASE.Modelos;

namespace TransferClienteASE.ModelosVista
{
    public sealed class ModeloVistaArchivoTransferencia : NotificacionPropiedadBase
    {
        private string _rutaCompleta = string.Empty;
        private double _progreso;
        private EstadoTransferenciaArchivo _estado;

        public string RutaCompleta
        {
            get => _rutaCompleta;
            set
            {
                if (EstablecerValor(ref _rutaCompleta, value))
                {
                    NotificarCambio(nameof(NombreArchivo));
                    NotificarCambio(nameof(TamanioBytes));
                    NotificarCambio(nameof(TamanioDescripcion));
                    NotificarCambio(nameof(UbicacionOriginal));
                }
            }
        }

        public string NombreArchivo => Path.GetFileName(RutaCompleta);

        public string UbicacionOriginal => Path.GetDirectoryName(RutaCompleta) ?? string.Empty;

        public long TamanioBytes =>
            File.Exists(RutaCompleta) ? new FileInfo(RutaCompleta).Length : 0;

        public string TamanioDescripcion =>
            $"{TamanioBytes / 1024d / 1024d:0.00} MB";

        public double Progreso
        {
            get => _progreso;
            set => EstablecerValor(ref _progreso, value);
        }

        public EstadoTransferenciaArchivo Estado
        {
            get => _estado;
            set => EstablecerValor(ref _estado, value);
        }

        public ModeloVistaArchivoTransferencia(string ruta)
        {
            RutaCompleta = ruta ?? throw new ArgumentNullException(nameof(ruta));
            Estado = EstadoTransferenciaArchivo.Pendiente;
            Progreso = 0;
        }
    }
}
