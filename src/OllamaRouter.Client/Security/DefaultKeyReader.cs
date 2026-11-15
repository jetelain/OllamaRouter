using Pmad.LocalApi.Security;

namespace OllamaRouter.Security;

/// <summary>
/// Canonical OllamaRouter factory for <see cref="IDefaultKeyReader"/> (task #115 — replaces
/// the in-tree DefaultKeyReader, whose discovery semantics are now provided by
/// Pmad.LocalApi.Security): pins OllamaRouter's directory/environment conventions
/// (app directory "OllamaRouter", Unix dot directory "ollamarouter", home env var
/// "OLLAMAROUTER_HOME") and delegates to
/// <see cref="DefaultKeyReader.CreateForApp(string, string, string?, string?, IKeyProtector?, string?, System.Func{string, string?}?, System.Collections.Generic.IEnumerable{string}?)"/>.
/// One definition shared by the CLI, the desktop app, the daemon launcher and the tests.
/// </summary>
public static class OllamaRouterDefaultKeyReader
{
    public static IDefaultKeyReader Create(
        string? configDirectory = null,
        IKeyProtector? protector = null,
        Func<string, string?>? environmentVariableAccessor = null)
        => DefaultKeyReader.CreateForApp(
            appDirectoryName: "OllamaRouter",
            unixDotDirectoryName: "ollamarouter",
            homeEnvironmentVariable: "OLLAMAROUTER_HOME",
            configDirectory: configDirectory,
            protector: protector,
            environmentVariableAccessor: environmentVariableAccessor);
}
