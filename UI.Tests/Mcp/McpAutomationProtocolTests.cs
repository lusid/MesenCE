using System.Text.Json;
using Mesen.Mcp;

namespace UI.Tests.Mcp;

public sealed class McpAutomationProtocolTests
{
	[Fact]
	public void ExperimentContracts_SerializeWithoutReflection()
	{
		RunExperimentRequest request = new(
			"Nes",
			null,
			[new(1, [new(0, ["Start"])], "started")],
			30000,
			[new("lives", "started", "NesInternalRam", 0xC4, 1, new(1, false, "little"))],
			[new("lives-is-three", "started", "lives", "equal", null, 3, null, null, null, null)],
			true,
			false
		);

		string json = JsonSerializer.Serialize(request, McpToolJsonContext.Default.RunExperimentRequest);
		Assert.Contains("\"captureFinalScreenshot\":true", json);
		Assert.Contains("\"expectedValue\":3", json);
		Assert.DoesNotContain("$type", json);
	}

	[Fact]
	public void MemorySearchContracts_SerializeWithoutReflection()
	{
		Assert.Equal(
			"{\"space\":\"NesWorkRam\",\"address\":196,\"count\":32,\"width\":1,\"signed\":false,\"byteOrder\":\"little\",\"stride\":1,\"initialValue\":3}",
			JsonSerializer.Serialize(
				new StartMemorySearchRequest("NesWorkRam", 0xC4, 32, 1, false, "little", 1, 3),
				McpToolJsonContext.Default.StartMemorySearchRequest
			)
		);
		Assert.Equal(
			"{\"id\":\"search-1\",\"comparison\":\"increased_by\",\"value\":null,\"delta\":1}",
			JsonSerializer.Serialize(
				new RefineMemorySearchRequest("search-1", "increased_by", null, 1),
				McpToolJsonContext.Default.RefineMemorySearchRequest
			)
		);
		Assert.Equal(
			"{\"id\":\"search-1\",\"offset\":0,\"limit\":100}",
			JsonSerializer.Serialize(
				new GetMemorySearchResultsRequest("search-1", 0, 100),
				McpToolJsonContext.Default.GetMemorySearchResultsRequest
			)
		);
		Assert.Equal(
			"{\"id\":\"search-1\"}",
			JsonSerializer.Serialize(
				new UndoMemorySearchRequest("search-1"),
				McpToolJsonContext.Default.UndoMemorySearchRequest
			)
		);
		Assert.Equal(
			"{\"id\":\"search-1\"}",
			JsonSerializer.Serialize(
				new DeleteMemorySearchRequest("search-1"),
				McpToolJsonContext.Default.DeleteMemorySearchRequest
			)
		);
	}

	[Fact]
	public void AutomationCapabilities_SerializeLimitsAndNestedControlsWithoutReflection()
	{
		McpAutomationResourceLimits limits = new(
			McpAutomationLimits.MaxSaveStates,
			McpAutomationLimits.MaxSaveStateBytes,
			McpAutomationLimits.MaxAggregateSaveStateBytes,
			McpAutomationLimits.MaxMemorySnapshots,
			McpAutomationLimits.MaxMemorySnapshotBytes,
			McpAutomationLimits.MaxAggregateMemorySnapshotBytes,
			McpAutomationLimits.MaxMemorySearches,
			McpAutomationLimits.MaxSearchRangeBytes,
			McpAutomationLimits.MaxSearchAllocationBytes,
			McpAutomationLimits.MaxAggregateSearchAllocationBytes,
			McpAutomationLimits.MaxSegments,
			McpAutomationLimits.MaxExperimentFrames,
			McpAutomationLimits.MaxObservations,
			McpAutomationLimits.MaxAssertions,
			McpAutomationLimits.MaxObservedBytes,
			McpAutomationLimits.MaxPngBytes,
			McpAutomationLimits.MaxScreenshotDimension,
			McpAutomationLimits.MaxScreenshotPixels,
			McpAutomationLimits.MaxResultPage,
			McpAutomationLimits.MinExperimentTimeoutMs,
			McpAutomationLimits.MaxExperimentTimeoutMs,
			(int)McpAutomationLimits.ResourceIdleExpiration.TotalMinutes
		);
		McpAutomationCapabilities capabilities = new(
			"Nes",
			1,
			2,
			true,
			true,
			true,
			"next_frame_boundary",
			[new(0, "Gamepad", true, [new("Start", 7, "button")])],
			limits,
			[]
		);

		string json = JsonSerializer.Serialize(capabilities, McpToolJsonContext.Default.McpAutomationCapabilities);
		Assert.Contains("\"maxSaveStates\":8", json);
		Assert.Contains("\"resourceIdleExpirationMinutes\":30", json);
		Assert.Contains("\"exclusiveInput\":true", json);
		Assert.DoesNotContain("$type", json);
	}

	[Fact]
	public void PublicResultContracts_HaveGeneratedMetadataAndStableDiscriminators()
	{
		Assert.NotNull(McpToolJsonContext.Default.McpSaveStateMetadata);
		Assert.NotNull(McpToolJsonContext.Default.McpSaveStateLoadResult);
		Assert.NotNull(McpToolJsonContext.Default.McpDeleteResourceResult);
		Assert.NotNull(McpToolJsonContext.Default.McpScreenshotMetadata);
		Assert.NotNull(McpToolJsonContext.Default.RunExperimentResult);
		Assert.NotNull(McpToolJsonContext.Default.CreateMemorySnapshotResult);
		Assert.NotNull(McpToolJsonContext.Default.CompareMemorySnapshotsResult);
		Assert.NotNull(McpToolJsonContext.Default.StartMemorySearchResult);
		Assert.NotNull(McpToolJsonContext.Default.RefineMemorySearchResult);
		Assert.NotNull(McpToolJsonContext.Default.GetMemorySearchResultsResult);
		Assert.NotNull(McpToolJsonContext.Default.UndoMemorySearchResult);
		Assert.Equal("completed", McpExperimentStatus.Completed);
		Assert.Equal("assertion_failed", McpExperimentStatus.AssertionFailed);
		Assert.Equal("interrupted", McpExperimentStatus.Interrupted);
		Assert.Equal("failed", McpExperimentStatus.Failed);
		Assert.Equal("breakpoint", McpExperimentReason.Breakpoint);
		Assert.Equal("timeout", McpExperimentReason.Timeout);
		Assert.Equal("cancelled", McpExperimentReason.Cancelled);
		Assert.Equal("reset", McpExperimentReason.Reset);
		Assert.Equal("rom_transition", McpExperimentReason.RomTransition);
		Assert.Equal("state_changed", McpExperimentReason.StateChanged);
		Assert.Equal("native_failure", McpExperimentReason.NativeFailure);
		Assert.Equal("cleanup_failed", McpExperimentReason.CleanupFailed);
	}
}
