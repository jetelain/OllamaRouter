using OllamaRouter.App.ViewModels;
using OllamaRouter.Endpoints.RouterApi.Targets;

namespace OllamaRouter.Tests.App;

/// <summary>
/// <see cref="OllamaNodeAddFormViewModel"/> tests (task #149): the dedicated "Add Ollama
/// Node" form — the validation matrix (shared <see cref="TargetFieldValidator"/> rules,
/// byte-identical messages), the pinned-<c>type=ollama</c> wire construction (explicit
/// nulls for the openai-only / shared-only fields — an apiKey on a non-openai target is a
/// server 400, so it is never sent), the <c>TestConnectionFirst</c> default, and the
/// report-error / clear-on-edit behavior.
/// </summary>
public sealed class OllamaNodeAddFormViewModelTests
{
    private static OllamaNodeAddFormViewModel ValidForm()
    {
        var form = new OllamaNodeAddFormViewModel
        {
            Name = "GpuBox",
            Url = "http://10.0.0.7:11434",
        };
        return form;
    }

    // -- Validation matrix ----------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void BuildAddRequest_BlankName_Rejects(string name)
    {
        var form = ValidForm();
        form.Name = name;

        Assert.Equal("The target name must not be empty.", form.BuildAddRequest().Error);
    }

    [Fact]
    public void BuildAddRequest_NameLongerThan64Chars_Rejects()
    {
        var form = ValidForm();
        form.Name = new string('a', 65);

        Assert.Equal("The target name must be 64 characters or fewer.", form.BuildAddRequest().Error);
    }

    [Theory]
    [InlineData("Bad/Name")]
    [InlineData("Bad\\Name")]
    [InlineData("Bad:Name")]
    public void BuildAddRequest_NameWithForbiddenSeparators_Rejects(string name)
    {
        var form = ValidForm();
        form.Name = name;

        Assert.Equal("The target name must not contain '/', '\\' or ':'.", form.BuildAddRequest().Error);
    }

    [Theory]
    [InlineData("Cloud")]
    [InlineData("cloud")]
    [InlineData(" CLOUD ")]
    public void BuildAddRequest_CloudName_Rejects(string name)
    {
        var form = ValidForm();
        form.Name = name;

        Assert.Equal("\"Cloud\" is reserved for the cloud overflow pseudo-target.", form.BuildAddRequest().Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://host/path")]
    [InlineData("file:///etc/hosts")]
    public void BuildAddRequest_NonAbsoluteOrNonHttpUrl_Rejects(string url)
    {
        var form = ValidForm();
        form.Url = url;

        Assert.Equal("The target URL must be an absolute http(s) URI.", form.BuildAddRequest().Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("2.5")]
    public void BuildAddRequest_BadMaxParallel_Rejects(string maxParallel)
    {
        var form = ValidForm();
        form.MaxParallel = maxParallel;

        Assert.Equal("maxParallel must be an integer of 1 or greater.", form.BuildAddRequest().Error);
    }

    [Theory]
    [InlineData("bad user")]
    [InlineData("üser")]
    [InlineData("a@b")]
    public void BuildAddRequest_BadSshUserCharset_Rejects(string sshUser)
    {
        var form = ValidForm();
        form.SshUser = sshUser;

        Assert.Equal(
            "sshUser must be 1–64 characters of letters, digits, '.', '_' or '-' (or blank).",
            form.BuildAddRequest().Error);
    }

    [Fact]
    public void BuildAddRequest_SshUserTooLong_Rejects()
    {
        var form = ValidForm();
        form.SshUser = new string('a', 65);

        Assert.Equal(
            "sshUser must be 1–64 characters of letters, digits, '.', '_' or '-' (or blank).",
            form.BuildAddRequest().Error);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-22")]
    [InlineData("65536")]
    [InlineData("70000")]
    [InlineData("abc")]
    public void BuildAddRequest_BadSshPort_Rejects(string sshPort)
    {
        var form = ValidForm();
        form.SshPort = sshPort;

        Assert.Equal("sshPort must be a port between 1 and 65535 (or blank).", form.BuildAddRequest().Error);
    }

    // -- Wire construction -----------------------------------------------------------

    [Fact]
    public void BuildAddRequest_Valid_BuildsOllamaRequest_WithExplicitNulls()
    {
        var form = ValidForm();
        form.Name = "  GpuBox  ";
        form.Url = "http://10.0.0.7:11434/";
        form.MaxParallel = "3";
        form.SshUser = "gpu.admin-1";
        form.SshPort = "2222";

        var (request, error) = form.BuildAddRequest();

        Assert.Null(error);
        Assert.IsType<AddTargetRequest>(request);
        Assert.Equal("GpuBox", request.Name); // trimmed
        Assert.Equal("http://10.0.0.7:11434", request.Url); // trailing / stripped
        Assert.Equal(3, request.MaxParallel);
        Assert.True(request.TestConnectionFirst); // default
        Assert.Equal("ollama", request.Type); // pinned
        Assert.Equal("gpu.admin-1", request.SshUser);
        Assert.Equal(2222, request.SshPort);

        // Openai-only / shared-only fields are EXPLICITLY null (an apiKey on a non-openai
        // target is a server 400 — never sent).
        Assert.Null(request.ApiKey);
        Assert.Null(request.Provider);
        Assert.Null(request.Models);
        Assert.Null(request.PowerWatts);
        Assert.Null(request.HardwareHint);
    }

    [Fact]
    public void BuildAddRequest_Valid_BlankSshFields_SendNull()
    {
        var form = ValidForm();

        var (request, error) = form.BuildAddRequest();

        Assert.Null(error);
        Assert.Null(request.SshUser); // blank = unset (null)
        Assert.Null(request.SshPort); // blank = unset (null, default port 22 at probe time)
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

    // -- Report error / clear on edit --------------------------------------------------

    [Fact]
    public void ReportError_SetsHasValidationError_AndEditingAnyFieldClearsIt()
    {
        var form = ValidForm();
        form.Name = ""; // force a validation failure
        var (_, error) = form.BuildAddRequest();
        Assert.Equal("The target name must not be empty.", error);

        // The dialog Save handler reports the build error back onto the form (the banner
        // re-shows and the dialog stays open — the same contract as the shared form).
        form.ReportError(error);
        Assert.True(form.HasValidationError);

        form.ReportError("server said no (retry)");
        Assert.Equal("server said no (retry)", form.ValidationErrorMessage);
        Assert.True(form.HasValidationError);

        form.Name = "GpuBox";
        Assert.Null(form.ValidationErrorMessage);
        Assert.False(form.HasValidationError);
        Assert.Null(form.BuildAddRequest().Error);
    }

    [Fact]
    public void EditingEachField_ClearsAReportedError()
    {
        foreach (Action<OllamaNodeAddFormViewModel> edit in new Action<OllamaNodeAddFormViewModel>[]
        {
            f => f.Name = "x",
            f => f.Url = "http://127.0.0.1:1",
            f => f.MaxParallel = "2",
            f => f.SshUser = "u",
            f => f.SshPort = "22",
        })
        {
            var form = ValidForm();
            form.ReportError("boom");
            Assert.True(form.HasValidationError);

            edit(form);

            Assert.Null(form.ValidationErrorMessage);
            Assert.False(form.HasValidationError);
        }
    }

    [Fact]
    public void ReportError_Null_ClearsTheBanner()
    {
        var form = ValidForm();
        form.ReportError("boom");
        Assert.True(form.HasValidationError);

        form.ReportError(null);

        Assert.Null(form.ValidationErrorMessage);
        Assert.False(form.HasValidationError);
    }
}
