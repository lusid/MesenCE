using Mesen.Interop;
using Mesen.Mcp;

namespace UI.Tests.Mcp;

public sealed class McpMemorySnapshotServiceTests
{
	[Fact]
	public void CreateMemorySnapshot_RequiresStoppedExecutionAndValidatesBeforeReading()
	{
		bool stopped = false;
		using SnapshotFixture fixture = new(32, [1], () => stopped);

		AssertError(fixture.Service.CreateMemorySnapshot(nameof(MemoryType.NesWorkRam), 0, 1), "debugger_unavailable");
		stopped = true;
		AssertError(fixture.Service.CreateMemorySnapshot(nameof(MemoryType.NesWorkRam), 0, 0), "invalid_range");
		AssertError(fixture.Service.CreateMemorySnapshot(nameof(MemoryType.NesWorkRam), uint.MaxValue, 2), "invalid_range");
		AssertError(fixture.Service.CreateMemorySnapshot(nameof(MemoryType.NesWorkRam), 31, 2), "invalid_range");
		AssertError(fixture.Service.CreateMemorySnapshot("nesWorkRam", 0, 1), "unknown_memory_space");
		Assert.Equal(0, fixture.Api.GetMemoryValuesCalls);
	}

	[Fact]
	public void CreateMemorySnapshot_RejectsItemAndStoreQuotasBeforeNativeRead()
	{
		using SnapshotFixture oversized = new(int.MaxValue, [0]);
		AssertError(
			oversized.Service.CreateMemorySnapshot(
				nameof(MemoryType.NesWorkRam), 0, McpAutomationLimits.MaxMemorySnapshotBytes + 1),
			"resource_limit");
		Assert.Equal(0, oversized.Api.GetMemoryValuesCalls);

		using SnapshotFixture full = new(1, [0]);
		for(int i = 0; i < McpAutomationLimits.MaxMemorySnapshots; i++) {
			AssertSuccess(full.Store.Create("Nes", nameof(MemoryType.NesWorkRam), 0, [0], new(1, i), DateTimeOffset.UnixEpoch));
		}
		AssertError(full.Service.CreateMemorySnapshot(nameof(MemoryType.NesWorkRam), 0, 1), "resource_limit");
		Assert.Equal(0, full.Api.GetMemoryValuesCalls);
	}

	[Fact]
	public void CreateMemorySnapshot_ReadsBeyondPublicTransferLimitAndStoresCoherentMetadataAndOwnedBytes()
	{
		int count = McpEmulatorService.MaxTransferSize + 1;
		byte[] nativeData = Enumerable.Range(0, count).Select(i => (byte)i).ToArray();
		using SnapshotFixture fixture = new(count, nativeData);
		fixture.Identity.NotifyRomChanged();
		fixture.Identity.NotifyMutableStateChanged();

		CreateMemorySnapshotResult result = AssertSuccess(
			fixture.Service.CreateMemorySnapshot(nameof(MemoryType.NesWorkRam), 0, count));

		Assert.Equal("Nes", result.Snapshot.System);
		Assert.Equal(nameof(MemoryType.NesWorkRam), result.Snapshot.Space);
		Assert.Equal(0U, result.Snapshot.Address);
		Assert.Equal(count, result.Snapshot.Count);
		Assert.Equal(fixture.Identity.Current.RomIdentity, result.Snapshot.RomIdentity);
		Assert.Equal(fixture.Identity.Current.MutableStateGeneration, result.Snapshot.MutableStateGeneration);
		Assert.Equal(1, fixture.Api.GetMemoryValuesCalls);
		Assert.Equal((uint)(count - 1), fixture.Api.LastReadEndInclusive);

		nativeData[0] = 0xFF;
		using McpPinnedResource<McpMemorySnapshotResource> pin = AssertSuccess(fixture.Store.Pin(result.Snapshot.Id));
		Assert.Equal(0, pin.Value.Data[0]);
		Assert.NotSame(nativeData, pin.Value.Data);
	}

	[Fact]
	public void CompareMemorySnapshots_RequiresStoppedExecutionAndCompatibleImmutableFieldsButAllowsGenerationChanges()
	{
		bool stopped = true;
		using SnapshotFixture fixture = new(8, [0], () => stopped);
		string first = Add(fixture, [1, 2], new(7, 10));
		string second = Add(fixture, [1, 3], new(7, 11));

		CompareMemorySnapshotsResult result = AssertSuccess(
			fixture.Service.CompareMemorySnapshots(first, second, 0, 10, 1));
		Assert.Equal(10, result.FirstMutableStateGeneration);
		Assert.Equal(11, result.SecondMutableStateGeneration);

		stopped = false;
		AssertError(fixture.Service.CompareMemorySnapshots(first, second, 0, 10, 1), "debugger_unavailable");
		stopped = true;
		string wrongRom = Add(fixture, [1, 3], new(8, 11));
		string wrongSystem = Add(fixture, [1, 3], new(7, 11), system: "Snes");
		string wrongSpace = Add(fixture, [1, 3], new(7, 11), space: nameof(MemoryType.NesInternalRam));
		string wrongStart = Add(fixture, [1, 3], new(7, 11), address: 1);
		string wrongCount = Add(fixture, [1, 3, 4], new(7, 11));
		foreach(string incompatible in new[] { wrongRom, wrongSystem, wrongSpace, wrongStart, wrongCount }) {
			AssertError(fixture.Service.CompareMemorySnapshots(first, incompatible, 0, 10, 1), "incompatible_snapshots");
		}
	}

	[Fact]
	public void CompareMemorySnapshots_ReturnsNoRunsForEqualSnapshots()
	{
		using SnapshotFixture fixture = new(4, [0]);
		string first = Add(fixture, [1, 2, 3], new(1, 1));
		string second = Add(fixture, [1, 2, 3], new(1, 2));

		CompareMemorySnapshotsResult result = AssertSuccess(
			fixture.Service.CompareMemorySnapshots(first, second, 0, 1000, 64));

		Assert.Equal(0, result.ChangedBytes);
		Assert.Equal(0, result.ChangedRuns);
		Assert.Empty(result.Runs);
		Assert.Null(result.NextOffset);
	}

	[Fact]
	public void CompareMemorySnapshots_CountsContiguousRunsAndReturnsAscendingAbsoluteAddressesAndSamples()
	{
		using SnapshotFixture fixture = new(80, [0]);
		byte[] before = new byte[80];
		byte[] after = new byte[80];
		for(int i = 1; i <= 2; i++) { after[i] = (byte)(i + 10); }
		for(int i = 10; i < 80; i++) { after[i] = (byte)i; }
		string first = Add(fixture, before, new(1, 1), address: 0x1000);
		string second = Add(fixture, after, new(1, 2), address: 0x1000);

		CompareMemorySnapshotsResult result = AssertSuccess(
			fixture.Service.CompareMemorySnapshots(first, second, 0, 10, 64));

		Assert.Equal(72, result.ChangedBytes);
		Assert.Equal(2, result.ChangedRuns);
		Assert.Equal([0x1001U, 0x100AU], result.Runs.Select(run => run.Address));
		Assert.Equal(2, result.Runs[0].Length);
		Assert.Equal([0, 0], result.Runs[0].Before);
		Assert.Equal([11, 12], result.Runs[0].After);
		Assert.False(result.Runs[0].SampleTruncated);
		Assert.Equal(70, result.Runs[1].Length);
		Assert.Equal(64, result.Runs[1].Before.Length);
		Assert.Equal(64, result.Runs[1].After.Length);
		Assert.True(result.Runs[1].SampleTruncated);
	}

	[Fact]
	public void CompareMemorySnapshots_SupportsZeroSamplesAndPagesWithoutMaterializingAllRuns()
	{
		const int runCount = 1002;
		byte[] before = new byte[(runCount * 2) - 1];
		byte[] after = new byte[before.Length];
		for(int i = 0; i < after.Length; i += 2) { after[i] = 1; }
		using SnapshotFixture fixture = new(after.Length, [0]);
		string first = Add(fixture, before, new(1, 1), address: 0x2000);
		string second = Add(fixture, after, new(1, 2), address: 0x2000);

		CompareMemorySnapshotsResult firstPage = AssertSuccess(
			fixture.Service.CompareMemorySnapshots(first, second, 0, 1000, 0));
		Assert.Equal(runCount, firstPage.ChangedBytes);
		Assert.Equal(runCount, firstPage.ChangedRuns);
		Assert.Equal(1000, firstPage.Runs.Count);
		Assert.Equal(1000, firstPage.NextOffset);
		Assert.Empty(firstPage.Runs[0].Before);
		Assert.Empty(firstPage.Runs[0].After);
		Assert.True(firstPage.Runs[0].SampleTruncated);

		CompareMemorySnapshotsResult lastPage = AssertSuccess(
			fixture.Service.CompareMemorySnapshots(first, second, 1000, 1000, 0));
		Assert.Equal(1000, lastPage.Offset);
		Assert.Equal(2, lastPage.Runs.Count);
		Assert.Equal(0x2000U + 2000, lastPage.Runs[0].Address);
		Assert.Null(lastPage.NextOffset);
	}

	[Theory]
	[InlineData(-1, 1, 0, "invalid_range")]
	[InlineData(0, 0, 0, "invalid_range")]
	[InlineData(0, 1001, 0, "payload_too_large")]
	[InlineData(0, 1, -1, "invalid_range")]
	[InlineData(0, 1, 65, "payload_too_large")]
	public void CompareMemorySnapshots_ValidatesPagingAndSampleBounds(int offset, int limit, int sampleBytes, string error)
	{
		using SnapshotFixture fixture = new(1, [0]);
		string first = Add(fixture, [0], new(1, 1));
		string second = Add(fixture, [1], new(1, 2));

		AssertError(fixture.Service.CompareMemorySnapshots(first, second, offset, limit, sampleBytes), error);
	}

	[Fact]
	public void CompareAndDeleteMemorySnapshots_PreservePinningAndReportStaleDeletedResources()
	{
		using SnapshotFixture fixture = new(2, [0]);
		string first = Add(fixture, [0, 0], new(1, 1));
		string second = Add(fixture, [1, 1], new(1, 2));
		using McpPinnedResource<McpMemorySnapshotResource> externalPin = AssertSuccess(fixture.Store.Pin(first));

		Assert.Equal(2, AssertSuccess(fixture.Service.CompareMemorySnapshots(first, second, 0, 1, 1)).ChangedBytes);
		Assert.True(AssertSuccess(fixture.Service.DeleteMemorySnapshot(first)).Deleted);
		Assert.Equal([0, 0], externalPin.Value.Data);
		AssertError(fixture.Service.CompareMemorySnapshots(first, second, 0, 1, 1), "resource_not_found");

		fixture.Store.InvalidateRom(2);
		AssertError(fixture.Service.CompareMemorySnapshots(second, second, 0, 1, 1), "stale_resource");
		Assert.True(AssertSuccess(fixture.Service.DeleteMemorySnapshot(second)).Deleted);
		AssertError(fixture.Service.DeleteMemorySnapshot("missing"), "resource_not_found");
	}

	private static string Add(
		SnapshotFixture fixture,
		byte[] data,
		McpStateIdentity identity,
		string system = "Nes",
		string space = nameof(MemoryType.NesWorkRam),
		uint address = 0) => AssertSuccess(
		fixture.Store.Create(system, space, address, data, identity, DateTimeOffset.UnixEpoch)).Id;

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

	private sealed class SnapshotFixture : IDisposable
	{
		internal SnapshotFixture(int memorySize, byte[] readData, Func<bool>? isStopped = null)
		{
			Api = FakeMcpEmulatorApi.RunningNes();
			Api.MemorySizes[MemoryType.NesWorkRam] = memorySize;
			Api.MemorySizes[MemoryType.NesInternalRam] = memorySize;
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
		internal McpMemorySnapshotStore Store { get; }
		internal McpMemorySnapshotService Service { get; }

		public void Dispose()
		{
			Store.Dispose();
			Emulator.Dispose();
		}
	}
}
