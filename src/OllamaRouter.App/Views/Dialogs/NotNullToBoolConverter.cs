using System.Globalization;
using Avalonia.Data.Converters;

namespace OllamaRouter.App.Views.Dialogs;

/// <summary>
/// Maps any non-null value to <c>true</c> and null to <c>false</c> (used for
/// <c>IsVisible="{Binding ..., Converter=NotNullToBool}"</c> on nullable panels).
/// </summary>
public sealed class NotNullToBoolConverter : IValueConverter
{
    /// <summary>The shared instance (XAML <c>{x:Static}</c>).</summary>
    public static readonly NotNullToBoolConverter Instance = new();

    private NotNullToBoolConverter()
    {
    }

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null;

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
