using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace OllamaRouter.App.Converters;

/// <summary>
/// Maps the active wizard step index against the step parameter to highlight completed,
/// current, and upcoming steps using NightCodeForge design tokens.
/// </summary>
public sealed class WizardStepForegroundConverter : IValueConverter
{
    public static readonly WizardStepForegroundConverter Instance = new();

    private static readonly IBrush ActiveBrush = new ImmutableSolidColorBrush(Color.Parse("#A5B4FC"));
    private static readonly IBrush CompletedBrush = new ImmutableSolidColorBrush(Color.Parse("#10B981"));
    private static readonly IBrush UpcomingBrush = new ImmutableSolidColorBrush(Color.Parse("#94A3B8"));

    private WizardStepForegroundConverter()
    {
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int currentStep
            && IntEqualityConverterSupport.TryGetIntParameter(parameter, culture, out var stepIndex))
        {
            if (currentStep == stepIndex)
            {
                return ActiveBrush;
            }

            if (currentStep > stepIndex)
            {
                return CompletedBrush;
            }
        }

        return UpcomingBrush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
