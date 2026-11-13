using System.Runtime.InteropServices;

namespace OllamaRouter.Client.Process;

/// <summary>
/// Windows helper for <see cref="OllamaRouterDaemonLauncher"/> (task #109): suppresses the
/// <c>HANDLE_FLAG_INHERIT</c> flag on the calling process's standard handles (stdin /
/// stdout / stderr) for the duration of the returned <see cref="IDisposable"/> scope.
/// <para>
/// Win32 background: <see cref="System.Diagnostics.Process.Start"/> with
/// <c>UseShellExecute = false</c> hard-codes <c>bInheritHandles = TRUE</c> in the
/// <c>CreateProcess</c> call (no BCL opt-out ships in .NET 10), so EVERY inheritable handle
/// in the CLI process — including the caller's own redirected stdin/stdout/stderr pipe
/// write-ends — is duplicated into the long-lived daemon. The grandparent caller then never
/// sees EOF on the CLI's pipes. Clearing the flag on the three std handles for the
/// <c>CreateProcess</c> window leaves nothing std-related to duplicate; the daemon's own
/// std streams are fresh inheritable pipes passed via <c>STARTUPINFO</c> (all three
/// redirects must be set for this to hold).
/// </para>
/// <para>
/// The state is REFERENCE-COUNTED: a flag is recorded (first writer wins) and cleared only
/// by the first acquire; it is restored to its EXACT original value only when the outermost
/// scope is disposed. Nested/overlapping scopes are therefore safe. <see cref="Dispose()"/>
/// is idempotent. On non-Windows platforms the scope is a shared no-op (Unix
/// <c>posix_spawn</c> file actions do not duplicate arbitrary parent handles).
/// </para>
/// <para>
/// The library has no logger by design: all Win32 failures are best-effort skips, consistent
/// with the launcher's existing failure posture. Only primitive-typed P/Invokes are used —
/// NativeAOT/trim-safe, zero package impact (the client stays an AOT leaf library).
/// </para>
/// </summary>
internal static class WindowsProcessLauncher
{
    private const int StdInputHandle = -10;
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;

    private const int HandleFlagInherit = 0x80;

    private static readonly IntPtr InvalidHandleValue = new(-1);

    private static readonly int[] StandardHandles = [StdInputHandle, StdOutputHandle, StdErrorHandle];

    private static readonly object Gate = new();

    private static int _activeScopes;
    private static readonly Dictionary<IntPtr, int> OriginalFlags = new();

    // DllImport (rather than LibraryImport) keeps the AOT/trim-safety of the primitive-typed
    // signatures without requiring unsafe blocks in the project (the csproj stays unchanged).
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll")]
    private static extern bool GetHandleInformation(IntPtr hHandle, out int lpFlags);

    [DllImport("kernel32.dll")]
    private static extern bool SetHandleInformation(IntPtr hHandle, int dwMask, int dwFlags);

    /// <summary>
    /// Starts a standard-handle-inheritance suppression scope. On non-Windows returns a
    /// shared no-op disposable (never throws, cheap). On Windows, for each resolvable
    /// standard handle that currently carries <c>HANDLE_FLAG_INHERIT</c>: records the exact
    /// original flags (first writer wins under nesting) and clears the inherit flag
    /// (best-effort). The flags are restored only when the OUTERMOST scope is disposed.
    /// </summary>
    public static IDisposable SuppressStandardHandleInheritance()
    {
        if (!OperatingSystem.IsWindows())
        {
            return NoopScope.Instance;
        }

        lock (Gate)
        {
            foreach (var standardHandle in StandardHandles)
            {
                var handle = GetStdHandle(standardHandle);
                if (handle == IntPtr.Zero || handle == InvalidHandleValue)
                {
                    continue; // absent/invalid stream — nothing to suppress or restore
                }

                if (!OriginalFlags.ContainsKey(handle)
                    && GetHandleInformation(handle, out var flags)
                    && (flags & HandleFlagInherit) != 0)
                {
                    OriginalFlags[handle] = flags;
                    SetHandleInformation(handle, HandleFlagInherit, 0); // best-effort
                }
            }

            _activeScopes++;
            return new Scope();
        }
    }

    private sealed class Scope : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return; // idempotent
            }

            lock (Gate)
            {
                if (--_activeScopes > 0)
                {
                    return; // an outer scope is still suppressing — do not restore yet
                }

                foreach (var (handle, originalFlags) in OriginalFlags)
                {
                    SetHandleInformation(handle, -1, originalFlags); // best-effort exact restore
                }

                OriginalFlags.Clear();
            }
        }
    }

    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();

        public void Dispose()
        {
        }
    }
}
