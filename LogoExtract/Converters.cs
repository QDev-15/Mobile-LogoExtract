using System.Globalization;

namespace LogoExtract;

public sealed class InvertedBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => !(value is true);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => !(value is true);
}
