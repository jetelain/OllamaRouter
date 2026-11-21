using OllamaRouter.App.ViewModels;
using OllamaRouter.Endpoints.RouterApi.Targets;

namespace OllamaRouter.Tests.App;

/// <summary>
/// <see cref="ExternalProviderAddFormViewModel"/> tests (task #149): the dedicated
/// "Add External Provider" form — the pinned-<c>type=openai</c> wire construction (blank
/// key/provider → null; optional key), the base-URL rules (full URL OR bare
/// <c>host[:port]</c> normalized to <c>http://…</c>), the shared name rules, and the
/// preset prefill behavior (prefill, suggested-name tracking — a user-typed name is NEVER
/// overwritten — Custom clearing, highlight drop on manual deviation, fully editable
/// prefills).
/// </summary>
public sealed class ExternalProviderAddFormViewModelTests
{
    private static ExternalProviderAddFormViewModel ValidForm() => new()
    {
        Name = "GroqBox",
        BaseUrl = "https://api.groq.com/openai/v1/",
    };

    // -- Preset table -----------------------------------------------------------------

    [Fact]
    public void ProviderPreset_All_MatchesTheWizardPrecedents()
    {
        Assert.Equal(
            [
                new ProviderPreset("openrouter", "OpenRouter", "https://openrouter.ai/api"),
                new ProviderPreset("groq", "Groq", "https://api.groq.com/openai"),
                new ProviderPreset("openai", "OpenAI", "https://api.openai.com/v1"),
            ],
            ProviderPreset.All);

        Assert.Null(ProviderPreset.Find(null));
        Assert.Null(ProviderPreset.Find(""));
        Assert.Null(ProviderPreset.Find("unknown"));
        Assert.Equal("OpenRouter", ProviderPreset.Find("OPENROUTER")?.Label); // case-insensitive
    }

    // -- Wire construction -----------------------------------------------------------

    [Fact]
    public void BuildAddRequest_Valid_BuildsOpenAiRequest()
    {
        var form = ValidForm();
        form.Name = "  GroqBox  ";
        form.ApiKey = "gsk_key";
        form.Provider = "  Groq  ";

        var (request, error) = form.BuildAddRequest();

        Assert.Null(error);
        Assert.IsType<AddTargetRequest>(request);
        Assert.Equal("GroqBox", request.Name); // trimmed
        Assert.Equal("https://api.groq.com/openai/v1", request.Url); // trailing / stripped
        Assert.Equal(1, request.MaxParallel); // NOT exposed in this dialog — wire default
        Assert.True(request.TestConnectionFirst); // default
        Assert.Equal("openai", request.Type); // pinned
        Assert.Equal("gsk_key", request.ApiKey);
        Assert.Equal("Groq", request.Provider); // trimmed
        Assert.Null(request.HardwareHint);
        Assert.Null(request.Models);
        Assert.Null(request.PowerWatts);
        Assert.Null(request.SshUser); // daemon-side probing is Ollama-protocol-only
        Assert.Null(request.SshPort);
    }

    [Fact]
    public void BuildAddRequest_BlankKeyAndProvider_SendNull()
    {
        var form = ValidForm();
        form.ApiKey = "   ";
        form.Provider = "";

        var (request, error) = form.BuildAddRequest();

        Assert.Null(error);
        Assert.Null(request.ApiKey); // the key is optional on the wire
        Assert.Null(request.Provider);
    }

    [Fact]
    public void BuildAddRequest_TestConnectionFirst_FalseWhenUnchecked()
    {
        var form = ValidForm();
        form.TestConnectionFirst = false;

        var (request, error) = form.BuildAddRequest();

        Assert.Null(error);
        Assert.False(request.TestConnectionFirst);
    }

    // -- Base URL normalization ---------------------------------------------------------

    [Theory]
    [InlineData("gpu-box:8080", "http://gpu-box:8080")]
    [InlineData("127.0.0.1:8000", "http://127.0.0.1:8000")]
    [InlineData("host", "http://host")]
    [InlineData("gpu-box:8080/v1/", "http://gpu-box:8080/v1")]
    [InlineData("https://openrouter.ai/api/", "https://openrouter.ai/api")]
    [InlineData("http://127.0.0.1:11435", "http://127.0.0.1:11435")]
    [InlineData("  https://api.groq.com/openai  ", "https://api.groq.com/openai")]
    public void BuildAddRequest_BaseUrl_FullUrlOrHostPort_Normalizes(string input, string expected)
    {
        var form = ValidForm();
        form.BaseUrl = input;

        var (request, error) = form.BuildAddRequest();

        Assert.Null(error);
        Assert.Equal(expected, request.Url);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("ftp://host/path")]
    [InlineData("https://")]
    public void BuildAddRequest_BaseUrl_BlankMalformedOrNonHttp_Rejects(string input)
    {
        var form = ValidForm();
        form.BaseUrl = input;

        Assert.Equal("The target URL must be an absolute http(s) URI.", form.BuildAddRequest().Error);
    }

    // -- Name validation (shared rules) ---------------------------------------------------

    [Fact]
    public void BuildAddRequest_Name_SharesTheFormValidationRules()
    {
        var form = ValidForm();
        form.Name = "";
        Assert.Equal("The target name must not be empty.", form.BuildAddRequest().Error);

        form.Name = new string('a', 65);
        Assert.Equal("The target name must be 64 characters or fewer.", form.BuildAddRequest().Error);

        form.Name = "Bad/Name";
        Assert.Equal("The target name must not contain '/', '\\' or ':'.", form.BuildAddRequest().Error);

        form.Name = "Cloud";
        Assert.Equal("\"Cloud\" is reserved for the cloud overflow pseudo-target.", form.BuildAddRequest().Error);
    }

    // -- Presets ------------------------------------------------------------------------

    [Theory]
    [InlineData("openrouter", "OpenRouter", "https://openrouter.ai/api")]
    [InlineData("groq", "Groq", "https://api.groq.com/openai")]
    [InlineData("openai", "OpenAI", "https://api.openai.com/v1")]
    public void ApplyPreset_PrefillsBaseUrlProviderAndSuggestsName(string id, string label, string url)
    {
        var form = new ExternalProviderAddFormViewModel();

        form.ApplyPresetCommand.Execute(id);

        Assert.Equal(url, form.BaseUrl);
        Assert.Equal(label, form.Provider);
        Assert.Equal(id, form.ActivePresetId);
        Assert.Equal(label, form.Name); // blank name → the suggestion
    }

    [Fact]
    public void ApplyPreset_UserTypedName_IsNeverOverwritten()
    {
        var form = new ExternalProviderAddFormViewModel { Name = "MyBox" };

        form.ApplyPresetCommand.Execute("groq");

        Assert.Equal("MyBox", form.Name); // untouched
        Assert.Equal("https://api.groq.com/openai", form.BaseUrl);
        Assert.Equal("Groq", form.Provider);
    }

    [Fact]
    public void ApplyPreset_SwitchingPresets_UpdatesTheSuggestedName()
    {
        var form = new ExternalProviderAddFormViewModel();
        form.ApplyPresetCommand.Execute("openrouter");
        Assert.Equal("OpenRouter", form.Name);

        form.ApplyPresetCommand.Execute("groq");

        Assert.Equal("Groq", form.Name); // the suggestion tracked the switch
        Assert.Equal("https://api.groq.com/openai", form.BaseUrl);
        Assert.Equal("groq", form.ActivePresetId);
    }

    [Fact]
    public void ApplyPreset_Custom_ClearsPrefills_KeepsNameAndApiKey_DropsTheHighlight()
    {
        var form = new ExternalProviderAddFormViewModel();
        form.ApplyPresetCommand.Execute("openrouter");
        form.ApiKey = "sk-123";

        form.ApplyPresetCommand.Execute("");

        Assert.Equal(string.Empty, form.BaseUrl);
        Assert.Equal(string.Empty, form.Provider);
        Assert.Null(form.ActivePresetId);
        Assert.Equal("OpenRouter", form.Name); // untouched
        Assert.Equal("sk-123", form.ApiKey); // untouched
    }

    [Fact]
    public void ApplyPreset_NullId_IsCustom()
    {
        var form = new ExternalProviderAddFormViewModel();
        form.ApplyPresetCommand.Execute("groq");

        form.ApplyPresetCommand.Execute(null);

        Assert.Equal(string.Empty, form.BaseUrl);
        Assert.Equal(string.Empty, form.Provider);
        Assert.Null(form.ActivePresetId);
    }

    [Theory]
    [InlineData("openrouter", "https://other.example/v1")]
    [InlineData("groq", "gpu-box:8080")]
    [InlineData("openai", "https://api.openai.com/v1/")]
    public void ManualBaseUrlEdit_DropsTheActivePresetHighlight(string id, string url)
    {
        var form = new ExternalProviderAddFormViewModel();
        form.ApplyPresetCommand.Execute(id);
        Assert.Equal(id, form.ActivePresetId);

        form.BaseUrl = url;

        Assert.Null(form.ActivePresetId); // the pair no longer matches the chip
    }

    [Fact]
    public void ReTypingThePresetUrl_AfterADrop_DoesNotRelightTheChip()
    {
        var form = new ExternalProviderAddFormViewModel();
        form.ApplyPresetCommand.Execute("groq");
        form.BaseUrl = "https://api.groq.com/openai/v1/"; // deviate → drop
        Assert.Null(form.ActivePresetId);

        form.BaseUrl = "https://api.groq.com/openai"; // retype the preset URL exactly

        Assert.Null(form.ActivePresetId); // the chip relights only on an explicit re-apply
    }

    [Fact]
    public void ManualProviderEdit_DropsTheActivePresetHighlight()
    {
        var form = new ExternalProviderAddFormViewModel();
        form.ApplyPresetCommand.Execute("openai");

        form.Provider = "Self-hosted";

        Assert.Null(form.ActivePresetId);
        Assert.Equal("https://api.openai.com/v1", form.BaseUrl); // still editable & intact
    }

    [Fact]
    public void PrefilledValues_StayFullyEditable_BuildAddRequestUsesTheEditedValues()
    {
        var form = new ExternalProviderAddFormViewModel();
        form.ApplyPresetCommand.Execute("groq");
        form.BaseUrl = "https://api.groq.com/openai/v2"; // user edits the prefill
        form.Provider = "Groq-v2";
        form.ApiKey = "gsk_key";

        var (request, error) = form.BuildAddRequest();

        Assert.Null(error);
        Assert.Equal("https://api.groq.com/openai/v2", request.Url);
        Assert.Equal("Groq-v2", request.Provider);
        Assert.Equal("gsk_key", request.ApiKey);
    }

    [Fact]
    public void ApplyPreset_ClearsAReportedValidationError()
    {
        var form = new ExternalProviderAddFormViewModel { BaseUrl = "https://api.groq.com/openai" };
        form.Name = ""; // invalid
        form.ReportError("The target name must not be empty.");
        Assert.True(form.HasValidationError);

        form.ApplyPresetCommand.Execute("openrouter");

        Assert.Null(form.ValidationErrorMessage);
        Assert.False(form.HasValidationError);
    }

    [Fact]
    public void ReportError_SetsHasValidationError_AndEditingFieldsClearsIt()
    {
        var form = ValidForm();
        form.ReportError("boom");
        Assert.True(form.HasValidationError);

        form.Name = "OtherBox";
        Assert.Null(form.ValidationErrorMessage);
        Assert.False(form.HasValidationError);

        form.ReportError("boom");
        form.BaseUrl = "http://127.0.0.1:9000";
        Assert.Null(form.ValidationErrorMessage);

        form.ReportError("boom");
        form.ApiKey = "k";
        Assert.Null(form.ValidationErrorMessage);

        form.ReportError("boom");
        form.Provider = "P";
        Assert.Null(form.ValidationErrorMessage);
    }

    [Fact]
    public void ReportError_Null_ClearsTheBanner()
    {
        var form = ValidForm();
        form.ReportError("boom");
        Assert.True(form.HasValidationError);

        form.ReportError(null);

        Assert.False(form.HasValidationError);
    }
}
