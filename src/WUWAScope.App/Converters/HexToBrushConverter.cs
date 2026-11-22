using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace WUWAScope.App.Converters;

public sealed class HexToBrushConverter : IValueConverter
{
    public static HexToBrushConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && Color.TryParse(hex, out var color))
        {
            return new SolidColorBrush(color);
        }

        return new SolidColorBrush(Color.Parse("#646D7C"));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
