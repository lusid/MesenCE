using Mesen.Mcp;

namespace UI.Tests.Mcp;

public sealed class McpResourceStoreTests
{
	private static readonly McpStateIdentity Identity = new(7, 11);

	[Fact]
	public void SaveStateStore_EnforcesCountPerItemAndAggregateLimitsWithoutEviction()
	{
		FakeMcpMonotonicClock clock = new();
		using McpSaveStateStore countStore = new(clock);
		List<string> ids = [];
		for(int i = 0; i < McpAutomationLimits.MaxSaveStates; i++) {
			ids.Add(AssertSuccess(countStore.Create([0], Identity, DateTimeOffset.UnixEpoch)).Id);
		}

		AssertError(countStore.Create([0], Identity, DateTimeOffset.UnixEpoch), "resource_limit");
		using McpPinnedResource<McpSaveStateResource> first = AssertSuccess(countStore.Pin(ids[0]));
		Assert.Single(first.Value.Data);

		using McpSaveStateStore byteStore = new(clock);
		AssertError(
			byteStore.Create(new byte[McpAutomationLimits.MaxSaveStateBytes + 1], Identity, DateTimeOffset.UnixEpoch),
			"resource_limit");
		for(int i = 0; i < McpAutomationLimits.MaxAggregateSaveStateBytes / McpAutomationLimits.MaxSaveStateBytes; i++) {
			AssertSuccess(byteStore.Create(new byte[McpAutomationLimits.MaxSaveStateBytes], Identity, DateTimeOffset.UnixEpoch));
		}
		AssertError(byteStore.Create([0], Identity, DateTimeOffset.UnixEpoch), "resource_limit");
	}

	[Fact]
	public void SnapshotStore_EnforcesCountPerItemAndAggregateLimitsWithoutEviction()
	{
		FakeMcpMonotonicClock clock = new();
		using McpMemorySnapshotStore countStore = new(clock);
		List<string> ids = [];
		for(int i = 0; i < McpAutomationLimits.MaxMemorySnapshots; i++) {
			ids.Add(AssertSuccess(countStore.Create("NES", "cpu", 0, [0], Identity, DateTimeOffset.UnixEpoch)).Id);
		}

		AssertError(countStore.Create("NES", "cpu", 0, [0], Identity, DateTimeOffset.UnixEpoch), "resource_limit");
		using McpPinnedResource<McpMemorySnapshotResource> first = AssertSuccess(countStore.Pin(ids[0]));
		Assert.Single(first.Value.Data);

		using McpMemorySnapshotStore byteStore = new(clock);
		AssertError(
			byteStore.Create("NES", "cpu", 0, new byte[McpAutomationLimits.MaxMemorySnapshotBytes + 1], Identity, DateTimeOffset.UnixEpoch),
			"resource_limit");
		for(int i = 0; i < McpAutomationLimits.MaxAggregateMemorySnapshotBytes / McpAutomationLimits.MaxMemorySnapshotBytes; i++) {
			AssertSuccess(byteStore.Create("NES", "cpu", 0, new byte[McpAutomationLimits.MaxMemorySnapshotBytes], Identity, DateTimeOffset.UnixEpoch));
		}
		AssertError(byteStore.Create("NES", "cpu", 0, [0], Identity, DateTimeOffset.UnixEpoch), "resource_limit");
	}

	[Fact]
	public void SaveStateAndSnapshotStoresOwnCopiesOfCallerData()
	{
		byte[] saveData = [1, 2, 3];
		byte[] snapshotData = [4, 5, 6];
		using McpSaveStateStore saveStates = new(new FakeMcpMonotonicClock());
		using McpMemorySnapshotStore snapshots = new(new FakeMcpMonotonicClock());
		string saveId = AssertSuccess(saveStates.Create(saveData, Identity, DateTimeOffset.UnixEpoch)).Id;
		string snapshotId = AssertSuccess(
			snapshots.Create("NES", "cpu", 0, snapshotData, Identity, DateTimeOffset.UnixEpoch)).Id;

		saveData[0] = 99;
		snapshotData[0] = 99;

		using McpPinnedResource<McpSaveStateResource> save = AssertSuccess(saveStates.Pin(saveId));
		using McpPinnedResource<McpMemorySnapshotResource> snapshot = AssertSuccess(snapshots.Pin(snapshotId));
		Assert.Equal([1, 2, 3], save.Value.Data);
		Assert.Equal([4, 5, 6], snapshot.Value.Data);
		Assert.NotSame(saveData, save.Value.Data);
		Assert.NotSame(snapshotData, snapshot.Value.Data);
	}

	[Fact]
	public void SuccessfulUseRefreshesIdleExpiration()
	{
		FakeMcpMonotonicClock clock = new();
		using McpSaveStateStore store = new(clock);
		string id = AssertSuccess(store.Create([1], Identity, DateTimeOffset.UnixEpoch)).Id;

		clock.Advance(TimeSpan.FromMinutes(29));
		AssertSuccess(store.Pin(id)).Dispose();
		clock.Advance(TimeSpan.FromMinutes(29));
		AssertSuccess(store.Pin(id)).Dispose();
		clock.Advance(TimeSpan.FromMinutes(30));

		AssertError(store.Pin(id), "resource_not_found");
	}

	[Fact]
	public void PinPreventsExpirationUntilItsIdempotentRelease()
	{
		FakeMcpMonotonicClock clock = new();
		using McpSaveStateStore store = new(clock);
		string id = AssertSuccess(store.Create([1, 2, 3], Identity, DateTimeOffset.UnixEpoch)).Id;
		McpPinnedResource<McpSaveStateResource> pin = AssertSuccess(store.Pin(id));

		clock.Advance(TimeSpan.FromMinutes(31));
		using(McpPinnedResource<McpSaveStateResource> concurrentPin = AssertSuccess(store.Pin(id))) {
			Assert.Equal(3, concurrentPin.Value.Data.Length);
		}
		clock.Advance(TimeSpan.FromMinutes(31));
		pin.Dispose();
		pin.Dispose();

		AssertError(store.Pin(id), "resource_not_found");
		Assert.Equal(0, store.RetainedBytes);
	}

	[Fact]
	public void ExplicitDeleteRemovesMetadataButDefersPinnedDataCleanup()
	{
		FakeMcpMonotonicClock clock = new();
		using McpSaveStateStore store = new(clock);
		string id = AssertSuccess(store.Create([1, 2, 3], Identity, DateTimeOffset.UnixEpoch)).Id;
		McpPinnedResource<McpSaveStateResource> pin = AssertSuccess(store.Pin(id));

		Assert.True(AssertSuccess(store.Delete(id)).Deleted);
		AssertError(store.Pin(id), "resource_not_found");
		Assert.Equal(3, store.RetainedBytes);
		Assert.Equal(3, pin.Value.Data.Length);

		pin.Dispose();
		Assert.Equal(0, store.RetainedBytes);
		AssertError(store.Delete(id), "resource_not_found");
	}

	[Fact]
	public void DeletedPinnedResourcesRemainChargedToTheCountQuotaUntilRelease()
	{
		using McpSaveStateStore store = new(new FakeMcpMonotonicClock());
		List<McpPinnedResource<McpSaveStateResource>> pins = [];
		for(int i = 0; i < McpAutomationLimits.MaxSaveStates; i++) {
			string id = AssertSuccess(store.Create([0], Identity, DateTimeOffset.UnixEpoch)).Id;
			pins.Add(AssertSuccess(store.Pin(id)));
			AssertSuccess(store.Delete(id));
		}

		AssertError(store.Create([0], Identity, DateTimeOffset.UnixEpoch), "resource_limit");
		pins[0].Dispose();
		AssertSuccess(store.Create([0], Identity, DateTimeOffset.UnixEpoch));
		foreach(McpPinnedResource<McpSaveStateResource> pin in pins) {
			pin.Dispose();
		}
	}

	[Fact]
	public void RomInvalidationIsImmediatelyStaleAndCleansPinnedDataOnRelease()
	{
		FakeMcpMonotonicClock clock = new();
		using McpMemorySnapshotStore store = new(clock);
		string id = AssertSuccess(store.Create("NES", "cpu", 0, [1, 2], Identity, DateTimeOffset.UnixEpoch)).Id;
		McpPinnedResource<McpMemorySnapshotResource> pin = AssertSuccess(store.Pin(id));

		store.InvalidateRom(Identity.RomIdentity + 1);

		AssertError(store.Pin(id), "stale_resource");
		Assert.Equal(2, store.RetainedBytes);
		Assert.Equal(2, pin.Value.Data.Length);
		pin.Dispose();
		Assert.Equal(0, store.RetainedBytes);
		AssertError(store.Pin(id), "stale_resource");

		clock.Advance(TimeSpan.FromMinutes(30));
		AssertError(store.Pin(id), "resource_not_found");
	}

	[Fact]
	public void SnapshotTopologyInvalidationRetainsStaleMetadataUntilExpiration()
	{
		FakeMcpMonotonicClock clock = new();
		using McpMemorySnapshotStore store = new(clock);
		string id = AssertSuccess(store.Create("NES", "cpu", 0, [1], Identity, DateTimeOffset.UnixEpoch)).Id;

		store.InvalidateTopology(resource => resource.Metadata.Space == "cpu");

		AssertError(store.Pin(id), "stale_resource");
		clock.Advance(TimeSpan.FromMinutes(30));
		AssertError(store.Pin(id), "resource_not_found");
	}

	[Fact]
	public void SaveStatesAreScopedOnlyToRomIdentity()
	{
		using McpSaveStateStore store = new(new FakeMcpMonotonicClock());
		string id = AssertSuccess(store.Create([1], Identity, DateTimeOffset.UnixEpoch)).Id;

		store.InvalidateRom(Identity.RomIdentity);
		AssertSuccess(store.Pin(id)).Dispose();
		store.InvalidateRom(Identity.RomIdentity + 1);
		AssertError(store.Pin(id), "stale_resource");
	}

	[Fact]
	public void DisposeDefersPinnedDataCleanupAndRejectsFurtherUse()
	{
		FakeMcpMonotonicClock clock = new();
		McpSaveStateStore store = new(clock);
		string id = AssertSuccess(store.Create([1, 2], Identity, DateTimeOffset.UnixEpoch)).Id;
		McpPinnedResource<McpSaveStateResource> pin = AssertSuccess(store.Pin(id));

		store.Dispose();
		store.Dispose();

		Assert.Equal(2, store.RetainedBytes);
		Assert.Throws<ObjectDisposedException>(() => store.Pin(id));
		pin.Dispose();
		Assert.Equal(0, store.RetainedBytes);
	}

	[Fact]
	public void ResourceIdsAreUniqueOpaqueValues()
	{
		using McpSaveStateStore store = new(new FakeMcpMonotonicClock());
		string first = AssertSuccess(store.Create([0], Identity, DateTimeOffset.UnixEpoch)).Id;
		string second = AssertSuccess(store.Create([0], Identity, DateTimeOffset.UnixEpoch)).Id;

		Assert.NotEqual(first, second);
		Assert.Equal(32, first.Length);
		Assert.False(long.TryParse(first, out _));
	}

	[Fact]
	public void SearchAllocationUsesActualRetainedArrayLengthsIncludingUndo()
	{
		byte[] sharedSnapshot = new byte[5];
		int[] sharedCandidates = new int[7];
		McpMemorySearchState state = new(new byte[3], sharedSnapshot, sharedCandidates, 1, 2);
		McpMemorySearchState undo = new(sharedSnapshot, new byte[13], sharedCandidates, 0, 1);
		McpMemorySearchResource resource = SearchResource(state, undo);

		Assert.Equal(3 + 5 + (7 * sizeof(int)) + 13, resource.AllocationBytes);
	}

	[Fact]
	public void SearchStoreOwnsOneCloneOfEveryAddArrayAndPreservesAliases()
	{
		byte[] previous = [1, 2];
		byte[] sharedSnapshot = [3, 4];
		int[] sharedCandidates = [5, 6];
		byte[] undoCurrent = [7, 8];
		McpMemorySearchState state = new(previous, sharedSnapshot, sharedCandidates, 1, 2);
		McpMemorySearchState undo = new(sharedSnapshot, undoCurrent, sharedCandidates, 0, 1);
		using McpMemorySearchStore store = new(new FakeMcpMonotonicClock());
		string id = AssertSuccess(store.Create(SearchResource(state, undo)));

		previous[0] = 99;
		sharedSnapshot[0] = 99;
		sharedCandidates[0] = 99;
		undoCurrent[0] = 99;

		using McpPinnedResource<McpMemorySearchResource> pin = AssertSuccess(store.Pin(id));
		Assert.Equal([1, 2], pin.Value.State.PreviousSnapshot);
		Assert.Equal([3, 4], pin.Value.State.CurrentSnapshot);
		Assert.Equal([5, 6], pin.Value.State.CandidateOffsets);
		Assert.Equal([7, 8], pin.Value.UndoState!.CurrentSnapshot);
		Assert.Same(pin.Value.State.CurrentSnapshot, pin.Value.UndoState.PreviousSnapshot);
		Assert.Same(pin.Value.State.CandidateOffsets, pin.Value.UndoState.CandidateOffsets);
		Assert.Equal(2 + 2 + (2 * sizeof(int)) + 2, pin.Value.AllocationBytes);
	}

	[Fact]
	public void SearchStoreOwnsOneCloneOfEveryReplacementArrayAndPreservesAliases()
	{
		using McpMemorySearchStore store = new(new FakeMcpMonotonicClock());
		string id = AssertSuccess(store.Create(SearchResource(new([1], [2], [3], 0, 1))));
		byte[] previous = [4, 5];
		byte[] sharedSnapshot = [6, 7];
		int[] sharedCandidates = [8, 9];
		byte[] undoCurrent = [10, 11];
		McpMemorySearchState state = new(previous, sharedSnapshot, sharedCandidates, 2, 3);
		McpMemorySearchState undo = new(sharedSnapshot, undoCurrent, sharedCandidates, 1, 2);

		AssertSuccess(store.Replace(id, SearchResource(state, undo)));
		previous[0] = 99;
		sharedSnapshot[0] = 99;
		sharedCandidates[0] = 99;
		undoCurrent[0] = 99;

		using McpPinnedResource<McpMemorySearchResource> pin = AssertSuccess(store.Pin(id));
		Assert.Equal([4, 5], pin.Value.State.PreviousSnapshot);
		Assert.Equal([6, 7], pin.Value.State.CurrentSnapshot);
		Assert.Equal([8, 9], pin.Value.State.CandidateOffsets);
		Assert.Equal([10, 11], pin.Value.UndoState!.CurrentSnapshot);
		Assert.Same(pin.Value.State.CurrentSnapshot, pin.Value.UndoState.PreviousSnapshot);
		Assert.Same(pin.Value.State.CandidateOffsets, pin.Value.UndoState.CandidateOffsets);
	}

	[Fact]
	public void SearchStoreEnforcesRangeCountAndAllocationLimits()
	{
		FakeMcpMonotonicClock clock = new();
		using McpMemorySearchStore countStore = new(clock);
		for(int i = 0; i < McpAutomationLimits.MaxMemorySearches; i++) {
			AssertSuccess(countStore.Create(SearchResource(new([0], [0], [0], 0, 0))));
		}
		AssertError(countStore.Create(SearchResource(new([0], [0], [0], 0, 0))), "resource_limit");

		using McpMemorySearchStore rangeStore = new(clock);
		McpMemorySearchResource excessiveRange = SearchResource(
			new([0], [0], [0], 0, 0),
			count: McpAutomationLimits.MaxSearchRangeBytes + 1);
		AssertError(rangeStore.Create(excessiveRange), "resource_limit");

		McpMemorySearchState excessiveAllocation = new(
			new byte[McpAutomationLimits.MaxSearchRangeBytes],
			new byte[McpAutomationLimits.MaxSearchRangeBytes],
			new int[(McpAutomationLimits.MaxSearchAllocationBytes / sizeof(int)) -
				(McpAutomationLimits.MaxSearchRangeBytes * 2 / sizeof(int)) + 1],
			0,
			0);
		AssertError(rangeStore.Create(SearchResource(excessiveAllocation)), "resource_limit");
	}

	[Fact]
	public void FailedSearchReplacementIsAtomicAndDoesNotEvictCurrentState()
	{
		FakeMcpMonotonicClock clock = new();
		using McpMemorySearchStore store = new(clock);
		McpMemorySearchResource initial = SearchResource(new([1], [2], [3], 1, 2));
		string id = AssertSuccess(store.Create(initial));
		McpMemorySearchState excessive = new(
			new byte[McpAutomationLimits.MaxSearchRangeBytes],
			new byte[McpAutomationLimits.MaxSearchRangeBytes],
			new int[(McpAutomationLimits.MaxSearchAllocationBytes / sizeof(int)) -
				(McpAutomationLimits.MaxSearchRangeBytes * 2 / sizeof(int)) + 1],
			2,
			3);

		AssertError(store.Replace(id, SearchResource(excessive)), "resource_limit");

		using McpPinnedResource<McpMemorySearchResource> pin = AssertSuccess(store.Pin(id));
		Assert.NotSame(initial, pin.Value);
		Assert.Equal(initial.State.CurrentSnapshot, pin.Value.State.CurrentSnapshot);
		Assert.Equal(2, pin.Value.State.MutableStateGeneration);
	}

	[Fact]
	public void FailedSearchReplacementCloneCannotMutateInstalledState()
	{
		using McpMemorySearchStore store = new(new FakeMcpMonotonicClock());
		McpMemorySearchResource initial = SearchResource(new([1], [2], [3], 1, 2));
		string id = AssertSuccess(store.Create(initial));
		long retainedBytes = store.RetainedBytes;
		byte[] rejectedPrevious = new byte[McpAutomationLimits.MaxSearchRangeBytes];
		byte[] rejectedCurrent = new byte[McpAutomationLimits.MaxSearchRangeBytes];
		int[] rejectedCandidates = new int[(McpAutomationLimits.MaxSearchAllocationBytes / sizeof(int)) -
			(McpAutomationLimits.MaxSearchRangeBytes * 2 / sizeof(int)) + 1];
		McpMemorySearchResource rejected = SearchResource(
			new(rejectedPrevious, rejectedCurrent, rejectedCandidates, 2, 3));

		AssertError(store.Replace(id, rejected), "resource_limit");
		Assert.Equal(retainedBytes, store.RetainedBytes);
		rejectedPrevious[0] = 99;
		rejectedCurrent[0] = 99;
		rejectedCandidates[0] = 99;

		using McpPinnedResource<McpMemorySearchResource> pin = AssertSuccess(store.Pin(id));
		Assert.Equal([1], pin.Value.State.PreviousSnapshot);
		Assert.Equal([2], pin.Value.State.CurrentSnapshot);
		Assert.Equal([3], pin.Value.State.CandidateOffsets);
	}

	[Fact]
	public void SearchReplacementAccountsForPinnedOldArraysUntilRelease()
	{
		FakeMcpMonotonicClock clock = new();
		using McpMemorySearchStore store = new(clock);
		McpMemorySearchResource initial = SearchResource(new(new byte[10], new byte[20], new int[5], 1, 2));
		string id = AssertSuccess(store.Create(initial));
		McpPinnedResource<McpMemorySearchResource> oldPin = AssertSuccess(store.Pin(id));
		McpMemorySearchResource replacement = SearchResource(new(new byte[7], new byte[9], new int[3], 2, 3));

		Assert.True(AssertSuccess(store.Replace(id, replacement)));
		Assert.Equal(initial.AllocationBytes + replacement.AllocationBytes, store.RetainedBytes);
		using(McpPinnedResource<McpMemorySearchResource> currentPin = AssertSuccess(store.Pin(id))) {
			Assert.NotSame(replacement, currentPin.Value);
			Assert.Equal(replacement.State.CurrentSnapshot, currentPin.Value.State.CurrentSnapshot);
		}

		oldPin.Dispose();
		Assert.Equal(replacement.AllocationBytes, store.RetainedBytes);
	}

	[Fact]
	public void SearchReplacementOwnsArraysIndependentlyFromPinnedVersion()
	{
		using McpMemorySearchStore store = new(new FakeMcpMonotonicClock());
		byte[] shared = new byte[20];
		McpMemorySearchResource initial = SearchResource(new(new byte[10], shared, new int[5], 1, 2));
		string id = AssertSuccess(store.Create(initial));
		McpPinnedResource<McpMemorySearchResource> oldPin = AssertSuccess(store.Pin(id));
		McpMemorySearchResource replacement = SearchResource(new(shared, new byte[9], new int[3], 2, 3));

		AssertSuccess(store.Replace(id, replacement));

		using(McpPinnedResource<McpMemorySearchResource> replacementPin = AssertSuccess(store.Pin(id))) {
			Assert.NotSame(oldPin.Value.State.CurrentSnapshot, replacementPin.Value.State.PreviousSnapshot);
			Assert.Equal(shared, replacementPin.Value.State.PreviousSnapshot);
		}
		Assert.Equal(initial.AllocationBytes + replacement.AllocationBytes, store.RetainedBytes);
		oldPin.Dispose();
		Assert.Equal(replacement.AllocationBytes, store.RetainedBytes);
	}

	[Fact]
	public void SearchStoreEnforcesAggregateAllocationWithoutEviction()
	{
		using McpMemorySearchStore store = new(new FakeMcpMonotonicClock());
		List<string> ids = [];
		for(int i = 0; i < McpAutomationLimits.MaxAggregateSearchAllocationBytes /
			McpAutomationLimits.MaxSearchAllocationBytes; i++) {
			McpMemorySearchState fullAllocation = new(
				new byte[McpAutomationLimits.MaxSearchRangeBytes],
				new byte[McpAutomationLimits.MaxSearchRangeBytes],
				new int[(McpAutomationLimits.MaxSearchAllocationBytes -
					(McpAutomationLimits.MaxSearchRangeBytes * 2)) / sizeof(int)],
				0,
				0);
			ids.Add(AssertSuccess(store.Create(SearchResource(fullAllocation))));
		}

		AssertError(store.Create(SearchResource(new([0], [0], [0], 0, 0))), "resource_limit");
		McpPinnedResource<McpMemorySearchResource> first = AssertSuccess(store.Pin(ids[0]));
		McpMemorySearchResource smallReplacement = SearchResource(new([0], [0], [0], 1, 2));
		AssertError(store.Replace(ids[0], smallReplacement), "resource_limit");
		Assert.Equal(McpAutomationLimits.MaxSearchAllocationBytes, first.Value.AllocationBytes);
		first.Dispose();
		AssertSuccess(store.Replace(ids[0], smallReplacement));
		using McpPinnedResource<McpMemorySearchResource> replacement = AssertSuccess(store.Pin(ids[0]));
		Assert.NotSame(smallReplacement, replacement.Value);
		Assert.Equal(smallReplacement.State.CurrentSnapshot, replacement.Value.State.CurrentSnapshot);
	}

	[Fact]
	public void SearchTopologyInvalidationRetainsStaleMetadataUntilExpiration()
	{
		FakeMcpMonotonicClock clock = new();
		using McpMemorySearchStore store = new(clock);
		string id = AssertSuccess(store.Create(SearchResource(new([1], [2], [3], 1, 2))));

		store.InvalidateTopology(resource => resource.Space == "cpu");

		AssertError(store.Pin(id), "stale_resource");
		clock.Advance(TimeSpan.FromMinutes(30));
		AssertError(store.Pin(id), "resource_not_found");
	}

	private static McpMemorySearchResource SearchResource(
		McpMemorySearchState state,
		McpMemorySearchState? undo = null,
		int? count = null) => new(
			"NES",
			"cpu",
			0,
			count ?? state.CurrentSnapshot.Length,
			1,
			false,
			"little",
			1,
			Identity,
			state,
			undo);

	private static T AssertSuccess<T>(McpServiceResult<T> result)
	{
		Assert.True(result.IsSuccess, result.Error?.Message);
		return Assert.IsType<T>(result.Value);
	}

	private static void AssertError<T>(McpServiceResult<T> result, string code)
	{
		Assert.False(result.IsSuccess);
		Assert.Equal(code, result.Error?.Code);
	}

	private sealed class FakeMcpMonotonicClock : IMcpMonotonicClock
	{
		private long _timestamp;

		public long GetTimestamp() => _timestamp;
		public TimeSpan GetElapsedTime(long start, long end) => TimeSpan.FromTicks(end - start);
		internal void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
	}
}
