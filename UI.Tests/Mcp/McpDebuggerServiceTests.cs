using Mesen.Debugger;
using Mesen.Debugger.Utilities;
using Mesen.Interop;
using Mesen.Mcp;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
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
		Assert.NotEmpty(result.Value.Context.Registers.Registers);
		Assert.Equal(0x8000U, result.Value.Context.ProgramCounter);
		Assert.NotNull(result.Value.Context.PhysicalProgramCounter);
		Assert.NotEmpty(result.Value.Context.Disassembly);
		Assert.NotEmpty(result.Value.Context.CallStack);
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

	[Fact]
	public void GetBreakContext_WhenNoCurrentStopReturnsStaleContext()
	{
		using McpEmulatorService service = CreateService(new FakeBreakpointCollection());

		McpServiceResult<BreakContext> result = service.GetBreakContext(0, 0, 1);

		Assert.Equal("stale_context", result.Error!.Code);
	}

	[Fact]
	public void GetBreakContext_AfterResumeOrGenerationChangeReturnsStaleContext()
	{
		using McpEmulatorService service = CreateService(new FakeBreakpointCollection());
		BreakEvent breakEvent = new() { Source = BreakSource.Pause, SourceCpu = CpuType.Nes, BreakpointId = -1 };
		SendBreak(service, breakEvent);

		Assert.True(service.Resume().IsSuccess);
		Assert.Equal("stale_context", service.GetBreakContext(0, 0, 1).Error!.Code);

		SendBreak(service, breakEvent);
		service.NotifyEmulatorStateChanged();
		Assert.Equal("stale_context", service.GetBreakContext(0, 0, 1).Error!.Code);
	}

	[Fact]
	public void GetBreakContext_ReturnsStableBreakpointOperationRegistersPhysicalPcAndBounds()
	{
		FakeMcpEmulatorApi api = CreateApi();
		api.GetNesCpuStateHandler = () => new NesCpuState { A = 0x42, PC = 0x8123 };
		api.GetProgramCounterHandler = (_, _) => 0x8123;
		api.GetAbsoluteAddressHandler = address => new AddressInfo {
			Address = address.Address == 0x8123 ? 0x4010 : address.Address,
			Type = MemoryType.NesPrgRom
		};
		api.GetDisassemblyOutputHandler = (_, _, _) => CreateDisassemblyRows(300, 0x8123);
		api.GetCallstackHandler = _ => CreateStackFrames(129);
		FakeBreakpointCollection collection = new();
		using McpEmulatorService service = CreateService(collection, api);
		long stableId = SetBreakpoint(service, 0x8123).Value!.Id;
		SendBreak(service, CreateBreakEvent(collection.GetNativeId(stableId)));

		McpServiceResult<BreakContext> result = service.GetBreakContext(127, 128, 128);

		BreakContext context = Assert.IsType<BreakContext>(result.Value);
		Assert.Equal("breakpoint", context.Reason);
		Assert.Equal(stableId, context.BreakpointId);
		Assert.Equal(nameof(CpuType.Nes), context.Cpu);
		Assert.Equal(new McpMemoryOperation(nameof(MemoryType.NesMemory), 0x8123, "execopcode", 1, 0x42), context.Operation);
		Assert.Contains(context.Registers.Registers, register => register.Name == "A" && register.Value == 0x42);
		Assert.Equal(0x8123U, context.ProgramCounter);
		Assert.Equal(new McpAddress(nameof(MemoryType.NesPrgRom), 0x4010), context.PhysicalProgramCounter);
		Assert.Equal(McpDebuggerLimits.MaxDisassemblyRows, context.Disassembly.Count);
		Assert.Equal(McpDebuggerLimits.MaxCallStackDepth, context.CallStack.Count);
		Assert.Equal(0, context.Generation);
	}

	[Fact]
	public void Disassemble_UsesRowAddressForBeforeWindowAndCapsTotalRows()
	{
		FakeMcpEmulatorApi api = CreateApi();
		uint requestedStart = 0;
		uint requestedCount = 0;
		api.GetDisassemblyRowAddressHandler = (_, address, offset) => {
			Assert.Equal(0x8000U, address);
			Assert.Equal(-127, offset);
			return 0x7000;
		};
		api.GetDisassemblyOutputHandler = (_, start, count) => {
			requestedStart = start;
			requestedCount = count;
			return CreateDisassemblyRows(300, start);
		};
		using McpEmulatorService service = CreateService(new FakeBreakpointCollection(), api);

		McpServiceResult<IReadOnlyList<DisassemblyRow>> result = service.Disassemble(nameof(CpuType.Nes), null, 0x8000, 127, 128);

		Assert.Equal(0x7000U, requestedStart);
		Assert.Equal(256U, requestedCount);
		Assert.Equal(McpDebuggerLimits.MaxDisassemblyRows, result.Value!.Count);
		Assert.Equal("invalid_range", service.Disassemble(nameof(CpuType.Nes), null, 0x8000, -1, 0).Error!.Code);
		Assert.Equal("invalid_range", service.Disassemble(nameof(CpuType.Nes), null, 0x8000, 128, 128).Error!.Code);
	}

	[Fact]
	public void Disassemble_ReturnsStructuredEffectiveAddressAndValue()
	{
		FakeMcpEmulatorApi api = CreateApi();
		api.GetDisassemblyOutputHandler = (_, _, _) => [new(CpuType.Nes) {
			Address = 0x8000,
			AbsoluteAddress = new AddressInfo { Address = 0x10, Type = MemoryType.NesPrgRom },
			OpSize = 2,
			ByteCode = [0xA9, 0x0F, 0xFF],
			Text = "LDA #$0F",
			Comment = "load accumulator",
			ShowEffectiveAddress = true,
			EffectiveAddress = 0x20,
			EffectiveAddressType = MemoryType.NesWorkRam,
			Value = 0x0F,
			ValueSize = 1
		}];
		using McpEmulatorService service = CreateService(new FakeBreakpointCollection(), api);

		DisassemblyRow row = Assert.Single(service.Disassemble(nameof(CpuType.Nes), null, 0x8000, 0, 0).Value!);

		Assert.Equal(0x8000U, row.CpuAddress);
		Assert.Equal(new McpAddress(nameof(MemoryType.NesPrgRom), 0x10), row.PhysicalAddress);
		Assert.Equal("A90F", row.Bytes);
		Assert.Equal("LDA #$0F", row.Text);
		Assert.Equal("load accumulator", row.Comment);
		Assert.Equal(new McpAddress(nameof(MemoryType.NesWorkRam), 0x20), row.EffectiveAddress);
		Assert.Equal(0x0FU, row.EffectiveValue);
		Assert.Equal(1, row.EffectiveValueWidth);
	}

	[Fact]
	public void MapAddress_RequeriesNativeMapperEveryCallAndIncludesGeneration()
	{
		FakeMcpEmulatorApi api = CreateApi();
		int mappingCall = 0;
		api.GetAbsoluteAddressHandler = _ => new AddressInfo {
			Address = mappingCall++ == 0 ? 0x10 : 0x4010,
			Type = MemoryType.NesPrgRom
		};
		using McpEmulatorService service = CreateService(new FakeBreakpointCollection(), api);

		AddressMapping first = service.MapAddress(nameof(CpuType.Nes), nameof(MemoryType.NesMemory), 0x8000).Value!;
		AddressMapping second = service.MapAddress(nameof(CpuType.Nes), nameof(MemoryType.NesMemory), 0x8000).Value!;

		Assert.Equal(0x10, first.Physical.Address);
		Assert.Equal(0x4010, second.Physical.Address);
		Assert.Equal(first.Generation, second.Generation);
		Assert.Equal(0, first.Generation);
		Assert.Equal(2, api.GetAbsoluteAddressCalls);
	}

	[Fact]
	public void MapAddress_UnmappedResultReturnsInvalidAddress()
	{
		FakeMcpEmulatorApi api = CreateApi();
		api.GetAbsoluteAddressHandler = _ => new AddressInfo { Address = -1, Type = MemoryType.None };
		using McpEmulatorService service = CreateService(new FakeBreakpointCollection(), api);

		McpServiceResult<AddressMapping> result = service.MapAddress(nameof(CpuType.Nes), nameof(MemoryType.NesMemory), 0x8000);

		Assert.Equal("invalid_address", result.Error!.Code);
	}

	[Fact]
	public void GetCallStack_RunningResultIsLiveAndTruncatesAtLimit()
	{
		FakeMcpEmulatorApi api = CreateApi();
		api.IsExecutionStoppedHandler = () => false;
		api.GetCallstackHandler = _ => CreateStackFrames(129);
		using McpEmulatorService service = CreateService(new FakeBreakpointCollection(), api);

		CallStackResult result = service.GetCallStack(nameof(CpuType.Nes), 128).Value!;

		Assert.True(result.Live);
		Assert.True(result.Truncated);
		Assert.Equal(McpDebuggerLimits.MaxCallStackDepth, result.Frames.Count);
		Assert.Equal(0, result.Generation);
		Assert.Equal("invalid_range", service.GetCallStack(nameof(CpuType.Nes), 0).Error!.Code);
		Assert.Equal("invalid_range", service.GetCallStack(nameof(CpuType.Nes), 129).Error!.Code);
	}

	[Fact]
	public void GetCallStack_StoppedResultIsNotLive()
	{
		using McpEmulatorService service = CreateService(new FakeBreakpointCollection());

		CallStackResult result = service.GetCallStack(nameof(CpuType.Nes), 1).Value!;

		Assert.False(result.Live);
		Assert.Single(result.Frames);
	}

	[Fact]
	public void ConfigureExecutionTrace_EnableValidatesUtf8BeforeNativeMutation()
	{
		FakeMcpEmulatorApi api = CreateApi();
		InteropTraceLoggerOptions captured = default;
		api.SetTraceOptionsHandler = (_, options) => captured = options;
		using McpEmulatorService service = CreateService(new FakeBreakpointCollection(), api);
		string tooLarge = new('\u00E9', 500);
		string maximum = new('x', McpDebuggerLimits.MaxConditionUtf8Bytes);

		McpServiceResult<TraceConfiguration> invalidCondition = service.ConfigureExecutionTrace(
			nameof(CpuType.Nes), "enable", true, true, tooLarge, "format"
		);
		McpServiceResult<TraceConfiguration> invalidFormat = service.ConfigureExecutionTrace(
			nameof(CpuType.Nes), "enable", true, true, "condition", tooLarge
		);

		Assert.Equal("payload_too_large", invalidCondition.Error!.Code);
		Assert.Equal("payload_too_large", invalidFormat.Error!.Code);
		Assert.Equal(0, api.SetTraceOptionsCalls);

		TraceConfiguration result = service.ConfigureExecutionTrace(
			nameof(CpuType.Nes), "enable", true, false, maximum, maximum
		).Value!;

		Assert.True(result.Enabled);
		Assert.True(result.IndentCode);
		Assert.False(result.UseLabels);
		Assert.Equal(maximum, result.Condition);
		Assert.Equal(maximum, result.Format);
		byte[] conditionBuffer = Assert.IsType<byte[]>(captured.Condition);
		byte[] formatBuffer = Assert.IsType<byte[]>(captured.Format);
		Assert.Equal(1000, conditionBuffer.Length);
		Assert.Equal(1000, formatBuffer.Length);
		Assert.Equal(Encoding.UTF8.GetBytes(maximum), conditionBuffer[..^1]);
		Assert.Equal(Encoding.UTF8.GetBytes(maximum), formatBuffer[..^1]);
		Assert.Equal(0, conditionBuffer[^1]);
		Assert.Equal(0, formatBuffer[^1]);
	}

	[Fact]
	public void ConfigureExecutionTrace_ClearClearsRowsWithoutDisabling()
	{
		FakeMcpEmulatorApi api = CreateApi();
		using McpEmulatorService service = CreateService(new FakeBreakpointCollection(), api);
		TraceConfiguration configured = service.ConfigureExecutionTrace(
			nameof(CpuType.Nes), "enable", true, false, "pc >= $8000", "{Address}"
		).Value!;

		McpServiceResult<TraceConfiguration> result = service.ConfigureExecutionTrace(
			nameof(CpuType.Nes), "clear", false, false, null, null
		);

		Assert.Equal(configured, result.Value);
		Assert.Equal(1, api.SetTraceOptionsCalls);
		Assert.Equal(1, api.ClearExecutionTraceCalls);
	}

	[Fact]
	public void ConfigureExecutionTrace_DisableSendsDisabledOptionsAndClearsOwnership()
	{
		FakeMcpEmulatorApi api = CreateApi();
		List<InteropTraceLoggerOptions> options = [];
		api.SetTraceOptionsHandler = (_, value) => options.Add(value);
		using McpEmulatorService service = CreateService(new FakeBreakpointCollection(), api);
		service.ConfigureExecutionTrace(nameof(CpuType.Nes), "enable", true, true, "condition", "format");

		TraceConfiguration disabled = service.ConfigureExecutionTrace(
			nameof(CpuType.Nes), "disable", false, false, null, null
		).Value!;
		McpServiceResult<TraceConfiguration> secondDisable = service.ConfigureExecutionTrace(
			nameof(CpuType.Nes), "disable", false, false, null, null
		);

		Assert.False(disabled.Enabled);
		Assert.False(options[^1].Enabled);
		Assert.Equal(new byte[1000], options[^1].Condition);
		Assert.Equal(new byte[1000], options[^1].Format);
		Assert.Equal(1, api.ClearExecutionTraceCalls);
		Assert.Equal("trace_not_owned", secondDisable.Error!.Code);
	}

	[Fact]
	public void GetExecutionTrace_ReturnsBoundedTailInChronologicalOrder()
	{
		FakeMcpEmulatorApi api = CreateApi();
		TraceRow[] nativeRows = [
			CreateTraceRow(CpuType.Nes, 0x8002, [0xA9, 0x02], "second"),
			CreateTraceRow(CpuType.Nes, 0x8003, [0x60], "third")
		];
		api.GetExecutionTraceSizeHandler = () => 3;
		api.GetExecutionTraceHandler = (start, count) => {
			Assert.Equal(1U, start);
			Assert.Equal(2U, count);
			return nativeRows;
		};
		using McpEmulatorService service = CreateService(new FakeBreakpointCollection(), api);
		service.ConfigureExecutionTrace(nameof(CpuType.Nes), "enable", false, false, null, null);

		ExecutionTraceResult result = service.GetExecutionTrace(2).Value!;

		Assert.Equal(3, result.AvailableRows);
		Assert.True(result.Truncated);
		Assert.Collection(
			result.Rows,
			row => Assert.Equal(new ExecutionTraceRow(nameof(CpuType.Nes), 0x8002, "A902", "second"), row),
			row => Assert.Equal(new ExecutionTraceRow(nameof(CpuType.Nes), 0x8003, "60", "third"), row)
		);
	}

	[Fact]
	public void GetExecutionTrace_AboveLimitReturnsPayloadTooLargeWithoutNativeRead()
	{
		FakeMcpEmulatorApi api = CreateApi();
		using McpEmulatorService service = CreateService(new FakeBreakpointCollection(), api);

		McpServiceResult<ExecutionTraceResult> result = service.GetExecutionTrace(McpDebuggerLimits.MaxTraceRows + 1);

		Assert.Equal("payload_too_large", result.Error!.Code);
		Assert.Equal(0, api.GetExecutionTraceSizeCalls);
		Assert.Equal(0, api.GetExecutionTraceCalls);
	}

	[Fact]
	public void TraceConfiguration_GenerationChangeInvalidatesCursorAndClearsOwnedTrace()
	{
		FakeMcpEmulatorApi api = CreateApi();
		List<InteropTraceLoggerOptions> options = [];
		api.SetTraceOptionsHandler = (_, value) => options.Add(value);
		using McpEmulatorService service = CreateService(new FakeBreakpointCollection(), api);
		service.ConfigureExecutionTrace(nameof(CpuType.Nes), "enable", false, false, null, null);

		service.NotifyEmulatorStateChanged();

		Assert.Single(options);
		Assert.Equal(0, api.ClearExecutionTraceCalls);
		Assert.Null(GetPrivateField(service, "_traceConfiguration"));

		Assert.True(service.GetStatus().IsSuccess);
		Assert.Equal(2, options.Count);
		Assert.False(options[^1].Enabled);
		Assert.Equal(1, api.ClearExecutionTraceCalls);
	}

	[Fact]
	public void Shutdown_DisablesAndClearsOwnedTrace()
	{
		FakeMcpEmulatorApi api = CreateApi();
		List<InteropTraceLoggerOptions> options = [];
		api.SetTraceOptionsHandler = (_, value) => options.Add(value);
		FakeBreakpointCollection collection = new();
		using McpEmulatorService service = CreateService(collection, api);
		service.ConfigureExecutionTrace(nameof(CpuType.Nes), "enable", false, false, null, null);
		SetBreakpoint(service, 0x8000);

		service.BeginShutdown();
		service.BeginShutdown();

		Assert.Equal(2, options.Count);
		Assert.False(options[^1].Enabled);
		Assert.Equal(1, api.ClearExecutionTraceCalls);
		Assert.Empty(collection.Snapshots[^1]);
		Assert.Equal(1, collection.DisposeCalls);
		Assert.Equal("server_stopping", service.GetStatus().Error!.Code);
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

	private static CodeLineData[] CreateDisassemblyRows(int count, uint start)
	{
		return Enumerable.Range(0, count).Select(index => new CodeLineData(CpuType.Nes) {
			Address = (int)start + index,
			AbsoluteAddress = new AddressInfo { Address = index, Type = MemoryType.NesPrgRom },
			OpSize = 1,
			ByteCode = [(byte)index],
			Text = "NOP"
		}).ToArray();
	}

	private static StackFrameInfo[] CreateStackFrames(int count)
	{
		return Enumerable.Range(0, count).Select(index => new StackFrameInfo {
			Source = (uint)(0x8000 + index),
			AbsSource = new AddressInfo { Address = index, Type = MemoryType.NesPrgRom },
			Target = (uint)(0x9000 + index),
			AbsTarget = new AddressInfo { Address = 0x100 + index, Type = MemoryType.NesPrgRom },
			Return = (uint)(0xA000 + index),
			AbsReturn = new AddressInfo { Address = 0x200 + index, Type = MemoryType.NesPrgRom },
			Flags = index == 0 ? StackFrameFlags.Nmi : StackFrameFlags.None
		}).ToArray();
	}

	private static TraceRow CreateTraceRow(CpuType cpu, uint programCounter, byte[] byteCode, string output)
	{
		byte[] encodedOutput = Encoding.UTF8.GetBytes(output);
		byte[] native = new byte[Marshal.SizeOf<TraceRow>()];
		BitConverter.GetBytes(programCounter).CopyTo(native, Marshal.OffsetOf<TraceRow>(nameof(TraceRow.ProgramCounter)).ToInt32());
		BitConverter.GetBytes((int)cpu).CopyTo(native, Marshal.OffsetOf<TraceRow>(nameof(TraceRow.Type)).ToInt32());
		byteCode.CopyTo(native, Marshal.OffsetOf<TraceRow>(nameof(TraceRow.ByteCode)).ToInt32());
		native[Marshal.OffsetOf<TraceRow>(nameof(TraceRow.ByteCodeSize)).ToInt32()] = (byte)byteCode.Length;
		BitConverter.GetBytes((uint)encodedOutput.Length).CopyTo(native, Marshal.OffsetOf<TraceRow>(nameof(TraceRow.LogSize)).ToInt32());
		encodedOutput.CopyTo(native, Marshal.OffsetOf<TraceRow>(nameof(TraceRow.LogOutput)).ToInt32());
		IntPtr pointer = Marshal.AllocHGlobal(native.Length);
		try {
			Marshal.Copy(native, 0, pointer, native.Length);
			return Marshal.PtrToStructure<TraceRow>(pointer);
		} finally {
			Marshal.FreeHGlobal(pointer);
		}
	}

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
		internal int DisposeCalls { get; private set; }

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

		public void Dispose() => DisposeCalls++;
	}
}
