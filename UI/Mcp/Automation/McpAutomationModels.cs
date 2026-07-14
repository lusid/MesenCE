using System;
using System.Collections.Generic;

namespace Mesen.Mcp;

internal static class McpAutomationLimits
{
	internal const int MaxSaveStates = 8;
	internal const int MaxSaveStateBytes = 16 * 1024 * 1024;
	internal const int MaxAggregateSaveStateBytes = 64 * 1024 * 1024;
	internal const int MaxMemorySnapshots = 16;
	internal const int MaxMemorySnapshotBytes = 16 * 1024 * 1024;
	internal const int MaxAggregateMemorySnapshotBytes = 64 * 1024 * 1024;
	internal const int MaxMemorySearches = 8;
	internal const int MaxSearchRangeBytes = 16 * 1024 * 1024;
	internal const int MaxSearchAllocationBytes = 40 * 1024 * 1024;
	internal const int MaxAggregateSearchAllocationBytes = 160 * 1024 * 1024;
	internal const int MaxSegments = 256;
	internal const int MaxExperimentFrames = 3600;
	internal const int MaxObservations = 256;
	internal const int MaxAssertions = 256;
	internal const int MaxObservedBytes = 65536;
	internal const int MaxPngBytes = 8 * 1024 * 1024;
	internal const int MaxScreenshotDimension = 4096;
	internal const int MaxScreenshotPixels = 16_777_216;
	internal const int MaxResultPage = 1000;
	internal const int MaxRunSampleBytes = 64;
	internal const int MinExperimentTimeoutMs = 1;
	internal const int MaxExperimentTimeoutMs = 300000;
	internal static readonly TimeSpan ResourceIdleExpiration = TimeSpan.FromMinutes(30);
}

public sealed record LoadSaveStateRequest(string Id);
public sealed record DeleteSaveStateRequest(string Id);
public sealed record McpAutomationResourceLimits(int MaxSaveStates, int MaxSaveStateBytes, int MaxAggregateSaveStateBytes, int MaxMemorySnapshots, int MaxMemorySnapshotBytes, int MaxAggregateMemorySnapshotBytes, int MaxMemorySearches, int MaxSearchRangeBytes, int MaxSearchAllocationBytes, int MaxAggregateSearchAllocationBytes, int MaxSegments, int MaxExperimentFrames, int MaxObservations, int MaxAssertions, int MaxObservedBytes, int MaxPngBytes, int MaxScreenshotDimension, int MaxScreenshotPixels, int MaxResultPage, int MaxRunSampleBytes, int MinExperimentTimeoutMs, int MaxExperimentTimeoutMs, int ResourceIdleExpirationMinutes);
public sealed record McpControllerControlCapability(string Id, int NativeId, string ValueType);
public sealed record McpControllerCapability(int Port, string DeviceType, bool ExclusiveInput, IReadOnlyList<McpControllerControlCapability> Controls);
public sealed record McpAutomationCapabilities(string System, long RomIdentity, long MutableStateGeneration, bool SaveStates, bool Screenshots, bool DeterministicFrames, string? FrameSemantics, IReadOnlyList<McpControllerCapability> Controllers, McpAutomationResourceLimits Limits, IReadOnlyList<string> Limitations);
public sealed record McpSaveStateMetadata(string Id, int ByteSize, long RomIdentity, long MutableStateGeneration, DateTimeOffset CreatedAt);
public sealed record McpSaveStateLoadResult(string Id, long RomIdentity, long PreviousMutableStateGeneration, long MutableStateGeneration, string State);
public sealed record McpDeleteResourceResult(string Id, bool Deleted);
public sealed record McpScreenshotMetadata(int Width, int Height, uint FrameNumber, int PngBytes, long RomIdentity, long MutableStateGeneration);
internal sealed record McpScreenshotCapture(McpScreenshotMetadata Metadata, byte[] Png);
