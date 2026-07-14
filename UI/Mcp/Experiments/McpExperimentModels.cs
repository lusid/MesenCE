using System.Collections.Generic;

namespace Mesen.Mcp;

public static class McpExperimentStatus
{
	public const string Completed = "completed";
	public const string AssertionFailed = "assertion_failed";
	public const string Interrupted = "interrupted";
	public const string Failed = "failed";
}

public static class McpExperimentReason
{
	public const string Breakpoint = "breakpoint";
	public const string Timeout = "timeout";
	public const string Cancelled = "cancelled";
	public const string Reset = "reset";
	public const string RomTransition = "rom_transition";
	public const string StateChanged = "state_changed";
	public const string NativeFailure = "native_failure";
	public const string CleanupFailed = "cleanup_failed";
}

public sealed record RunExperimentRequest(string Cpu, string? SaveStateId, IReadOnlyList<McpInputSegment> Segments, int TimeoutMs, IReadOnlyList<McpMemoryObservationRequest> Observations, IReadOnlyList<McpAssertionRequest> Assertions, bool CaptureFinalScreenshot, bool FailFast);
public sealed record McpInputSegment(int Frames, IReadOnlyList<McpControllerInput> Controllers, string? Checkpoint);
public sealed record McpControllerInput(int Port, IReadOnlyList<string> Buttons);
public sealed record McpMemoryObservationRequest(string Id, string Checkpoint, string Space, uint Address, int Count, McpDecodeRequest? Decode);
public sealed record McpDecodeRequest(int Width, bool Signed, string ByteOrder);
public sealed record McpAssertionRequest(string Id, string Checkpoint, string ObservationId, string Operator, int[]? ExpectedBytes, long? ExpectedValue, long? MinimumValue, long? MaximumValue, ulong? Mask, string? ReferenceObservationId);
public sealed record McpObservationResult(string Id, string Checkpoint, string Space, uint Address, int Count, int[] Data, string Hex, long? DecodedValue);
public sealed record McpAssertionResult(string Id, string Checkpoint, string ObservationId, string Operator, bool Passed, string Actual, string Expected);
public sealed record McpCheckpointResult(string Name, int CompletedFrames, IReadOnlyList<McpObservationResult> Observations, IReadOnlyList<McpAssertionResult> Assertions);
public sealed record McpAssertionSummary(int Total, int Passed, int Failed, int Skipped);
public sealed record McpExperimentInterruption(string Reason, BreakContext? BreakContext);
public sealed record McpExperimentCleanup(bool StopConfirmed, bool InputReleased, bool LeaseReleased, bool Quarantined);
public sealed record RunExperimentResult(string Status, string? Reason, McpAssertionSummary AssertionSummary, IReadOnlyList<McpCheckpointResult> Checkpoints, IReadOnlyList<int> CompletedSegments, IReadOnlyList<int> SkippedSegments, int CompletedFrames, string FinalRunState, McpScreenshotMetadata? Screenshot, McpExperimentInterruption? Interruption, McpExperimentCleanup Cleanup);
