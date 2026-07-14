using Mesen.Interop;
using Mesen.Mcp;

namespace UI.Tests.Mcp;

public sealed class McpMemorySearchServiceTests
{
	public static TheoryData<int, bool, string, byte[], long[]> DecodeCases => new() {
		{ 1, false, "little", [0x00, 0x7F, 0x80, 0xFF], [0, 127, 128, 255] },
		{ 1, true, "big", [0x00, 0x7F, 0x80, 0xFF], [0, 127, -128, -1] },
		{ 2, false, "little", [0x34, 0x12, 0xFE, 0xFF], [0x1234, 0xFFFE] },
		{ 2, true, "big", [0x12, 0x34, 0xFF, 0xFE], [0x1234, -2] },
		{ 4, false, "little", [0x78, 0x56, 0x34, 0x12, 0xFF, 0xFF, 0xFF, 0xFF], [0x12345678, 4294967295] },
		{ 4, true, "big", [0x12, 0x34, 0x56, 0x78, 0xFF, 0xFF, 0xFF, 0xFE], [0x12345678, -2] }
	};

	[Theory]
	[MemberData(nameof(DecodeCases))]
	public void StartMemorySearch_DecodesWidthSignednessAndByteOrder(
		int width, bool signed, string byteOrder, byte[] bytes, long[] values)
	{
		using SearchFixture fixture = new(0x1000 + bytes.Length, bytes);

		StartMemorySearchResult started = AssertSuccess(fixture.Service.StartMemorySearch(
			nameof(MemoryType.NesWorkRam), 0x1000, bytes.Length, width, signed, byteOrder, width, null));
		GetMemorySearchResultsResult results = AssertSuccess(
			fixture.Service.GetMemorySearchResults(started.Id, 0, 1000));

		Assert.Equal(values, results.Candidates.Select(candidate => candidate.CurrentValue));
		Assert.Equal(values, results.Candidates.Select(candidate => candidate.PreviousValue));
		Assert.Equal(Enumerable.Range(0, values.Length).Select(i => 0x1000U + (uint)(i * width)),
			results.Candidates.Select(candidate => candidate.Address));
	}

	[Fact]
	public void StartMemorySearch_UsesStrideAndRequiresTheCompleteValueToFit()
	{
		using SearchFixture fixture = new(17, [1, 2, 3, 4, 5, 6, 7]);

		StartMemorySearchResult result = AssertSuccess(fixture.Service.StartMemorySearch(
			nameof(MemoryType.NesWorkRam), 10, 7, 4, false, "little", 3, null));

		Assert.Equal(2, result.CandidateCount);
		Assert.Equal([10U, 13U], AssertSuccess(fixture.Service.GetMemorySearchResults(result.Id, 0, 10))
			.Candidates.Select(candidate => candidate.Address));
	}

	[Fact]
	public void StartMemorySearch_OptionalInitialValueFiltersExactMatches()
	{
		using SearchFixture fixture = new(5, [2, 1, 2, 3, 2]);

		StartMemorySearchResult result = AssertSuccess(fixture.Service.StartMemorySearch(
			nameof(MemoryType.NesWorkRam), 0, 5, 1, false, "little", 1, 2));

		Assert.Equal(3, result.CandidateCount);
		Assert.Equal([0U, 2U, 4U], AssertSuccess(fixture.Service.GetMemorySearchResults(result.Id, 0, 10))
			.Candidates.Select(candidate => candidate.Address));
	}

	[Theory]
	[InlineData("exact", 12L, null, 1)]
	[InlineData("not_equal", 12L, null, 3)]
	[InlineData("increased", null, null, 2)]
	[InlineData("decreased", null, null, 1)]
	[InlineData("changed", null, null, 3)]
	[InlineData("unchanged", null, null, 1)]
	[InlineData("increased_by", null, 2L, 1)]
	[InlineData("decreased_by", null, 2L, 1)]
	public void RefineMemorySearch_AppliesEveryComparison(
		string comparison, long? value, long? delta, int expectedCount)
	{
		using SearchFixture fixture = new(4, [10, 10, 10, 10]);
		string id = Start(fixture, count: 4);
		fixture.Api.ReadData = [12, 8, 10, 13];

		RefineMemorySearchResult result = AssertSuccess(
			fixture.Service.RefineMemorySearch(id, comparison, value, delta));

		Assert.Equal(4, result.PreviousCandidateCount);
		Assert.Equal(expectedCount, result.CandidateCount);
	}

	[Theory]
	[InlineData("unknown", null, null)]
	[InlineData("exact", null, null)]
	[InlineData("exact", 1L, 1L)]
	[InlineData("changed", 1L, null)]
	[InlineData("changed", null, 1L)]
	[InlineData("increased_by", null, null)]
	[InlineData("increased_by", null, 0L)]
	[InlineData("decreased_by", null, -1L)]
	public void RefineMemorySearch_ValidatesComparisonOperandsBeforeNativeCapture(
		string comparison, long? value, long? delta)
	{
		using SearchFixture fixture = new(1, [1]);
		string id = Start(fixture);
		int calls = fixture.Api.GetMemoryValuesCalls;

		AssertError(fixture.Service.RefineMemorySearch(id, comparison, value, delta), "invalid_comparison");
		Assert.Equal(calls, fixture.Api.GetMemoryValuesCalls);
	}

	[Theory]
	[InlineData(1, false, 256)]
	[InlineData(1, true, 128)]
	[InlineData(1, true, -129)]
	[InlineData(2, false, -1)]
	[InlineData(4, false, 4294967296)]
	public void StartMemorySearch_RejectsUnrepresentableInitialValuesBeforeNativeCapture(
		int width, bool signed, long value)
	{
		using SearchFixture fixture = new(4, [0, 0, 0, 0]);

		AssertError(fixture.Service.StartMemorySearch(
			nameof(MemoryType.NesWorkRam), 0, 4, width, signed, "little", 1, value), "invalid_value");
		Assert.Equal(0, fixture.Api.GetMemoryValuesCalls);
	}

	[Fact]
	public void IncreasedAndDecreasedBy_DoNotMatchArithmeticOverflow()
	{
		using SearchFixture unsignedFixture = new(4, [0xFF, 0xFF, 0xFF, 0xFF]);
		string unsignedId = AssertSuccess(unsignedFixture.Service.StartMemorySearch(
			nameof(MemoryType.NesWorkRam), 0, 4, 4, false, "little", 4, null)).Id;
		unsignedFixture.Api.ReadData = [0, 0, 0, 0];
		Assert.Equal(0, AssertSuccess(unsignedFixture.Service.RefineMemorySearch(
			unsignedId, "increased_by", null, 1)).CandidateCount);

		using SearchFixture signedFixture = new(1, [0x80]);
		string signedId = AssertSuccess(signedFixture.Service.StartMemorySearch(
			nameof(MemoryType.NesWorkRam), 0, 1, 1, true, "little", 1, null)).Id;
		signedFixture.Api.ReadData = [0x7F];
		Assert.Equal(0, AssertSuccess(signedFixture.Service.RefineMemorySearch(
			signedId, "decreased_by", null, 1)).CandidateCount);
	}

	[Fact]
	public void StartAndRefine_RequireStoppedExecutionAndValidateRangeTopologyBeforeCapture()
	{
		bool stopped = false;
		using SearchFixture fixture = new(8, [0], () => stopped);
		AssertError(fixture.Service.StartMemorySearch(nameof(MemoryType.NesWorkRam), 0, 1, 1, false, "little", 1, null),
			"debugger_unavailable");
		stopped = true;
		AssertError(fixture.Service.StartMemorySearch(nameof(MemoryType.NesWorkRam), 0, 0, 1, false, "little", 1, null),
			"invalid_range");
		AssertError(fixture.Service.StartMemorySearch(nameof(MemoryType.NesWorkRam), 7, 2, 1, false, "little", 1, null),
			"invalid_range");
		AssertError(fixture.Service.StartMemorySearch("nesWorkRam", 0, 1, 1, false, "little", 1, null),
			"unknown_memory_space");
		AssertError(fixture.Service.StartMemorySearch(nameof(MemoryType.NesWorkRam), 0, 1, 3, false, "little", 1, null),
			"invalid_width");
		AssertError(fixture.Service.StartMemorySearch(nameof(MemoryType.NesWorkRam), 0, 1, 1, false, "native", 1, null),
			"invalid_byte_order");
		AssertError(fixture.Service.StartMemorySearch(nameof(MemoryType.NesWorkRam), 0, 1, 1, false, "little", 0, null),
			"invalid_stride");
		Assert.Equal(0, fixture.Api.GetMemoryValuesCalls);

		fixture.Api.ReadData = [1];
		string id = Start(fixture);
		stopped = false;
		AssertError(fixture.Service.RefineMemorySearch(id, "changed", null, null), "debugger_unavailable");
	}

	[Fact]
	public void StartMemorySearch_AcceptsTheExactRangeLimitAndRejectsOneByteMoreBeforeCapture()
	{
		byte[] data = new byte[McpAutomationLimits.MaxSearchRangeBytes];
		using SearchFixture fixture = new(McpAutomationLimits.MaxSearchRangeBytes + 1, data);

		StartMemorySearchResult exact = AssertSuccess(fixture.Service.StartMemorySearch(
			nameof(MemoryType.NesWorkRam), 0, McpAutomationLimits.MaxSearchRangeBytes,
			4, false, "little", 4, null));
		Assert.Equal(McpAutomationLimits.MaxSearchRangeBytes / 4, exact.CandidateCount);
		int calls = fixture.Api.GetMemoryValuesCalls;

		AssertError(fixture.Service.StartMemorySearch(
			nameof(MemoryType.NesWorkRam), 0, McpAutomationLimits.MaxSearchRangeBytes + 1,
			4, false, "little", 4, null), "resource_limit");
		Assert.Equal(calls, fixture.Api.GetMemoryValuesCalls);
	}

	[Fact]
	public void StartMemorySearch_RejectsTheNinthSessionBeforeCapture()
	{
		using SearchFixture fixture = new(1, [0]);
		for(int index = 0; index < McpAutomationLimits.MaxMemorySearches; index++) {
			Start(fixture);
		}
		int calls = fixture.Api.GetMemoryValuesCalls;

		AssertError(fixture.Service.StartMemorySearch(
			nameof(MemoryType.NesWorkRam), 0, 1, 1, false, "little", 1, null), "resource_limit");
		Assert.Equal(calls, fixture.Api.GetMemoryValuesCalls);
	}

	[Fact]
	public void RefineMemorySearch_IsAtomicAndProvidesExactlyOneUndoState()
	{
		using SearchFixture fixture = new(3, [1, 2, 3]);
		string id = Start(fixture, count: 3);
		fixture.Identity.NotifyMutableStateChanged();
		fixture.Api.ReadData = [1, 4, 5];
		RefineMemorySearchResult refined = AssertSuccess(
			fixture.Service.RefineMemorySearch(id, "changed", null, null));
		Assert.Equal(2, refined.CandidateCount);
		Assert.Equal([1U, 2U], AssertSuccess(fixture.Service.GetMemorySearchResults(id, 0, 10))
			.Candidates.Select(candidate => candidate.Address));

		UndoMemorySearchResult undo = AssertSuccess(fixture.Service.UndoMemorySearch(id));
		Assert.Equal(3, undo.CandidateCount);
		AssertError(fixture.Service.UndoMemorySearch(id), "undo_unavailable");
	}

	[Fact]
	public void FailedRefineCaptureLeavesInstalledSessionUntouched()
	{
		using SearchFixture fixture = new(2, [1, 2]);
		string id = Start(fixture, count: 2);
		fixture.Api.ReadData = [9];

		AssertError(fixture.Service.RefineMemorySearch(id, "changed", null, null), "interop_failure");
		GetMemorySearchResultsResult results = AssertSuccess(fixture.Service.GetMemorySearchResults(id, 0, 10));
		Assert.Equal(2, results.CandidateCount);
		Assert.Equal([1L, 2L], results.Candidates.Select(candidate => candidate.CurrentValue));
		AssertError(fixture.Service.UndoMemorySearch(id), "undo_unavailable");
	}

	[Fact]
	public void GetMemorySearchResults_ReturnsAscendingPagesAndGenerationValues()
	{
		const int count = 1002;
		byte[] before = Enumerable.Repeat((byte)1, count).ToArray();
		using SearchFixture fixture = new(count, before);
		string id = Start(fixture, count: count);
		fixture.Identity.NotifyMutableStateChanged();
		fixture.Api.ReadData = Enumerable.Repeat((byte)2, count).ToArray();
		AssertSuccess(fixture.Service.RefineMemorySearch(id, "changed", null, null));

		GetMemorySearchResultsResult first = AssertSuccess(fixture.Service.GetMemorySearchResults(id, 0, 1000));
		Assert.Equal(count, first.CandidateCount);
		Assert.Equal(1000, first.Candidates.Count);
		Assert.Equal(1000, first.NextOffset);
		Assert.Equal(0U, first.Candidates[0].Address);
		Assert.Equal(999U, first.Candidates[^1].Address);
		Assert.All(first.Candidates, candidate => Assert.Equal((1L, 2L), (candidate.PreviousValue, candidate.CurrentValue)));
		Assert.Equal(0, first.PreviousMutableStateGeneration);
		Assert.Equal(1, first.MutableStateGeneration);

		GetMemorySearchResultsResult last = AssertSuccess(fixture.Service.GetMemorySearchResults(id, 1000, 1000));
		Assert.Equal([1000U, 1001U], last.Candidates.Select(candidate => candidate.Address));
		Assert.Null(last.NextOffset);
	}

	[Theory]
	[InlineData(-1, 1, "invalid_range")]
	[InlineData(0, 0, "invalid_range")]
	[InlineData(0, 1001, "payload_too_large")]
	public void GetMemorySearchResults_ValidatesPaging(int offset, int limit, string error)
	{
		using SearchFixture fixture = new(1, [0]);
		string id = Start(fixture);
		AssertError(fixture.Service.GetMemorySearchResults(id, offset, limit), error);
	}

	[Fact]
	public void CompatibleStateLoadsRemainValidButRomSystemSpaceAndSizeChangesStaleSessions()
	{
		using SearchFixture fixture = new(4, [1, 2, 3, 4]);
		string compatible = Start(fixture, count: 4);
		fixture.Identity.NotifyStateLoaded();
		AssertSuccess(fixture.Service.GetMemorySearchResults(compatible, 0, 1));

		fixture.Identity.NotifyRomChanged();
		AssertError(fixture.Service.GetMemorySearchResults(compatible, 0, 1), "stale_resource");

		using SearchFixture sizeFixture = new(4, [1, 2, 3, 4]);
		string sizeId = Start(sizeFixture, count: 4);
		sizeFixture.Api.MemorySizes[MemoryType.NesWorkRam] = 5;
		AssertError(sizeFixture.Service.GetMemorySearchResults(sizeId, 0, 1), "stale_resource");

		using SearchFixture systemFixture = new(4, [1, 2, 3, 4]);
		string systemId = Start(systemFixture, count: 4);
		systemFixture.Api.GetRomInfoHandler = () => new RomInfo { ConsoleType = ConsoleType.Snes };
		AssertError(systemFixture.Service.GetMemorySearchResults(systemId, 0, 1), "stale_resource");

		using SearchFixture spaceFixture = new(4, [1, 2, 3, 4]);
		string spaceId = Start(spaceFixture, count: 4);
		spaceFixture.Api.MemorySizes[MemoryType.NesWorkRam] = 0;
		AssertError(spaceFixture.Service.GetMemorySearchResults(spaceId, 0, 1), "stale_resource");
	}

	[Fact]
	public void DeleteMemorySearch_RemovesTheSession()
	{
		using SearchFixture fixture = new(1, [0]);
		string id = Start(fixture);
		Assert.True(AssertSuccess(fixture.Service.DeleteMemorySearch(id)).Deleted);
		AssertError(fixture.Service.GetMemorySearchResults(id, 0, 1), "resource_not_found");
	}

	private static string Start(SearchFixture fixture, int count = 1) => AssertSuccess(
		fixture.Service.StartMemorySearch(
			nameof(MemoryType.NesWorkRam), 0, count, 1, false, "little", 1, null)).Id;

	private static T AssertSuccess<T>(McpServiceResult<T> result)
	{
		Assert.True(result.IsSuccess, result.Error?.Code);
		return result.Value!;
	}

	private static void AssertError<T>(McpServiceResult<T> result, string code)
	{
		Assert.False(result.IsSuccess);
		Assert.Equal(code, result.Error?.Code);
	}

	private sealed class SearchFixture : IDisposable
	{
		internal SearchFixture(int memorySize, byte[] readData, Func<bool>? isStopped = null)
		{
			Api = FakeMcpEmulatorApi.RunningNes();
			Api.MemorySizes[MemoryType.NesWorkRam] = memorySize;
			Api.ReadData = readData;
			Api.IsExecutionStoppedHandler = isStopped;
			Identity = new();
			Emulator = new(Api, emulatorIdentity: Identity);
			Store = new();
			Service = new(Emulator, Store);
		}

		internal FakeMcpEmulatorApi Api { get; }
		internal McpEmulatorIdentity Identity { get; }
		internal McpEmulatorService Emulator { get; }
		internal McpMemorySearchStore Store { get; }
		internal McpMemorySearchService Service { get; }

		public void Dispose()
		{
			Store.Dispose();
			Emulator.Dispose();
		}
	}
}
