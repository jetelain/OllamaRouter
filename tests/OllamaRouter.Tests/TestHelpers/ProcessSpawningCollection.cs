namespace OllamaRouter.Tests.TestHelpers;

/// <summary>
/// Serial ("ProcessSpawning") xunit collection (task #162).
/// </summary>
/// <remarks>
/// <para>
/// <b>Convention:</b> any test class that genuinely launches OS processes (a real
/// <c>Process</c> / <c>ProcessRunner</c>, real apphosts) MUST carry
/// <c>[Collection("ProcessSpawning")]</c>. Fake/mocked process classes
/// (<c>FakeProcessRunner</c>, fake <c>ProcessStarter</c>, <c>FakeRouterProcess</c>) stay
/// OUT of this collection.
/// </para>
/// <para>
/// Rationale: under full-suite parallel load (~3200 tests, many spawning processes) the
/// real-process tests starve the <c>ProcessRunner</c> drain threads; combined with pipe
/// buffering this produced the 2026-11-22 flake family (captures truncated at read-chunk
/// boundaries, e.g. 120 KB / 128 KB). Serializing the collection removes the load
/// coupling. Mirrors the <c>AvaloniaCollectionDefinition</c> precedent
/// (<c>DisableParallelization = true</c> on a named collection).
/// </para>
/// <para>
/// Currently participating classes:
/// <list type="bullet">
/// <item><see cref="Services.Process.ProcessRunnerTests" /></item>
/// <item><see cref="Services.Hardware.HardwareProfileTests" /> (one live probe test)</item>
/// <item><see cref="Client.OllamaRouterDaemonLauncherHandleLeakTests" /></item>
/// </list>
/// The deterministic drain-window regression gate lives in
/// <see cref="Services.Process.ProcessRunnerTests" />
/// (<c>Run_ChildExitsButDescendantHoldsPipe_DrainsFullPayloadAfterExitWithinSafetyWindow</c>).
/// </para>
/// </remarks>
[CollectionDefinition("ProcessSpawning", DisableParallelization = true)]
public sealed class ProcessSpawningCollection;
