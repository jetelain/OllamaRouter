using System.Globalization;
using Avalonia.Data.Converters;

namespace OllamaRouter.App.Views.Dialogs;

/// <summary>
/// Maps the nullable per-target uninstall outcome (<c>Succeeded</c>) to a visibility flag:
/// <see cref="SuccessInstance"/> shows only on success (true), <see cref="FailureInstance"/>
/// shows only on an explicit failure (false; null = not executed yet → hidden either way).
/// Used by the Uninstall Model dialog's per-row result TextBlocks (task #126).
/// </summary>
public sealed class SuccessVisibilityConverter : IValueConverter
{
    private readonly bool _showOnSuccess;

    private SuccessVisibilityConverter(bool showOnSuccess)
    {
        _showOnSuccess = showOnSuccess;
    }

    /// <summary>Shared instance for the SUCCESS result text (visible when <c>Succeeded == true</c>).</summary>
    public static readonly SuccessVisibilityConverter SuccessInstance = new(showOnSuccess: true);

    /// <summary>Shared instance for the FAILURE result text (visible when <c>Succeeded == false</c>).</summary>
    public static readonly SuccessVisibilityConverter FailureInstance = new(showOnSuccess: false);

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && b == _showOnSuccess;

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
