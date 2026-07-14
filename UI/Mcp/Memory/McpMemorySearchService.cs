using System;
using System.Collections.Generic;
using System.Numerics;
using Mesen.Interop;

namespace Mesen.Mcp;

internal sealed class McpMemorySearchService
{
	private readonly McpEmulatorService _emulator;
	private readonly McpMemorySearchStore _searches;

	internal McpMemorySearchService(McpEmulatorService emulator, McpMemorySearchStore searches)
	{
		_emulator = emulator;
		_searches = searches;
	}

	internal McpServiceResult<StartMemorySearchResult> StartMemorySearch(
		string space,
		uint address,
		int count,
		int width,
		bool signed,
		string byteOrder,
		int stride,
		long? initialValue)
	{
		if(count <= 0) {
			return Failure<StartMemorySearchResult>("invalid_range", "Search count must be greater than zero.");
		}
		if(count > McpAutomationLimits.MaxSearchRangeBytes) {
			return Failure<StartMemorySearchResult>("resource_limit", "The memory search range quota would be exceeded.");
		}
		if(width is not (1 or 2 or 4)) {
			return Failure<StartMemorySearchResult>("invalid_width", "Search width must be 1, 2, or 4 bytes.");
		}
		if(byteOrder is not ("little" or "big")) {
			return Failure<StartMemorySearchResult>("invalid_byte_order", "Byte order must be little or big.");
		}
		if(stride < 1 || stride > width) {
			return Failure<StartMemorySearchResult>("invalid_stride", "Search stride must be between one and the value width.");
		}
		if(initialValue.HasValue && !IsRepresentable(initialValue.Value, width, signed)) {
			return Failure<StartMemorySearchResult>("invalid_value", "The initial value is outside the selected numeric range.");
		}

		int positionCount = GetPositionCount(count, width, stride);
		int wordCount = GetWordCount(positionCount);
		long allocationBytes = count + ((long)wordCount * sizeof(int));
		return _emulator.ExecuteStoppedMemoryTransaction((api, identity, registerCompletion) => {
			if(!TryResolveMemorySpace(api, space, out MemoryType type, out int memorySize)) {
				return Failure<StartMemorySearchResult>("unknown_memory_space", "The selected memory space is not available.");
			}
			if((ulong)address + (uint)count > (ulong)memorySize) {
				return Failure<StartMemorySearchResult>("invalid_range", "The requested range is outside the selected memory space.");
			}
			string system = api.GetRomInfo().ConsoleType.ToString();
			McpServiceResult<bool> capacity = _searches.CheckCreate(count, allocationBytes);
			if(!capacity.IsSuccess) {
				return ForwardFailure<StartMemorySearchResult, bool>(capacity);
			}

			byte[] snapshot = api.GetMemoryValues(type, address, checked(address + (uint)count - 1));
			if(snapshot.Length != count) {
				return Failure<StartMemorySearchResult>("interop_failure", "Native emulator interop returned an unexpected memory range.");
			}

			try {
				int[] candidates = new int[wordCount];
				int candidateCount = 0;
				for(int index = 0; index < positionCount; index++) {
					int offset = index * stride;
					if(!initialValue.HasValue || Decode(snapshot, offset, width, signed, byteOrder) == initialValue.Value) {
						SetCandidate(candidates, index);
						candidateCount++;
					}
				}

				long generation = identity.MutableStateGeneration;
				McpMemorySearchState state = new(snapshot, snapshot, candidates, generation, generation);
				McpMemorySearchResource resource = new(
					system, space, address, count, width, signed, byteOrder, stride, identity, state,
					MemorySize: memorySize);
				McpServiceResult<McpTransactionalResourceCreation<McpMemorySearchResource>> created =
					_searches.CreateTransactional(resource);
				if(created.IsSuccess) {
					registerCompletion(created.Value!.Transaction.Complete);
				}
				return created.IsSuccess
					? McpServiceResult<StartMemorySearchResult>.Success(new(
						created.Value!.Id, system, space, address, count, width, signed, byteOrder, stride,
						candidateCount, identity.RomIdentity, generation))
					: ForwardFailure<StartMemorySearchResult, McpTransactionalResourceCreation<McpMemorySearchResource>>(created);
			} catch(OutOfMemoryException) {
				return Failure<StartMemorySearchResult>("resource_limit", "The memory search allocation could not be completed.");
			}
		});
	}

	internal McpServiceResult<RefineMemorySearchResult> RefineMemorySearch(
		string id, string comparison, long? value, long? delta)
	{
		McpServiceResult<bool> operands = ValidateComparison(comparison, value, delta);
		if(!operands.IsSuccess) {
			return ForwardFailure<RefineMemorySearchResult, bool>(operands);
		}

		return _emulator.ExecuteStoppedMemoryTransaction((api, identity, registerCompletion) => {
			McpServiceResult<McpPinnedResource<McpMemorySearchResource>> pinResult = _searches.Pin(id);
			if(!pinResult.IsSuccess) {
				return ForwardFailure<RefineMemorySearchResult, McpPinnedResource<McpMemorySearchResource>>(pinResult);
			}

			McpMemorySearchResource resource;
			byte[] currentSnapshot;
			int previousCandidateCount;
			McpMemorySearchResource replacement;
			using(McpPinnedResource<McpMemorySearchResource> pin = pinResult.Value!) {
				resource = pin.Value;
				McpServiceResult<bool> compatibility = ValidateCompatibility(api, identity, resource);
				if(!compatibility.IsSuccess) {
					return ForwardFailure<RefineMemorySearchResult, bool>(compatibility);
				}

				if(value.HasValue && !IsRepresentable(value.Value, resource.Width, resource.Signed)) {
					return Failure<RefineMemorySearchResult>("invalid_value", "The comparison value is outside the selected numeric range.");
				}
				McpServiceResult<bool> capacity = _searches.CheckTransactionalReplace(
					id, GetRefinementAllocationBytes(resource));
				if(!capacity.IsSuccess) {
					return ForwardFailure<RefineMemorySearchResult, bool>(capacity);
				}
				currentSnapshot = api.GetMemoryValues(
					ParseMemoryType(resource.Space), resource.Address, checked(resource.Address + (uint)resource.Count - 1));
				if(currentSnapshot.Length != resource.Count) {
					return Failure<RefineMemorySearchResult>("interop_failure", "Native emulator interop returned an unexpected memory range.");
				}

				try {
					int[] candidates = new int[resource.State.CandidateOffsets.Length];
					previousCandidateCount = CountCandidates(resource.State.CandidateOffsets);
					int positionCount = GetPositionCount(resource.Count, resource.Width, resource.Stride);
					for(int index = 0; index < positionCount; index++) {
						if(!IsCandidate(resource.State.CandidateOffsets, index)) {
							continue;
						}
						int offset = index * resource.Stride;
						long previous = Decode(resource.State.CurrentSnapshot, offset, resource.Width, resource.Signed, resource.ByteOrder);
						long current = Decode(currentSnapshot, offset, resource.Width, resource.Signed, resource.ByteOrder);
						if(Matches(comparison, previous, current, value, delta, resource.Width, resource.Signed)) {
							SetCandidate(candidates, index);
						}
					}

					McpMemorySearchState state = new(
						resource.State.CurrentSnapshot,
						currentSnapshot,
						candidates,
						resource.State.MutableStateGeneration,
						identity.MutableStateGeneration);
					replacement = resource with { Identity = identity, State = state, UndoState = resource.State };
				}
				catch(OutOfMemoryException) {
					return Failure<RefineMemorySearchResult>("resource_limit", "The memory search allocation could not be completed.");
				}
			}

			McpServiceResult<McpResourceTransaction> replaced;
			try {
				replaced = _searches.ReplaceTransactional(id, replacement);
			}
			catch(OutOfMemoryException) {
				return Failure<RefineMemorySearchResult>("resource_limit", "The memory search allocation could not be completed.");
			}
			if(!replaced.IsSuccess) {
				return ForwardFailure<RefineMemorySearchResult, McpResourceTransaction>(replaced);
			}
			registerCompletion(replaced.Value!.Complete);

			int candidateCount = CountCandidates(replacement.State.CandidateOffsets);
			return McpServiceResult<RefineMemorySearchResult>.Success(new(
				id,
				comparison,
				previousCandidateCount,
				candidateCount,
				replacement.State.PreviousMutableStateGeneration,
				replacement.State.MutableStateGeneration));
		});
	}

	internal McpServiceResult<GetMemorySearchResultsResult> GetMemorySearchResults(string id, int offset, int limit)
	{
		if(offset < 0 || limit < 1) {
			return Failure<GetMemorySearchResultsResult>("invalid_range", "Search result paging values are outside the supported range.");
		}
		if(limit > McpAutomationLimits.MaxResultPage) {
			return Failure<GetMemorySearchResultsResult>("payload_too_large", "The search result page exceeds the supported limit.");
		}

		return _emulator.ExecuteAutomation((api, identity) => {
			McpServiceResult<McpPinnedResource<McpMemorySearchResource>> pinResult = _searches.Pin(id);
			if(!pinResult.IsSuccess) {
				return ForwardFailure<GetMemorySearchResultsResult, McpPinnedResource<McpMemorySearchResource>>(pinResult);
			}
			using McpPinnedResource<McpMemorySearchResource> pin = pinResult.Value!;
			McpMemorySearchResource resource = pin.Value;
			McpServiceResult<bool> compatibility = ValidateCompatibility(api, identity, resource);
			if(!compatibility.IsSuccess) {
				return ForwardFailure<GetMemorySearchResultsResult, bool>(compatibility);
			}

			int total = CountCandidates(resource.State.CandidateOffsets);
			List<McpMemorySearchCandidate> results = new(Math.Min(limit, Math.Max(0, total - offset)));
			int seen = 0;
			int positionCount = GetPositionCount(resource.Count, resource.Width, resource.Stride);
			for(int index = 0; index < positionCount && results.Count < limit; index++) {
				if(!IsCandidate(resource.State.CandidateOffsets, index)) {
					continue;
				}
				if(seen++ < offset) {
					continue;
				}
				int candidateOffset = index * resource.Stride;
				results.Add(new(
					checked(resource.Address + (uint)candidateOffset),
					Decode(resource.State.PreviousSnapshot, candidateOffset, resource.Width, resource.Signed, resource.ByteOrder),
					Decode(resource.State.CurrentSnapshot, candidateOffset, resource.Width, resource.Signed, resource.ByteOrder)));
			}

			int returnedEnd = offset + results.Count;
			return McpServiceResult<GetMemorySearchResultsResult>.Success(new(
				id,
				total,
				offset,
				returnedEnd < total ? returnedEnd : null,
				resource.State.PreviousMutableStateGeneration,
				resource.State.MutableStateGeneration,
				results));
		});
	}

	internal McpServiceResult<UndoMemorySearchResult> UndoMemorySearch(string id)
	{
		return _emulator.ExecuteAutomationTransaction((api, identity, registerCompletion) => {
			McpServiceResult<McpPinnedResource<McpMemorySearchResource>> pinResult = _searches.Pin(id);
			if(!pinResult.IsSuccess) {
				return ForwardFailure<UndoMemorySearchResult, McpPinnedResource<McpMemorySearchResource>>(pinResult);
			}

			McpMemorySearchResource replacement;
			using(McpPinnedResource<McpMemorySearchResource> pin = pinResult.Value!) {
				McpMemorySearchResource resource = pin.Value;
				McpServiceResult<bool> compatibility = ValidateCompatibility(api, identity, resource);
				if(!compatibility.IsSuccess) {
					return ForwardFailure<UndoMemorySearchResult, bool>(compatibility);
				}
				if(resource.UndoState is null) {
					return Failure<UndoMemorySearchResult>("undo_unavailable", "The memory search has no refinement to undo.");
				}
				replacement = resource with { Identity = identity, State = resource.UndoState, UndoState = null };
			}

			McpServiceResult<bool> capacity = _searches.CheckTransactionalReplace(
				id, GetStateAllocationBytes(replacement.State));
			if(!capacity.IsSuccess) {
				return ForwardFailure<UndoMemorySearchResult, bool>(capacity);
			}

			McpServiceResult<McpResourceTransaction> replaced;
			try {
				replaced = _searches.ReplaceTransactional(id, replacement);
			}
			catch(OutOfMemoryException) {
				return Failure<UndoMemorySearchResult>("resource_limit", "The memory search allocation could not be completed.");
			}
			if(!replaced.IsSuccess) {
				return ForwardFailure<UndoMemorySearchResult, McpResourceTransaction>(replaced);
			}
			registerCompletion(replaced.Value!.Complete);
			return McpServiceResult<UndoMemorySearchResult>.Success(new(
				id,
				CountCandidates(replacement.State.CandidateOffsets),
				replacement.State.PreviousMutableStateGeneration,
				replacement.State.MutableStateGeneration));
		});
	}

	internal McpServiceResult<McpDeleteResourceResult> DeleteMemorySearch(string id) =>
		_emulator.ExecuteAutomation((_, _) => _searches.Delete(id));

	private McpServiceResult<bool> ValidateCompatibility(
		IMcpEmulatorApi api, McpStateIdentity identity, McpMemorySearchResource resource)
	{
		bool compatible = identity.RomIdentity == resource.Identity.RomIdentity
			&& api.GetRomInfo().ConsoleType.ToString() == resource.System
			&& TryResolveMemorySpace(api, resource.Space, out _, out int memorySize)
			&& memorySize == resource.MemorySize
			&& (ulong)resource.Address + (uint)resource.Count <= (ulong)memorySize;
		if(compatible) {
			return McpServiceResult<bool>.Success(true);
		}

		_searches.InvalidateTopology(candidate => ReferenceEquals(candidate, resource));
		return Failure<bool>("stale_resource", "The memory search is no longer compatible with the active ROM or memory topology.");
	}

	private static McpServiceResult<bool> ValidateComparison(string comparison, long? value, long? delta)
	{
		bool valid = comparison switch {
			"exact" or "not_equal" => value.HasValue && !delta.HasValue,
			"increased" or "decreased" or "changed" or "unchanged" => !value.HasValue && !delta.HasValue,
			"increased_by" or "decreased_by" => !value.HasValue && delta is > 0,
			_ => false
		};
		return valid
			? McpServiceResult<bool>.Success(true)
			: Failure<bool>("invalid_comparison", "The comparison and its operands are not valid.");
	}

	private static bool Matches(
		string comparison, long previous, long current, long? value, long? delta, int width, bool signed)
	{
		return comparison switch {
			"exact" => current == value,
			"not_equal" => current != value,
			"increased" => current > previous,
			"decreased" => current < previous,
			"changed" => current != previous,
			"unchanged" => current == previous,
			"increased_by" => TryApplyDelta(previous, delta!.Value, true, width, signed, out long increased)
				&& current == increased,
			"decreased_by" => TryApplyDelta(previous, delta!.Value, false, width, signed, out long decreased)
				&& current == decreased,
			_ => false
		};
	}

	private static bool TryApplyDelta(
		long value, long delta, bool increase, int width, bool signed, out long result)
	{
		try {
			result = increase ? checked(value + delta) : checked(value - delta);
			return IsRepresentable(result, width, signed);
		}
		catch(OverflowException) {
			result = 0;
			return false;
		}
	}

	private static bool IsRepresentable(long value, int width, bool signed)
	{
		int bits = width * 8;
		if(signed) {
			long minimum = -(1L << (bits - 1));
			long maximum = (1L << (bits - 1)) - 1;
			return value >= minimum && value <= maximum;
		}
		long unsignedMaximum = (1L << bits) - 1;
		return value >= 0 && value <= unsignedMaximum;
	}

	private static long Decode(byte[] data, int offset, int width, bool signed, string byteOrder)
	{
		uint value = 0;
		if(byteOrder == "little") {
			for(int index = width - 1; index >= 0; index--) {
				value = (value << 8) | data[offset + index];
			}
		} else {
			for(int index = 0; index < width; index++) {
				value = (value << 8) | data[offset + index];
			}
		}

		if(!signed) {
			return value;
		}
		int shift = 64 - (width * 8);
		return ((long)value << shift) >> shift;
	}

	private static int GetPositionCount(int count, int width, int stride) =>
		count < width ? 0 : ((count - width) / stride) + 1;

	private static int GetWordCount(int positionCount) => (positionCount + 31) / 32;

	private static void SetCandidate(int[] candidates, int index) =>
		candidates[index >> 5] |= 1 << (index & 31);

	private static bool IsCandidate(int[] candidates, int index) =>
		(((uint)candidates[index >> 5] >> (index & 31)) & 1U) != 0;

	private static int CountCandidates(int[] candidates)
	{
		int count = 0;
		foreach(int word in candidates) {
			count += BitOperations.PopCount((uint)word);
		}
		return count;
	}

	private static long GetRefinementAllocationBytes(McpMemorySearchResource resource) =>
		GetStateAllocationBytes(resource.State)
			+ resource.Count
			+ Buffer.ByteLength(resource.State.CandidateOffsets);

	private static long GetStateAllocationBytes(McpMemorySearchState state)
	{
		HashSet<Array> arrays = new(ReferenceEqualityComparer.Instance) {
			state.PreviousSnapshot,
			state.CurrentSnapshot,
			state.CandidateOffsets
		};
		long bytes = 0;
		foreach(Array array in arrays) {
			bytes += Buffer.ByteLength(array);
		}
		return bytes;
	}

	private static bool TryResolveMemorySpace(
		IMcpEmulatorApi api, string id, out MemoryType type, out int size)
	{
		if(!Enum.TryParse(id, ignoreCase: false, out type) || type == MemoryType.None || type.ToString() != id) {
			size = 0;
			return false;
		}
		size = api.GetMemorySize(type);
		return size > 0;
	}

	private static MemoryType ParseMemoryType(string value) => Enum.Parse<MemoryType>(value, ignoreCase: false);

	private static McpServiceResult<T> Failure<T>(string code, string message) => McpServiceResult<T>.Failure(code, message);

	private static McpServiceResult<T> ForwardFailure<T, TSource>(McpServiceResult<TSource> source) =>
		Failure<T>(source.Error!.Code, source.Error.Message);
}
