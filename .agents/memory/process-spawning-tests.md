# Convention: process-spawning test classes must join `[Collection("ProcessSpawning")]`

**Rule:** Any test class that genuinely launches OS processes (a real `Process` /
`ProcessRunner`, or real apphost executables) MUST carry
`[Collection("ProcessSpawning")]` on the class declaration. Fake/mocked process
types (`FakeProcessRunner`, fake `ProcessStarter`, `FakeRouterProcess`) stay OUT of
the collection — they spawn nothing.

The collection is defined with parallelization disabled:

```csharp
// tests/OllamaRouter.Tests/TestHelpers/ProcessSpawningCollection.cs
[CollectionDefinition("ProcessSpawning", DisableParallelization = true)]
public sealed class ProcessSpawningCollection;
```

This follows the existing `AvaloniaCollectionDefinition` precedent (a named xunit
collection whose definition sets `DisableParallelization = true`).

## Why

`ProcessRunner.Run` awaits its background stdout/stderr drain tasks after the child
exits (or is killed). Under full-suite parallel load (~3200 tests, many spawning
processes) the `LongRunning` drain threads were starved by CPU contention; combined
with OS pipe buffering this truncated captures exactly at read-chunk boundaries
(120 KB / 128 KB) — the 2026-11-22 flake family in
`ProcessRunnerTests` (`Run_LargeStandardInputEchoedToStdOut_RoundTripsWithoutDeadlock`,
`Run_VerboseStdErrExceedingPipeBuffer_CapturesFullErrorWithoutTimeout`,
`Run_VerboseStdOutAndStdErrBothLarge_DoesNotDeadlockAndCapturesBoth`). Serializing
the collection removes the load coupling that made the post-exit drain wait
load-dependent. See the source fix: the post-exit drain is bounded by the
`DrainAfterExitSafetyTimeoutMs` (30 s) safety window in
`src/OllamaRouter/Services/Process/ProcessRunner.cs` — a starvation guard, not a
functional timeout (after child exit the pipes are guaranteed to reach EOF).

## Currently participating classes

- `tests/OllamaRouter.Tests/Services/Process/ProcessRunnerTests.cs`
- `tests/OllamaRouter.Tests/Services/Hardware/HardwareProfileTests.cs` (one live
  `nvidia-smi`/`wmic`/bash probe; the rest of the class is fake-based — serializing
  it is harmless, all sub-second)
- `tests/OllamaRouter.Tests/Client/OllamaRouterDaemonLauncherHandleLeakTests.cs`
  (launches the real CLI + server apphosts; already `[Trait("Category", "Integration")]`)

## Regression gate

The deterministic drain-window regression test is
`ProcessRunnerTests.Run_ChildExitsButDescendantHoldsPipe_DrainsFullPayloadAfterExitWithinSafetyWindow`:
a backgrounded descendant holds the stdout pipe open for ~4 s after the direct child
exits, so post-exit drain takes longer than the old 2 s cap (fails deterministically
against it) and comfortably fits the 30 s safety window. A layout guard
(`ProcessSpawning_CollectionIsDefinedAndProcessRunnerTestsParticipates`) keeps the
collection definition and participation from being accidentally removed.
