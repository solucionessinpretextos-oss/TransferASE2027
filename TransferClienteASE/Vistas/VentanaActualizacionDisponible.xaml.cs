using System;
using System.Windows;
using TransferClienteASE.Servicios;

namespace TransferClienteASE.Vistas
{
    /// <summary>
    /// Compatibilidad visual para instalaciones antiguas. La descarga ZIP heredada fue retirada
    /// porque no incluía hash ni firma. El flujo vigente se ejecuta desde la pantalla de inicio
    /// mediante ServicioVersion y un manifiesto RSA-PSS firmado.
    /// </summary>
    public partial class VentanaActualizacionDisponible : Window
    {
        private readonly VersionInfo _info;
        private readonly string _versionLocal;

        public VentanaActualizacionDisponible(VersionInfo info, string versionLocal)
        {
            InitializeComponent();
            _info = info ?? throw new ArgumentNullException(nameof(info));
            _versionLocal = versionLocal ?? string.Empty;

            txtTitulo.Text = "Nueva versión de Transfer ASE disponible";
            txtVersiones.Text = $"Versión instalada: {_versionLocal}    |    Nueva versión: {_info.Version}";
            txtNotas.Text = string.IsNullOrWhiteSpace(_info.Notas)
                ? "No se proporcionaron notas de la versión."
                : _info.Notas;

            if (_info.Obligatoria)
                btnOmitir.Visibility = Visibility.Collapsed;
        }

        private void btnOmitir_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void btnActualizar_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                "Este canal de actualización fue retirado porque no admitía firma criptográfica. " +
                "Cierre y vuelva a abrir Transfer ASE para usar el actualizador seguro.",
                "Actualización segura requerida",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            DialogResult = false;
            Close();
        }
    }
}
