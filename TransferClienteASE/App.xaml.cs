// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: TransferClienteASE/App.xaml.cs
// Capa: Cliente TransferClienteASE (WPF .NET)
// Propósito: Inicialización de aplicación WPF: prepara servicios globales y muestra la primera ventana.
// Interacción: Arranca login y establece recursos de aplicación.
// Regla de mantenimiento: Excepciones aquí impiden abrir el cliente; mantener simple.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System.Windows;
using System.Net;

namespace TransferClienteASE
{
    public partial class App : Application
    {
        static App()
        {
            ServicePointManager.SecurityProtocol =
                SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
        }
    }
}
