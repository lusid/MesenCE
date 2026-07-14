using System;
using System.Collections.Generic;
using Mesen.Interop;

namespace Mesen.Mcp;

internal sealed class McpMemorySnapshotService
{
	private readonly McpEmulatorService _emulator;
	private readonly McpMemorySnapshotStore _snapshots;

	internal McpMemorySnapshotService(McpEmulatorService emulator, McpMemorySnapshotStore snapshots)
	{
		_emulator = emulator;
		_snapshots = snapshots;
	}

	internal McpServiceResult<CreateMemorySnapshotResult> CreateMemorySnapshot(string space, uint address, int count)
	{
		if(count <= 0) {
			return McpServiceResult<CreateMemorySnapshotResult>.Failure(
				"invalid_range", "Snapshot count must be greater than zero.");
		}
		if(count > McpAutomationLimits.MaxMemorySnapshotBytes) {
			return McpServiceResult<CreateMemorySnapshotResult>.Failure(
				"resource_limit", "The memory snapshot size quota would be exceeded.");
		}

		return _emulator.ExecuteStoppedMemorySnapshot((api, identity) => {
			if(!TryResolveMemorySpace(api, space, out MemoryType type, out int size)) {
				return McpServiceResult<CreateMemorySnapshotResult>.Failure(
					"unknown_memory_space", "The selected memory space is not available.");
			}
			ulong endExclusive = (ulong)address + (uint)count;
			if(endExclusive > (ulong)size) {
				return McpServiceResult<CreateMemorySnapshotResult>.Failure(
					"invalid_range", "The requested range is outside the selected memory space.");
			}

			McpServiceResult<bool> capacity = _snapshots.CheckCreate(count);
			if(!capacity.IsSuccess) {
				return ForwardFailure<CreateMemorySnapshotResult, bool>(capacity);
			}

			string system = api.GetRomInfo().ConsoleType.ToString();
			byte[] data = api.GetMemoryValues(type, address, checked((uint)(endExclusive - 1)));
			if(data.Length != count) {
				return McpServiceResult<CreateMemorySnapshotResult>.Failure(
					"interop_failure", "Native emulator interop returned an unexpected memory range.");
			}
			McpServiceResult<McpMemorySnapshotMetadata> created =
				_snapshots.Create(system, space, address, data, identity, DateTimeOffset.UtcNow);
			return created.IsSuccess
				? McpServiceResult<CreateMemorySnapshotResult>.Success(new(created.Value!))
				: ForwardFailure<CreateMemorySnapshotResult, McpMemorySnapshotMetadata>(created);
		});
	}

	internal McpServiceResult<CompareMemorySnapshotsResult> CompareMemorySnapshots(
		string firstId,
		string secondId,
		int offset,
		int limit,
		int sampleBytes)
	{
		if(offset < 0 || limit < 1 || sampleBytes < 0) {
			return McpServiceResult<CompareMemorySnapshotsResult>.Failure(
				"invalid_range", "Snapshot comparison paging values are outside the supported range.");
		}
		if(limit > McpAutomationLimits.MaxResultPage || sampleBytes > McpAutomationLimits.MaxRunSampleBytes) {
			return McpServiceResult<CompareMemorySnapshotsResult>.Failure(
				"payload_too_large", "The snapshot comparison page or sample size exceeds the supported limit.");
		}

		return _emulator.ExecuteStoppedMemorySnapshot((_, _) => {
			McpServiceResult<McpPinnedResource<McpMemorySnapshotResource>> firstResult = _snapshots.Pin(firstId);
			if(!firstResult.IsSuccess) {
				return ForwardFailure<CompareMemorySnapshotsResult, McpPinnedResource<McpMemorySnapshotResource>>(firstResult);
			}
			using McpPinnedResource<McpMemorySnapshotResource> first = firstResult.Value!;
			McpServiceResult<McpPinnedResource<McpMemorySnapshotResource>> secondResult = _snapshots.Pin(secondId);
			if(!secondResult.IsSuccess) {
				return ForwardFailure<CompareMemorySnapshotsResult, McpPinnedResource<McpMemorySnapshotResource>>(secondResult);
			}
			using McpPinnedResource<McpMemorySnapshotResource> second = secondResult.Value!;

			McpMemorySnapshotResource before = first.Value;
			McpMemorySnapshotResource after = second.Value;
			if(!AreCompatible(before, after)) {
				return McpServiceResult<CompareMemorySnapshotsResult>.Failure(
					"incompatible_snapshots", "Memory snapshots must have matching ROM, system, space, start, and count.");
			}

			int changedBytes = 0;
			int changedRuns = 0;
			List<McpChangedMemoryRun> runs = new(Math.Min(limit, 16));
			int index = 0;
			while(index < before.Data.Length) {
				if(before.Data[index] == after.Data[index]) {
					index++;
					continue;
				}

				int runStart = index;
				bool retainRun = changedRuns >= offset && runs.Count < limit;
				int[] beforeSample = retainRun ? new int[sampleBytes] : [];
				int[] afterSample = retainRun ? new int[sampleBytes] : [];
				int sampleLength = 0;
				do {
					if(retainRun && sampleLength < sampleBytes) {
						beforeSample[sampleLength] = before.Data[index];
						afterSample[sampleLength] = after.Data[index];
						sampleLength++;
					}
					changedBytes++;
					index++;
				} while(index < before.Data.Length && before.Data[index] != after.Data[index]);
				int runLength = index - runStart;
				if(retainRun) {
					Array.Resize(ref beforeSample, sampleLength);
					Array.Resize(ref afterSample, sampleLength);
					runs.Add(new(
						checked(before.Metadata.Address + (uint)runStart),
						runLength,
						beforeSample,
						afterSample,
						runLength > sampleLength));
				}
				changedRuns++;
			}

			int returnedEnd = offset + runs.Count;
			int? nextOffset = returnedEnd < changedRuns ? returnedEnd : null;
			return McpServiceResult<CompareMemorySnapshotsResult>.Success(new(
				firstId,
				secondId,
				before.Metadata.MutableStateGeneration,
				after.Metadata.MutableStateGeneration,
				changedBytes,
				changedRuns,
				offset,
				nextOffset,
				runs));
		});
	}

	internal McpServiceResult<McpDeleteResourceResult> DeleteMemorySnapshot(string id) =>
		_emulator.ExecuteAutomation((_, _) => _snapshots.Delete(id));

	private static bool TryResolveMemorySpace(
		IMcpEmulatorApi api,
		string id,
		out MemoryType type,
		out int size)
	{
		if(!Enum.TryParse(id, ignoreCase: false, out type) || type == MemoryType.None || type.ToString() != id) {
			size = 0;
			return false;
		}
		size = api.GetMemorySize(type);
		return size > 0;
	}

	private static bool AreCompatible(McpMemorySnapshotResource first, McpMemorySnapshotResource second) =>
		first.Identity.RomIdentity == second.Identity.RomIdentity
		&& first.Metadata.System == second.Metadata.System
		&& first.Metadata.Space == second.Metadata.Space
		&& first.Metadata.Address == second.Metadata.Address
		&& first.Metadata.Count == second.Metadata.Count;

	private static McpServiceResult<T> ForwardFailure<T, TSource>(McpServiceResult<TSource> source) =>
		McpServiceResult<T>.Failure(source.Error!.Code, source.Error.Message);
}
