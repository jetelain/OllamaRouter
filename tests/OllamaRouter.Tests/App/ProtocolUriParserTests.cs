using OllamaRouter.App;
using OllamaRouter.Client.Protocol;

namespace OllamaRouter.Tests.App;

/// <summary>
/// Task #107: unit tests of the pure <see cref="ProtocolUriParser"/> (URI → launch options) and
/// the pure <see cref="ProtocolRegistrationService.CreateWindowsPlan"/> /
/// <see cref="ProtocolRegistrationService.CreateLinuxPlan"/> registration-plan builders.
/// Deterministic and side-effect-free: NO registry, file, PATH, or environment mutation — the
/// executors (<c>Register</c>/<c>Unregister</c>) are never invoked.
/// </summary>
public class ProtocolUriParserTests
{
    // ------------------------------------------------------------------
    //  A. URI → mode mapping
    // ------------------------------------------------------------------

    [Fact]
    public void Parse_BareScheme_LaunchesDashboardWithoutTab()
    {
        var options = ProtocolUriParser.Parse(["ollamarouter://"]);
        Assert.False(options.Wizard);
        Assert.Null(options.InitialTabIndex);
    }

    [Theory]
    [InlineData("ollamarouter://open")]
    [InlineData("ollamarouter://dashboard")]
    public void Parse_DashboardHosts_LaunchDashboard(string uri)
    {
        var options = ProtocolUriParser.Parse([uri]);
        Assert.False(options.Wizard);
        Assert.Null(options.InitialTabIndex);
    }

    [Theory]
    [InlineData("ollamarouter://wizard")]
    [InlineData("ollamarouter://setup")]
    public void Parse_WizardHosts_LaunchWizard(string uri)
    {
        var options = ProtocolUriParser.Parse([uri]);
        Assert.True(options.Wizard);
        Assert.Null(options.InitialTabIndex);
    }

    [Theory]
    [InlineData("ollamarouter://open/")]
    [InlineData("ollamarouter://dashboard/")]
    [InlineData("ollamarouter://wizard/")]
    [InlineData("ollamarouter://setup/")]
    public void Parse_TrailingSlashes_MatchUnslashedBehavior(string uri)
    {
        var options = ProtocolUriParser.Parse([uri]);
        var expectedWizard = uri.Contains("wizard") || uri.Contains("setup");
        Assert.Equal(expectedWizard, options.Wizard);
        Assert.Null(options.InitialTabIndex);
    }

    [Fact]
    public void Parse_UppercaseSchemeAndHost_IsCaseInsensitive()
    {
        var options = ProtocolUriParser.Parse(["OLLAMAROUTER://OPEN/"]);
        Assert.False(options.Wizard);
        Assert.Null(options.InitialTabIndex);
    }

    [Fact]
    public void Parse_MixedCaseWizardHost_IsCaseInsensitive()
    {
        var options = ProtocolUriParser.Parse(["ollamarouter://Wizard"]);
        Assert.True(options.Wizard);
        Assert.Null(options.InitialTabIndex);
    }

    [Fact]
    public void Parse_UppercaseSetupHostWithUppercaseTabQuery_ParsesBoth()
    {
        var options = ProtocolUriParser.Parse(["OLLAMAROUTER://SETUP?TAB=SETTINGS"]);
        Assert.True(options.Wizard);
        Assert.Equal(4, options.InitialTabIndex);
    }

    [Fact]
    public void Parse_UnknownHost_FallsBackToDashboard()
    {
        var options = ProtocolUriParser.Parse(["ollamarouter://nope"]);
        Assert.False(options.Wizard);
        Assert.Null(options.InitialTabIndex);
    }

    [Theory]
    [InlineData("http://ollamarouter.local")]
    [InlineData("https://ollamarouter.example.com/open")]
    [InlineData("foo://bar")]
    [InlineData("ollamarouter.local")]
    [InlineData("--verbose")]
    [InlineData("some-file.json")]
    public void Parse_NonProtocolArgs_AreIgnored(string arg)
    {
        var options = ProtocolUriParser.Parse([arg]);
        Assert.False(options.Wizard);
        Assert.Null(options.InitialTabIndex);
    }

    [Fact]
    public void Parse_ProtocolUriAfterUnrelatedArgs_IsStillRecognized()
    {
        var options = ProtocolUriParser.Parse(["--verbose", "log.txt", "ollamarouter://wizard"]);
        Assert.True(options.Wizard);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("--something-else")]
    public void Parse_NoProtocolArgs_DefaultsToDashboard(string? singleArg)
    {
        var options = ProtocolUriParser.Parse(singleArg is null ? null : [singleArg]);
        Assert.False(options.Wizard);
        Assert.Null(options.InitialTabIndex);
    }

    // ------------------------------------------------------------------
    //  B. ?tab= query
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("monitor", 0)]
    [InlineData("sessions", 1)]
    [InlineData("fleet", 2)]
    [InlineData("cartography", 3)]
    [InlineData("settings", 4)]
    [InlineData("MONITOR", 0)]
    [InlineData("Sessions", 1)]
    [InlineData("Fleet", 2)]
    [InlineData("CARTOGRAPHY", 3)]
    [InlineData("SETTINGS", 4)]
    public void Parse_TabQuery_MapsToTabIndex(string tab, int expectedIndex)
    {
        var options = ProtocolUriParser.Parse([$"ollamarouter://open?tab={tab}"]);
        Assert.False(options.Wizard);
        Assert.Equal(expectedIndex, options.InitialTabIndex);
    }

    [Fact]
    public void Parse_ProtocolUriWithoutHostAndTabQuery_MapsTab()
    {
        var options = ProtocolUriParser.Parse(["ollamarouter://?tab=fleet"]);
        Assert.False(options.Wizard);
        Assert.Equal(2, options.InitialTabIndex);
    }

    [Theory]
    [InlineData("ollamarouter://open?tab=nope")]
    [InlineData("ollamarouter://open?tab=")]
    [InlineData("ollamarouter://open?other=fleet")]
    [InlineData("ollamarouter://open")]
    public void Parse_UnknownOrMissingTabValue_YieldsNullTab(string uri)
    {
        var options = ProtocolUriParser.Parse([uri]);
        Assert.Null(options.InitialTabIndex);
    }

    [Fact]
    public void Parse_TabKeyIsCaseInsensitive()
    {
        var options = ProtocolUriParser.Parse(["ollamarouter://open?Tab=Cartography"]);
        Assert.Equal(3, options.InitialTabIndex);
    }

    [Fact]
    public void Parse_PercentEncodedTabValue_IsDecoded()
    {
        var options = ProtocolUriParser.Parse(["ollamarouter://open?tab=sett%69ngs"]); // "settings"
        Assert.Equal(4, options.InitialTabIndex);
    }

    [Fact]
    public void Parse_MultipleTabPairs_FirstWins()
    {
        var options = ProtocolUriParser.Parse(["ollamarouter://open?tab=fleet&tab=settings"]);
        Assert.Equal(2, options.InitialTabIndex);
    }

    [Fact]
    public void Parse_TabQuerySurfacedOnWizardUri_AppIgnoresItInWizardMode()
    {
        var options = ProtocolUriParser.Parse(["ollamarouter://wizard?tab=fleet"]);
        Assert.True(options.Wizard);
        Assert.Equal(2, options.InitialTabIndex);
    }

    // ------------------------------------------------------------------
    //  C. --wizard flag semantics (flag wins over any URI host)
    // ------------------------------------------------------------------

    [Fact]
    public void Parse_WizardFlagAlone_LaunchesWizard()
    {
        var options = ProtocolUriParser.Parse(["--wizard"]);
        Assert.True(options.Wizard);
        Assert.Null(options.InitialTabIndex);
    }

    [Fact]
    public void Parse_WizardFlagIsCaseInsensitive()
    {
        var options = ProtocolUriParser.Parse(["--WIZARD"]);
        Assert.True(options.Wizard);
    }

    [Fact]
    public void Parse_WizardFlagWithDashboardUri_FlagWins()
    {
        var options = ProtocolUriParser.Parse(["--wizard", "ollamarouter://open"]);
        Assert.True(options.Wizard);
    }

    [Fact]
    public void Parse_WizardFlagWithDashboardUriAndTab_FlagWinsAndTabStillSurfaced()
    {
        var options = ProtocolUriParser.Parse(["--wizard", "ollamarouter://open?tab=settings"]);
        Assert.True(options.Wizard);
        Assert.Equal(4, options.InitialTabIndex);
    }

    [Fact]
    public void Parse_WizardFlagAmongOtherArgs_LaunchesWizard()
    {
        var options = ProtocolUriParser.Parse(["--verbose", "--wizard", "log.txt"]);
        Assert.True(options.Wizard);
    }

    // ------------------------------------------------------------------
    //  D. Registration payload generation (pure builders — the injectable
    //     sink seam; the executors are never invoked)
    // ------------------------------------------------------------------

    [Fact]
    public void CreateWindowsPlan_ProducesExactSpecdValues()
    {
        const string exe = @"C:\Users\me\AppData\Local\OllamaRouter\ollamarouter-app.exe";
        var plan = ProtocolRegistrationService.CreateWindowsPlan(exe);

        Assert.Equal(@"Software\Classes\ollamarouter", plan.SchemeKeyPath);
        Assert.Equal("URL:OllamaRouter Protocol", plan.DisplayName);
        Assert.Equal(string.Empty, plan.UrlProtocolValue);
        Assert.Equal(@"Software\Classes\ollamarouter\DefaultIcon", plan.IconKeyPath);
        Assert.Equal($"\"{exe}\",0", plan.IconValue);
        Assert.Equal(@"Software\Classes\ollamarouter\shell\open\command", plan.CommandKeyPath);
        Assert.Equal($"\"{exe}\" \"%1\"", plan.CommandValue);
    }

    [Fact]
    public void CreateLinuxPlan_ProducesExactDesktopEntryPathAndContent()
    {
        const string appPath = "/home/me/.ollamarouter/ollamarouter-app";
        var plan = ProtocolRegistrationService.CreateLinuxPlan(appPath, "/home/me", xdgMimeOnPath: _ => true);

        Assert.Equal("/home/me/.local/share/applications/ollamarouter.desktop", plan.DesktopEntryPath);
        Assert.Equal("x-scheme-handler/ollamarouter", plan.MimeType);
        Assert.Contains("[Desktop Entry]", plan.DesktopEntryContent);
        Assert.Contains($"Exec=\"{appPath}\" %u", plan.DesktopEntryContent);
        Assert.Contains("MimeType=x-scheme-handler/ollamarouter;", plan.DesktopEntryContent);
        Assert.Contains("Type=Application", plan.DesktopEntryContent);
    }

    [Fact]
    public void CreateLinuxPlan_XdgMimePresent_PlanCarriesXdgMimeCommand()
    {
        var plan = ProtocolRegistrationService.CreateLinuxPlan(
            "/home/me/.ollamarouter/ollamarouter-app", "/home/me", xdgMimeOnPath: _ => true);
        Assert.Equal("xdg-mime default ollamarouter.desktop x-scheme-handler/ollamarouter", plan.XdgMimeCommand);
    }

    [Fact]
    public void CreateLinuxPlan_XdgMimeAbsent_PlanCarriesNoCommand()
    {
        var plan = ProtocolRegistrationService.CreateLinuxPlan(
            "/home/me/.ollamarouter/ollamarouter-app", "/home/me", xdgMimeOnPath: _ => false);
        Assert.Null(plan.XdgMimeCommand);
    }
}
