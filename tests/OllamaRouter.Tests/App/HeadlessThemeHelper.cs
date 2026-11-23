using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace OllamaRouter.Tests.App;

/// <summary>
/// Applies the production NightCodeForge Obsidian &amp; Indigo theme to a single
/// window/user control under test — the SAME dictionary App.axaml merges in production via
/// avares://ollamarouter-app/Themes/NightCodeForgeTheme.axaml, PLUS the four Target*Brush
/// values from App.axaml (mirrored here in code — in production they live in
/// Application.Resources, which unshown headless windows never reach: see the class docs of
/// the headless fixture in PreloadDialogLayoutTests.cs / task #170 spec §7.2).
///
/// The avares load happens immediately on <see cref="Apply"/> (adding a <see cref="ResourceInclude"/>
/// registers it as a resource-provider owner) and requires a LIVE Application with the asset
/// services — i.e. a headless session. The layout test classes that construct views directly
/// (deliberately avoiding HeadlessUnitTestSession) get the programmatic mirror of the same
/// token set instead (task #170 spec §7.2 fallback) so {DynamicResource} still resolves for
/// the whole subtree. All Avalonia property reads must stay inside the headless session
/// dispatch (thread-affinity).
/// </summary>
internal static class HeadlessThemeHelper
{
    public static readonly Uri ThemeSource = new("avares://ollamarouter-app/Themes/NightCodeForgeTheme.axaml");

    public static void Apply(StyledElement root)
    {
        root.Resources.MergedDictionaries.Add(LoadTheme());

        // The four Target*Brush values from App.axaml (in production they live in
        // Application.Resources, which unshown headless windows never reach).
        root.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            ["TargetLocalBrush"] = new SolidColorBrush(Color.Parse("#3F9EFF")),
            ["TargetRemoteBrush"] = new SolidColorBrush(Color.Parse("#34E0A1")),
            ["TargetCloudBrush"] = new SolidColorBrush(Color.Parse("#C48BFF")),
            ["TargetNeutralBrush"] = new SolidColorBrush(Color.Parse("#7A8AA0"))
        });
    }

    private static ResourceDictionary LoadTheme()
    {
        try
        {
            // NOTE: the (Uri?)null cast disambiguates the ResourceInclude(Uri?) /
            // ResourceInclude(IServiceProvider) constructors. Reading Loaded forces the
            // avares load NOW (before the include is added to MergedDictionaries) so a
            // missing asset service (no live headless Application) is caught here.
            var include = new ResourceInclude((Uri?)null) { Source = ThemeSource };
            return (ResourceDictionary)include.Loaded;
        }
        catch (InvalidOperationException)
        {
            // "Could not create IAssetLoader : maybe Application.RegisterServices() wasn't
            // called?" — this test class runs without a headless session: fall back to the
            // programmatic mirror of the same token set (task #170 spec §7.2 fallback).
            return ProgrammaticThemeMirror();
        }
    }

    /// <summary>
    /// Programmatic mirror of the token surface of
    /// <c>avares://ollamarouter-app/Themes/NightCodeForgeTheme.axaml</c> (the raw-color
    /// brushes, the Semi.Avalonia semantic overrides and the radii — the control-level
    /// Button*/TextBox*/ComboBox*/… overrides are omitted: only Semi.Avalonia's own
    /// ControlThemes consume them, and the test app applies no SemiTheme at all).
    /// Built with IMMUTABLE brushes: they are shared across session boundaries and must not
    /// capture a thread owner (the TargetVisualConverters convention).
    /// </summary>
    private static ResourceDictionary ProgrammaticThemeMirror()
    {
        static IBrush B(string hex) => new ImmutableSolidColorBrush(Color.Parse(hex));

        return new ResourceDictionary
        {
            // Named brushes for views.
            ["NightCodeBgCanvas"] = B("#0B0D13"),
            ["NightCodeBgPrimary"] = B("#0F1117"),
            ["NightCodeBgSurface"] = B("#181B24"),
            ["NightCodeBgCard"] = B("#202433"),
            ["NightCodeBgCardHover"] = B("#272C3D"),
            ["NightCodeBgElevated"] = B("#242938"),
            ["NightCodeBgInput"] = B("#12141C"),
            ["NightCodeBorderAccent"] = B("#596366F1"),
            ["NightCodeAccentIndigoLightText"] = B("#A5B4FC"),
            ["NightCodeAccentAmber"] = B("#F59E0B"),
            ["NightCodeAccentCrimsonLightText"] = B("#F87171"),

            // Semi.Avalonia semantic overrides — backgrounds.
            ["SemiColorBackground0"] = B("#0B0D13"),
            ["SemiColorBackground1"] = B("#0F1117"),
            ["SemiColorBackground2"] = B("#181B24"),
            ["SemiColorBackground3"] = B("#202433"),
            ["SemiColorBackground4"] = B("#242938"),
            ["SemiColorNavBackground"] = B("#0F1117"),

            // Text.
            ["SemiColorText0"] = B("#F8FAFC"),
            ["SemiColorText1"] = B("#CBD5E1"),
            ["SemiColorText2"] = B("#94A3B8"),
            ["SemiColorText3"] = B("#64748B"),
            ["SemiColorDisabledText"] = B("#475569"),

            // Borders & fills.
            ["SemiColorBorder"] = B("#2D3345"),
            ["SemiColorFocusBorder"] = B("#6366F1"),

            // Primary (Indigo).
            ["SemiColorPrimary"] = B("#6366F1"),
            ["SemiColorPrimaryPointerover"] = B("#4F46E5"),
            ["SemiColorPrimaryActive"] = B("#4338CA"),
            ["SemiColorPrimaryDisabled"] = B("#312E81"),
            ["SemiColorPrimaryLight"] = WithOpacity(Color.Parse("#6366F1"), 0.15),

            // Success.
            ["SemiColorSuccess"] = B("#10B981"),
            ["SemiColorSuccessPointerover"] = B("#059669"),
            ["SemiColorSuccessActive"] = B("#047857"),
            ["SemiColorSuccessDisabled"] = B("#064E3B"),
            ["SemiColorSuccessLight"] = WithOpacity(Color.Parse("#10B981"), 0.15),

            // Warning (amber).
            ["SemiColorWarning"] = B("#F59E0B"),
            ["SemiColorWarningPointerover"] = B("#D97706"),
            ["SemiColorWarningActive"] = B("#B45309"),
            ["SemiColorWarningLight"] = WithOpacity(Color.Parse("#F59E0B"), 0.15),

            // Information (indigo).
            ["SemiColorInformation"] = B("#6366F1"),
            ["SemiColorInformationPointerover"] = B("#4F46E5"),
            ["SemiColorInformationActive"] = B("#4338CA"),
            ["SemiColorInformationDisabled"] = B("#312E81"),
            ["SemiColorInformationLight"] = WithOpacity(Color.Parse("#6366F1"), 0.15),

            // Danger (crimson).
            ["SemiColorDanger"] = B("#EF4444"),
            ["SemiColorDangerPointerover"] = B("#DC2626"),
            ["SemiColorDangerActive"] = B("#B91C1C"),
            ["SemiColorDangerLight"] = WithOpacity(Color.Parse("#EF4444"), 0.15),

            // Links.
            ["SemiColorLink"] = B("#6366F1"),
            ["SemiColorLinkPointerover"] = B("#818CF8"),
            ["SemiColorLinkActive"] = B("#A5B4FC"),
            ["SemiColorLinkVisited"] = B("#6366F1"),

            // Radii.
            ["SemiBorderRadiusSmall"] = new CornerRadius(4),
            ["SemiBorderRadiusMedium"] = new CornerRadius(8),
            ["SemiBorderRadiusLarge"] = new CornerRadius(12),

            // Window defaults.
            ["WindowDefaultBackground"] = B("#0B0D13"),
            ["WindowDefaultForeground"] = B("#F8FAFC"),
            ["DefaultForeground"] = B("#F8FAFC"),
        };
    }

    /// <summary>
    /// The Light token variants of the theme carry a brush Opacity (e.g. 0.15) over the
    /// full-alpha color; the mirror bakes that opacity into the alpha channel (the test app
    /// applies no SemiTheme, so only the app XAML's own references consume these keys).
    /// </summary>
    private static IBrush WithOpacity(Color rgb, double opacity)
    {
        var alpha = (byte)Math.Round(opacity * 255);
        return new ImmutableSolidColorBrush(new Color(alpha, rgb.R, rgb.G, rgb.B));
    }
}
