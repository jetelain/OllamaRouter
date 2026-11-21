using System.Runtime.InteropServices;
using OllamaRouter.Client.Process;

namespace OllamaRouter.Tests.Client;

/// <summary>
/// Unit tests for <see cref="WindowsProcessLauncher"/> (task #109): the std-handle-inheritance
/// suppression scope. NO real child processes: the scope is exercised directly against the
/// TEST HOST's own standard handles (snapshot → suppress → assert cleared → dispose →
/// assert EXACT restore), plus nesting semantics and the non-Windows no-op contract.
/// Every budget is sub-second.
/// </summary>
public sealed class WindowsProcessLauncherTests
{
    private const int StdInputHandle = -10;
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;

    private const int HandleFlagInherit = 0x00000001;

    private static readonly IntPtr InvalidHandleValue = new(-1);

    private static readonly int[] StandardHandles = [StdInputHandle, StdOutputHandle, StdErrorHandle];

    [Fact]
    public void SuppressScope_AllPlatforms_NoOpContract_DoubleDispose_NeverThrows()
    {
        // On non-Windows this is the shared no-op; on Windows it must be a real, idempotent
        // scope. Both contracts (never throws, double-dispose safe) hold everywhere.
        using var scope = WindowsProcessLauncher.SuppressStandardHandleInheritance();
        scope.Dispose();
        scope.Dispose(); // idempotent
    }

    [Fact]
    public void SuppressScope_ClearsInheritFlagWithinScope_AndRestoresExactOriginalFlags_After()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // the Win32 flag semantics are only observable on Windows
        }

        var snapshot = SnapshotStandardHandleFlags();
        var resolvable = snapshot.Where(kv => kv.Value is not null).Select(kv => kv.Key).ToArray();
        if (resolvable.Length == 0)
        {
            return; // this host exposes no resolvable std handles (headless edge)
        }

        using (WindowsProcessLauncher.SuppressStandardHandleInheritance())
        {
            foreach (var n in resolvable)
            {
                var flags = ReadStandardHandleFlags(n)!;
                Assert.True((flags & HandleFlagInherit) == 0,
                    $"std handle {n}: inherit flag must be cleared inside the scope");
            }
        }

        foreach (var n in resolvable)
        {
            var flags = ReadStandardHandleFlags(n)!;
            Assert.True(snapshot[n] == flags,
                $"std handle {n}: exact original flags must be restored");
        }
    }

    [Fact]
    public void SuppressScope_NestedScopes_RestoresOnlyWhenOutermostScopeDisposes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // the Win32 flag semantics are only observable on Windows
        }

        var snapshot = SnapshotStandardHandleFlags();
        var resolvable = snapshot.Where(kv => kv.Value is not null).Select(kv => kv.Key).ToArray();
        if (resolvable.Length == 0)
        {
            return; // this host exposes no resolvable std handles (headless edge)
        }

        using var outer = WindowsProcessLauncher.SuppressStandardHandleInheritance();
        using var inner = WindowsProcessLauncher.SuppressStandardHandleInheritance();

        AssertAllCleared(resolvable); // inner scope still suppressing

        inner.Dispose();
        AssertAllCleared(resolvable); // the inner dispose must NOT restore (outer is active)

        outer.Dispose();
        foreach (var n in resolvable)
        {
            Assert.True(snapshot[n] == ReadStandardHandleFlags(n),
                $"std handle {n}: exact original flags must be restored after the outermost dispose");
        }
    }

    private static void AssertAllCleared(int[] resolvable)
    {
        foreach (var n in resolvable)
        {
            var flags = ReadStandardHandleFlags(n)!;
            Assert.True((flags & HandleFlagInherit) == 0,
                $"std handle {n}: inherit flag must still be cleared");
        }
    }

    private static Dictionary<int, int?> SnapshotStandardHandleFlags()
    {
        var snapshot = new Dictionary<int, int?>();
        foreach (var n in StandardHandles)
        {
            snapshot[n] = ReadStandardHandleFlags(n);
        }

        return snapshot;
    }

    private static int? ReadStandardHandleFlags(int nStdHandle)
    {
        var handle = NativeMethods.GetStdHandle(nStdHandle);
        if (handle == IntPtr.Zero || handle == InvalidHandleValue)
        {
            return null;
        }

        return NativeMethods.GetHandleInformation(handle, out var flags) ? flags : null;
    }

    /// <summary>Test-local kernel32 P/Invoke (test host only; never ships in the library).</summary>
    private static class NativeMethods
    {
        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll")]
        internal static extern bool GetHandleInformation(IntPtr hHandle, out int lpFlags);
    }
}
