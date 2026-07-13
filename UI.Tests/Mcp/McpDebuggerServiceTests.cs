using Mesen.Debugger;
using Mesen.Debugger.Utilities;
using Mesen.Interop;
using Mesen.Mcp;
using System.Reflection;
using System.Runtime.InteropServices;
using static Mesen.Debugger.BreakpointManager;

namespace UI.Tests.Mcp;

public sealed class McpDebuggerServiceTests
{
	[Fact]
	public async Task ProcessNotification_CodeBreakCopiesStructBeforeCallbackReturns()
	{
		FakeBreakpointCollection collection = new();
		using McpEmulatorService service = CreateService(collection);
		long stableId = SetBreakpoint(service, 0x8000).Value!.Id;
		BreakEvent original = CreateBreakEvent(collection.GetNativeId(stableId));
		IntPtr pointer = Marshal.AllocHGlobal(Marshal.SizeOf<BreakEvent>());
		try {
			Task<McpServiceResult<ContinueResult>> wait = service.ContinueUntilBreakAsync(nameof(CpuType.Nes), 5000, CancellationToken.None);
			Marshal.StructureToPtr(original, pointer, false);
			service.ProcessNotification(Notification(pointer));
			Marshal.StructureToPtr(new BreakEvent { Source = BreakSource.Nmi, SourceCpu = CpuType.Spc }, pointer, false);

			McpServiceResult<ContinueResult> result = await wait.WaitAsync(TimeSpan.FromSeconds(5));
			BreakEvent copied = Assert.IsType<BreakEvent>(GetPrivateField(service, "_latestBreakEvent"));
			Assert.Equal(original.Source, copied.Source);
			Assert.Equal(original.SourceCpu, copied.SourceCpu);
			Assert.Equal(original.Operation.Address, copied.Operation.Address);
			Assert.Equal(original.Operation.Value, copied.Operation.Value);
			Assert.Equal(original.Operation.Type, copied.Operation.Type);
			Assert.Equal(original.Operation.MemType, copied.Operation.MemType);
			Assert.Equal(original.BreakpointId, copied.BreakpointId);
			Assert.Equal(stableId, result.Value!.Context!.BreakpointId);
		} finally {
			Marshal.FreeHGlobal(pointer);
		}
	}

	[Fact]
	public async Task ContinueUntilBreak_ReleasesGateWhileWaiting()
	{
		using McpEmulatorService service = CreateService(new FakeBreakpointCollection());
		using CancellationTokenSource cancellation = new();

		Task<McpServiceResult<ContinueResult>> wait = service.ContinueUntilBreakAsync(nameof(CpuType.Nes), 5000, cancellation.Token);
		McpServiceResult<EmulatorStatus> status = await Task.Run(service.GetStatus).WaitAsync(TimeSpan.FromSeconds(2));

		Assert.True(status.IsSuccess);
		cancellation.Cancel();
		Assert.Equal("cancelled", (await wait).Error!.Code);
	}

	[Fact]
	public async Task ContinueUntilBreak_CorrelatesNativeHitToStableMcpId()
	{
		FakeBreakpointCollection collection = new();
		using McpEmulatorService service = CreateService(collection);
		long stableId = SetBreakpoint(service, 0x8000).Value!.Id;
		Task<McpServiceResult<ContinueResult>> wait = service.ContinueUntilBreakAsync(nameof(CpuType.Nes), 5000, CancellationToken.None);

		SendBreak(service, CreateBreakEvent(collection.GetNativeId(stableId)));

		McpServiceResult<ContinueResult> result = await wait.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Equal("breakpoint", result.Value!.Reason);
		Assert.Equal(stableId, result.Value.Context!.BreakpointId);
	}

	[Fact]
	public async Task ContinueUntilBreak_RejectsSecondWaiterForSameCpu()
	{
		using McpEmulatorService service = CreateService(new FakeBreakpointCollection());
		using CancellationTokenSource cancellation = new();
		Task<McpServiceResult<ContinueResult>> first = service.ContinueUntilBreakAsync(nameof(CpuType.Nes), 5000, cancellation.Token);

		McpServiceResult<ContinueResult> second = await service.ContinueUntilBreakAsync(nameof(CpuType.Nes), 5000, CancellationToken.None);

		Assert.Equal("operation_in_progress", second.Error!.Code);
		cancellation.Cancel();
		await first;
	}

	[Fact]
	public async Task ContinueUntilBreak_TimesOutAndRemovesWaiter()
	{
		using McpEmulatorService service = CreateService(new FakeBreakpointCollection());

		McpServiceResult<ContinueResult> timedOut = await service.ContinueUntilBreakAsync(nameof(CpuType.Nes), 1, CancellationToken.None);
		Task<McpServiceResult<ContinueResult>> next = service.ContinueUntilBreakAsync(nameof(CpuType.Nes), 5000, CancellationToken.None);
		SendBreak(service, new BreakEvent { Source = BreakSource.Pause, SourceCpu = CpuType.Nes, BreakpointId = -1 });

		Assert.Equal("timeout", timedOut.Error!.Code);
		Assert.Equal("pause", (await next).Value!.Reason);
	}

	[Fact]
	public async Task ContinueUntilBreak_CancellationRemovesWaiter()
	{
		using McpEmulatorService service = CreateService(new FakeBreakpointCollection());
		using CancellationTokenSource cancellation = new();
		Task<McpServiceResult<ContinueResult>> cancelled = service.ContinueUntilBreakAsync(nameof(CpuType.Nes), 5000, cancellation.Token);

		cancellation.Cancel();
		Assert.Equal("cancelled", (await cancelled).Error!.Code);

		Task<McpServiceResult<ContinueResult>> next = service.ContinueUntilBreakAsync(nameof(CpuType.Nes), 5000, CancellationToken.None);
		SendBreak(service, new BreakEvent { Source = BreakSource.Pause, SourceCpu = CpuType.Nes, BreakpointId = -1 });
		Assert.True((await next).IsSuccess);
	}

	[Fact]
	public async Task ContinueUntilBreak_StateGenerationChangeReturnsStateChanged()
	{
		using McpEmulatorService service = CreateService(new FakeBreakpointCollection());
		Task<McpServiceResult<ContinueResult>> wait = service.ContinueUntilBreakAsync(nameof(CpuType.Nes), 5000, CancellationToken.None);

		service.NotifyEmulatorStateChanged();

		Assert.Equal("state_changed", (await wait.WaitAsync(TimeSpan.FromSeconds(2))).Error!.Code);
	}

	[Fact]
	public async Task ContinueUntilBreak_ShutdownReturnsServerStopping()
	{
		using McpEmulatorService service = CreateService(new FakeBreakpointCollection());
		Task<McpServiceResult<ContinueResult>> wait = service.ContinueUntilBreakAsync(nameof(CpuType.Nes), 5000, CancellationToken.None);

		service.BeginShutdown();

		Assert.Equal("server_stopping", (await wait.WaitAsync(TimeSpan.FromSeconds(2))).Error!.Code);
	}

	[Fact]
	public void Resume_InvalidatesLatestContext()
	{
		using McpEmulatorService service = CreateService(new FakeBreakpointCollection());
		SendBreak(service, new BreakEvent { Source = BreakSource.Pause, SourceCpu = CpuType.Nes, BreakpointId = -1 });
		Assert.NotNull(GetPrivateField(service, "_latestBreakEvent"));

		Assert.True(service.Resume().IsSuccess);

		Assert.Null(GetPrivateField(service, "_latestBreakEvent"));
		Assert.Equal(-1L, GetPrivateField(service, "_latestBreakGeneration"));
	}

	[Fact]
	public void Step_UnsupportedFeatureReturnsInvalidStepTypeWithoutNativeCall()
	{
		FakeMcpEmulatorApi api = CreateApi();
		using McpEmulatorService service = CreateService(new FakeBreakpointCollection(), api);

		McpServiceResult<ExecutionState> result = service.Step(nameof(CpuType.Nes), "over");

		Assert.Equal("invalid_step_type", result.Error!.Code);
		Assert.Equal(0, api.StepCalls);
	}

	[Fact]
	public void Pause_UsesNormalPauseAndResumeSelectsDebuggerOrNormalPath()
	{
		FakeMcpEmulatorApi api = CreateApi();
		bool executionStopped = true;
		api.IsExecutionStoppedHandler = () => executionStopped;
		using McpEmulatorService service = CreateService(new FakeBreakpointCollection(), api);

		Assert.True(service.Pause().IsSuccess);
		Assert.True(service.Resume().IsSuccess);
		executionStopped = false;
		Assert.True(service.Resume().IsSuccess);

		Assert.Equal(1, api.PauseCalls);
		Assert.Equal(1, api.ResumeDebuggerCalls);
		Assert.Equal(1, api.ResumeCalls);
	}

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

	private static BreakEvent CreateBreakEvent(int nativeBreakpointId) => new() {
		Source = BreakSource.Breakpoint,
		SourceCpu = CpuType.Nes,
		Operation = new MemoryOperationInfo {
			Address = 0x8123,
			Value = 0x42,
			Type = MemoryOperationType.ExecOpCode,
			MemType = MemoryType.NesMemory
		},
		BreakpointId = nativeBreakpointId
	};

	private static NotificationEventArgs Notification(IntPtr pointer) => new() {
		NotificationType = ConsoleNotificationType.CodeBreak,
		Parameter = pointer
	};

	private static void SendBreak(McpEmulatorService service, BreakEvent breakEvent)
	{
		IntPtr pointer = Marshal.AllocHGlobal(Marshal.SizeOf<BreakEvent>());
		try {
			Marshal.StructureToPtr(breakEvent, pointer, false);
			service.ProcessNotification(Notification(pointer));
		} finally {
			Marshal.FreeHGlobal(pointer);
		}
	}

	private static object? GetPrivateField(McpEmulatorService service, string name) =>
		typeof(McpEmulatorService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service);

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
