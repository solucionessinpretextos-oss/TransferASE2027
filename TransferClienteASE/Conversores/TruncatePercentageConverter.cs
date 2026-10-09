// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: TransferClienteASE/Conversores/TruncatePercentageConverter.cs
// Capa: Cliente TransferClienteASE (WPF .NET)
// Propósito: Convertidor WPF: transforma valores para presentación en XAML sin modificar el modelo original.
// Interacción: Se declara como recurso y se usa en bindings para adaptar bool, porcentajes u otros formatos.
// Regla de mantenimiento: Debe ser puro y sin efectos secundarios; nunca llamar servicios desde un converter.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

using System;
using System.Globalization;
using System.Windows.Data;

namespace TransferClienteASE.Conversores
{
    public sealed class TruncatePercentageConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
                return 0;

            if (value is double d)
                return (int)d;

            if (value is float f)
                return (int)f;

            if (value is int i)
                return i;

            if (double.TryParse(value.ToString(), NumberStyles.Any, culture, out var parsed))
                return (int)parsed;

            return 0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }
}
