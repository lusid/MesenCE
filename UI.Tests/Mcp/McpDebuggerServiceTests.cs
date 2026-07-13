using Mesen.Debugger;
using Mesen.Debugger.Utilities;
using Mesen.Interop;
using Mesen.Mcp;
using static Mesen.Debugger.BreakpointManager;

namespace UI.Tests.Mcp;

public sealed class McpDebuggerServiceTests
{
	[Fact]
	public void SetBreakpoint_PreservesExistingCollectionEntriesAndReturnsStableId()
	{
		string? evaluatedExpression = null;
		CpuType evaluatedCpu = default;
		bool? evaluatedUseCache = null;
		FakeMcpEmulatorApi api = CreateApi();
		api.EvaluateExpressionHandler = (string expression, CpuType cpu, out EvalResultType resultType, bool useCache) => {
			evaluatedExpression = expression;
			evaluatedCpu = cpu;
			evaluatedUseCache = useCache;
			resultType = EvalResultType.Boolean;
			return 1;
		};
		FakeBreakpointCollection collection = new();
		using McpEmulatorService service = CreateService(collection, api);

		McpServiceResult<McpBreakpoint> first = SetBreakpoint(service, 0x8000);
		McpServiceResult<McpBreakpoint> second = SetBreakpoint(service, 0x9000, condition: "  pc == $9000  ");

		Assert.Equal(1, first.Value!.Id);
		Assert.Equal(2, second.Value!.Id);
		Assert.Collection(
			collection.Snapshots[^1],
			entry => AssertBreakpoint(entry, 1, 0x8000, ""),
			entry => AssertBreakpoint(entry, 2, 0x9000, "pc == $9000")
		);
		Assert.Equal([first.Value, second.Value], service.ListBreakpoints().Value);
		Assert.Equal("  pc == $9000  ", evaluatedExpression);
		Assert.Equal(CpuType.Nes, evaluatedCpu);
		Assert.False(evaluatedUseCache);
	}

	[Fact]
	public void SetBreakpoint_WhenAtLimit_ReturnsPayloadTooLargeWithoutMutation()
	{
		FakeBreakpointCollection collection = new();
		using McpEmulatorService service = CreateService(collection);
		for(uint i = 0; i < McpDebuggerLimits.MaxMcpBreakpoints; i++) {
			Assert.True(SetBreakpoint(service, i).IsSuccess);
		}
		int snapshotCount = collection.Snapshots.Count;

		McpServiceResult<McpBreakpoint> result = SetBreakpoint(service, 0x8000);

		Assert.Equal("payload_too_large", result.Error!.Code);
		Assert.Equal(snapshotCount, collection.Snapshots.Count);
		Assert.Equal(McpDebuggerLimits.MaxMcpBreakpoints, service.ListBreakpoints().Value!.Count);
	}

	[Fact]
	public void SetBreakpoint_InvalidConditionUtf8LengthDoesNotMutateCollection()
	{
		FakeMcpEmulatorApi api = CreateApi();
		FakeBreakpointCollection collection = new();
		using McpEmulatorService service = CreateService(collection, api);
		string condition = new('\u00E9', 500);

		McpServiceResult<McpBreakpoint> result = SetBreakpoint(service, 0x8000, condition: condition);

		Assert.Equal("payload_too_large", result.Error!.Code);
		Assert.Empty(collection.Snapshots);
		Assert.Equal(0, api.EvaluateExpressionCalls);

		api.EvaluateExpressionHandler = (string expression, CpuType cpu, out EvalResultType resultType, bool useCache) => {
			resultType = EvalResultType.Invalid;
			return 0;
		};
		McpServiceResult<McpBreakpoint> invalidExpression = SetBreakpoint(service, 0x8000, condition: "unknownLabel");

		Assert.Equal("invalid_expression", invalidExpression.Error!.Code);
		Assert.Empty(collection.Snapshots);
		Assert.Equal(1, api.EvaluateExpressionCalls);
	}

	[Fact]
	public void SetBreakpoint_InvalidRangeOrAccessDoesNotMutateCollection()
	{
		FakeBreakpointCollection collection = new();
		using McpEmulatorService service = CreateService(collection);

		McpServiceResult<McpBreakpoint> invalidAccess = service.SetBreakpoint(
			nameof(CpuType.Nes), nameof(MemoryType.NesMemory), "Execute", 0, null, null
		);
		McpServiceResult<McpBreakpoint> invalidRange = service.SetBreakpoint(
			nameof(CpuType.Nes), nameof(MemoryType.NesMemory), "execute", 0x100, 0xFF, null
		);

		Assert.Equal("invalid_access", invalidAccess.Error!.Code);
		Assert.Equal("invalid_range", invalidRange.Error!.Code);
		Assert.Empty(collection.Snapshots);
	}

	[Fact]
	public void RemoveBreakpoint_UnknownStableIdReturnsBreakpointNotOwned()
	{
		FakeBreakpointCollection collection = new();
		using McpEmulatorService service = CreateService(collection);

		McpServiceResult<BreakpointRemoval> result = service.RemoveBreakpoint(42);

		Assert.Equal("breakpoint_not_owned", result.Error!.Code);
		Assert.Empty(collection.Snapshots);
	}

	[Fact]
	public void RemoveAllBreakpoints_RemovesOnlyMcpEntries()
	{
		FakeBreakpointCollection collection = new() { UnownedEntryCount = 2 };
		using McpEmulatorService service = CreateService(collection);
		SetBreakpoint(service, 0x8000);
		SetBreakpoint(service, 0x9000);

		McpServiceResult<BreakpointRemovalSummary> result = service.RemoveAllBreakpoints();

		Assert.Equal(2, result.Value!.RemovedCount);
		Assert.Empty(collection.Snapshots[^1]);
		Assert.Equal(2, collection.UnownedEntryCount);
		Assert.Empty(service.ListBreakpoints().Value!);
	}

	[Fact]
	public void NativeHitMapping_RemainsStableAfterRemoveAndInsert()
	{
		FakeBreakpointCollection collection = new();
		using McpEmulatorService service = CreateService(collection);
		long firstId = SetBreakpoint(service, 0x8000).Value!.Id;
		long secondId = SetBreakpoint(service, 0x9000).Value!.Id;
		int previousSecondNativeId = collection.GetNativeId(secondId);

		Assert.True(service.RemoveBreakpoint(firstId).IsSuccess);
		int reorderedSecondNativeId = collection.GetNativeId(secondId);
		long thirdId = SetBreakpoint(service, 0xA000).Value!.Id;

		Assert.Equal(1, firstId);
		Assert.Equal(2, secondId);
		Assert.Equal(3, thirdId);
		Assert.NotEqual(previousSecondNativeId, reorderedSecondNativeId);
		Assert.True(collection.TryGetStableId(collection.GetNativeId(secondId), out long mappedSecondId));
		Assert.True(collection.TryGetStableId(collection.GetNativeId(thirdId), out long mappedThirdId));
		Assert.Equal(secondId, mappedSecondId);
		Assert.Equal(thirdId, mappedThirdId);
	}

	private static McpEmulatorService CreateService(FakeBreakpointCollection collection, FakeMcpEmulatorApi? api = null)
	{
		return new McpEmulatorService(
			api ?? CreateApi(),
			debuggerLifetime: new DebuggerLifetimeCoordinator(() => { }, () => { }),
			breakpointCollection: collection
		);
	}

	private static FakeMcpEmulatorApi CreateApi()
	{
		FakeMcpEmulatorApi api = FakeMcpEmulatorApi.RunningNes();
		api.MemorySizes[MemoryType.NesMemory] = 0x10000;
		return api;
	}

	private static McpServiceResult<McpBreakpoint> SetBreakpoint(
		McpEmulatorService service,
		uint address,
		uint? endAddress = null,
		string? condition = null
	) => service.SetBreakpoint(
		nameof(CpuType.Nes),
		nameof(MemoryType.NesMemory),
		"execute",
		address,
		endAddress,
		condition
	);

	private static void AssertBreakpoint(ExternalBreakpoint entry, long id, uint address, string condition)
	{
		Assert.Equal(id, entry.StableId);
		Assert.Equal(CpuType.Nes, entry.Breakpoint.CpuType);
		Assert.Equal(MemoryType.NesMemory, entry.Breakpoint.MemoryType);
		Assert.Equal(address, entry.Breakpoint.StartAddress);
		Assert.Equal(address, entry.Breakpoint.EndAddress);
		Assert.True(entry.Breakpoint.BreakOnExec);
		Assert.Equal(condition, entry.Breakpoint.Condition);
	}

	private sealed class FakeBreakpointCollection : IMcpBreakpointCollection
	{
		private readonly Dictionary<int, long> _stableIdsByNativeId = [];
		private int _nextNativeId;

		internal List<IReadOnlyList<ExternalBreakpoint>> Snapshots { get; } = [];
		internal int UnownedEntryCount { get; init; }

		public void Replace(IReadOnlyList<ExternalBreakpoint> breakpoints)
		{
			Snapshots.Add(breakpoints.ToArray());
			_stableIdsByNativeId.Clear();
			foreach(ExternalBreakpoint breakpoint in breakpoints) {
				_stableIdsByNativeId.Add(++_nextNativeId, breakpoint.StableId);
			}
		}

		public bool TryGetStableId(int nativeBreakpointId, out long stableId) =>
			_stableIdsByNativeId.TryGetValue(nativeBreakpointId, out stableId);

		internal int GetNativeId(long stableId) =>
			_stableIdsByNativeId.Single(entry => entry.Value == stableId).Key;

		public void Dispose() { }
	}
}
