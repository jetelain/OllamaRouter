using Avalonia.Media;
using Avalonia.Media.Immutable;
using OllamaRouter.App.Converters;

namespace OllamaRouter.Tests.App;

/// <summary>
/// Direct unit asserts for the <see cref="PresetChipConverter"/> (task #149): the
/// "Add External Provider" dialog's active-preset chip highlight — an exact
/// (case-insensitive) match of the bound <c>ActivePresetId</c> with the chip's
/// <c>ConverterParameter</c> paints the soft accent, anything else (custom/none, another
/// chip, missing parameter) stays transparent. One-way only: <c>ConvertBack</c> throws
/// <see cref="NotSupportedException"/> (Avalonia 12 has no <c>Binding.DoNothing</c>).
/// </summary>
public sealed class PresetChipConverterTests
{
    [Fact]
    public void Convert_MatchingIds_ReturnsTheSoftAccent()
    {
        var brush = Assert.IsType<ImmutableSolidColorBrush>(
            PresetChipConverter.Instance.Convert("groq", typeof(IBrush), "groq", null));

        Assert.NotEqual(byte.MaxValue, brush.Color.A); // the SOFT highlight (semi-transparent)
        Assert.NotEqual(0, brush.Color.A);
    }

    [Fact]
    public void Convert_MatchIsCaseInsensitive()
    {
        var brush = Assert.IsType<ImmutableSolidColorBrush>(
            PresetChipConverter.Instance.Convert("GROQ", typeof(IBrush), "groq", null));

        Assert.NotEqual(0, brush.Color.A);
    }

    [Theory]
    [InlineData("groq", "openrouter")]
    [InlineData(null, "openrouter")]
    [InlineData("openrouter", null)]
    public void Convert_NoActivePresetOrDifferentChip_StaysTransparent(string? activeId, string? chipId)
    {
        var brush = Assert.IsType<ImmutableSolidColorBrush>(
            PresetChipConverter.Instance.Convert(activeId, typeof(IBrush), chipId, null));

        Assert.Equal(0, brush.Color.A);
    }

    [Fact]
    public void ConvertBack_Throws()
    {
        Assert.Throws<NotSupportedException>(() =>
            PresetChipConverter.Instance.ConvertBack(null, typeof(string), "groq", null));
    }
}
