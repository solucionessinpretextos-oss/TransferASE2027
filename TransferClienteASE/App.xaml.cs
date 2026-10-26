// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: TransferClienteASE/App.xaml.cs
// Capa: Cliente TransferClienteASE (WPF .NET)
// Propósito: Inicialización de aplicación WPF: prepara servicios globales y muestra la primera ventana.
// Interacción: Arranca login y establece recursos de aplicación.
// Regla de mantenimiento: Excepciones aquí impiden abrir el cliente; mantener simple.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System;
using System.Linq;
using System.Windows;
using System.Net;
using TransferClienteASE.Servicios;

namespace TransferClienteASE
{
    public partial class App : Application
    {
        static App()
        {
            ServicePointManager.SecurityProtocol =
                SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;

            // En la publicación autónoma qpdf viaja dentro del EXE. Se materializa
            // antes de construir los servicios para que la validación de PDF use
            // exactamente ese binario y no dependa del PATH del equipo.
            EmbeddedRuntimeAssets.PrepareQpdfIfEmbedded();
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            if (e.Args.Any(arg => string.Equals(arg, "--self-test", StringComparison.OrdinalIgnoreCase)))
            {
                var exitCode = EmbeddedRuntimeAssets.RunSelfTest();
                Environment.ExitCode = exitCode;
                Shutdown(exitCode);
                return;
            }

            base.OnStartup(e);
        }
    }
}
