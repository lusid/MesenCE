using Mesen.Debugger;
using Mesen.Debugger.Utilities;
using Mesen.Interop;
using Mesen.Mcp;
using System.Runtime.InteropServices;

namespace UI.Tests.Mcp;

public sealed class McpExecutionWaiterTests
{
	[Fact]
	public async Task StepAndWait_RegistersBeforeNativeStepAndCompletesOnlyForMatchingPpuStep()
	{
		FakeMcpEmulatorApi api = CreateApi();
		using McpEmulatorService service = CreateService(api);
		api.StepHandler = (cpu, count, type) => {
			Assert.Equal((CpuType.Nes, 3, StepType.PpuFrame), (cpu, count, type));
			SendBreak(service, new BreakEvent { Source = BreakSource.PpuStep, SourceCpu = CpuType.Nes });
		};
		await using McpExecutionLease lease = await AcquireAsync(service);

		McpStopResult result = await service.StepAndWaitAsync(
			lease.LeaseId, CpuType.Nes, 3, service.EmulatorIdentity.Current, TimeSpan.FromSeconds(1), CancellationToken.None);

		Assert.Equal(McpStopReason.StepCompleted, result.Reason);
		Assert.Equal(3, result.CompletedFrames);
		Assert.True(result.StopConfirmed);
		Assert.Null(result.BreakEvent);
	}

	[Fact]
	public async Task StepAndWait_IgnoresOtherCpuPpuStepAndPpuFrameDone()
	{
		FakeMcpEmulatorApi api = CreateApi();
		using McpEmulatorService service = CreateService(api);
		await using McpExecutionLease lease = await AcquireAsync(service);
		Task<McpStopResult> wait = service.StepAndWaitAsync(
			lease.LeaseId, CpuType.Nes, 2, service.EmulatorIdentity.Current, TimeSpan.FromSeconds(1), CancellationToken.None);

		SendBreak(service, new BreakEvent { Source = BreakSource.PpuStep, SourceCpu = CpuType.Spc });
		service.ProcessNotification(Notification(ConsoleNotificationType.PpuFrameDone));
		Assert.False(wait.IsCompleted);

		SendBreak(service, new BreakEvent { Source = BreakSource.PpuStep, SourceCpu = CpuType.Nes });
		Assert.Equal(McpStopReason.StepCompleted, (await wait).Reason);
	}

	[Fact]
	public async Task StepAndWait_CrossCpuNonPpuStepInterruptsAndPreservesCopiedContext()
	{
		FakeMcpEmulatorApi api = CreateApi();
		using McpEmulatorService service = CreateService(api);
		await using McpExecutionLease lease = await AcquireAsync(service);
		Task<McpStopResult> wait = service.StepAndWaitAsync(
			lease.LeaseId, CpuType.Nes, 8, service.EmulatorIdentity.Current, TimeSpan.FromMilliseconds(50), CancellationToken.None);
		BreakEvent original = new() {
			Source = BreakSource.Breakpoint,
			SourceCpu = CpuType.Spc,
			BreakpointId = 91,
			Operation = new MemoryOperationInfo { Address = 0x3456, Value = 0x72, MemType = MemoryType.SpcMemory }
		};

		SendBreak(service, original);

		McpStopResult result = await wait;
		Assert.Equal(McpStopReason.Breakpoint, result.Reason);
		Assert.Null(result.CompletedFrames);
		Assert.Equal(original.SourceCpu, result.BreakEvent?.SourceCpu);
		Assert.Equal(original.BreakpointId, result.BreakEvent?.BreakpointId);
		Assert.Equal(original.Operation.Address, result.BreakEvent?.Operation.Address);
	}

	[Fact]
	public async Task StepAndWait_MismatchedIdentityCodeBreakCompletesStateChangedWithoutStaleContext()
	{
		FakeMcpEmulatorApi api = CreateApi();
		using McpEmulatorService service = CreateService(api);
		await using McpExecutionLease lease = await AcquireAsync(service);
		Task<McpStopResult> wait = service.StepAndWaitAsync(
			lease.LeaseId, CpuType.Nes, 8, service.EmulatorIdentity.Current, TimeSpan.FromMilliseconds(50), CancellationToken.None);
		service.EmulatorIdentity.NotifyMutableStateChanged();

		SendBreak(service, new BreakEvent {
			Source = BreakSource.Breakpoint,
			SourceCpu = CpuType.Spc,
			BreakpointId = 45
		});

		McpStopResult result = await wait;
		Assert.Equal(McpStopReason.StateChanged, result.Reason);
		Assert.Null(result.CompletedFrames);
		Assert.Null(result.BreakEvent);
	}

	[Fact]
	public async Task StepAndWait_PreservesCopiedBreakpointContextWithoutClaimingPartialFrames()
	{
		FakeMcpEmulatorApi api = CreateApi();
		using McpEmulatorService service = CreateService(api);
		await using McpExecutionLease lease = await AcquireAsync(service);
		Task<McpStopResult> wait = service.StepAndWaitAsync(
			lease.LeaseId, CpuType.Nes, 10, service.EmulatorIdentity.Current, TimeSpan.FromSeconds(1), CancellationToken.None);
		BreakEvent original = new() {
			Source = BreakSource.Breakpoint,
			SourceCpu = CpuType.Nes,
			BreakpointId = 27,
			Operation = new MemoryOperationInfo { Address = 0x8123, Value = 0x44, MemType = MemoryType.NesMemory }
		};

		IntPtr pointer = Marshal.AllocHGlobal(Marshal.SizeOf<BreakEvent>());
		try {
			Marshal.StructureToPtr(original, pointer, false);
			service.ProcessNotification(Notification(ConsoleNotificationType.CodeBreak, pointer));
			Marshal.StructureToPtr(new BreakEvent { Source = BreakSource.Nmi, SourceCpu = CpuType.Spc }, pointer, false);
		} finally {
			Marshal.FreeHGlobal(pointer);
		}

		McpStopResult result = await wait;
		Assert.Equal(McpStopReason.Breakpoint, result.Reason);
		Assert.Null(result.CompletedFrames);
		Assert.Equal(original.BreakpointId, result.BreakEvent?.BreakpointId);
		Assert.Equal(original.Operation.Address, result.BreakEvent?.Operation.Address);
	}

	[Theory]
	[InlineData(ConsoleNotificationType.GameReset, (int)McpStopReason.Reset)]
	[InlineData(ConsoleNotificationType.StateLoaded, (int)McpStopReason.StateChanged)]
	[InlineData(ConsoleNotificationType.BeforeGameLoad, (int)McpStopReason.RomTransition)]
	public async Task StepAndWait_LifecycleInterruptsWithoutEnteringNativeGate(
		ConsoleNotificationType notificationType,
		int expectedReason)
	{
		FakeMcpEmulatorApi api = CreateApi();
		using McpEmulatorService service = CreateService(api);
		await using McpExecutionLease lease = await AcquireAsync(service);
		Task<McpStopResult> wait = service.StepAndWaitAsync(
			lease.LeaseId, CpuType.Nes, 1, service.EmulatorIdentity.Current, TimeSpan.FromSeconds(1), CancellationToken.None);
		int nativeCalls = api.DebuggerRequestBlockStateCalls;

		service.ProcessNotification(Notification(notificationType));

		Assert.Equal((McpStopReason)expectedReason, (await wait).Reason);
		Assert.Equal(nativeCalls, api.DebuggerRequestBlockStateCalls);
	}

	[Fact]
	public async Task StepAndWait_TimeoutCancellationAndShutdownRemoveWaiter()
	{
		FakeMcpEmulatorApi api = CreateApi();
		using McpEmulatorService service = CreateService(api);
		await using McpExecutionLease lease = await AcquireAsync(service);

		McpStopResult timedOut = await service.StepAndWaitAsync(
			lease.LeaseId, CpuType.Nes, 1, service.EmulatorIdentity.Current, TimeSpan.FromMilliseconds(10), CancellationToken.None);
		Assert.Equal(McpStopReason.Timeout, timedOut.Reason);

		using CancellationTokenSource cancellation = new();
		Task<McpStopResult> cancelledWait = service.StepAndWaitAsync(
			lease.LeaseId, CpuType.Nes, 1, service.EmulatorIdentity.Current, TimeSpan.FromSeconds(1), cancellation.Token);
		cancellation.Cancel();
		Assert.Equal(McpStopReason.Cancelled, (await cancelledWait).Reason);

		Task<McpStopResult> shutdownWait = service.StepAndWaitAsync(
			lease.LeaseId, CpuType.Nes, 1, service.EmulatorIdentity.Current, TimeSpan.FromSeconds(1), CancellationToken.None);
		service.BeginServiceShutdown();
		Assert.Equal(McpStopReason.ServerStopping, (await shutdownWait).Reason);
	}

	[Fact]
	public async Task StepAndWait_NativeFailureRemovesWaiter()
	{
		FakeMcpEmulatorApi api = CreateApi();
		using McpEmulatorService service = CreateService(api);
		await using McpExecutionLease lease = await AcquireAsync(service);
		api.StepHandler = (_, _, _) => throw new InvalidOperationException("native details");

		McpStopResult failed = await service.StepAndWaitAsync(
			lease.LeaseId, CpuType.Nes, 1, service.EmulatorIdentity.Current, TimeSpan.FromSeconds(1), CancellationToken.None);
		Assert.Equal(McpStopReason.NativeFailure, failed.Reason);

		api.StepHandler = (_, _, _) =>
			SendBreak(service, new BreakEvent { Source = BreakSource.PpuStep, SourceCpu = CpuType.Nes });
		McpStopResult next = await service.StepAndWaitAsync(
			lease.LeaseId, CpuType.Nes, 1, service.EmulatorIdentity.Current, TimeSpan.FromSeconds(1), CancellationToken.None);
		Assert.Equal(McpStopReason.StepCompleted, next.Reason);
	}

	[Fact]
	public async Task EnsureStopped_DoesNotPauseWhenAlreadyStopped()
	{
		FakeMcpEmulatorApi api = CreateApi();
		using McpEmulatorService service = CreateService(api);
		await using McpExecutionLease lease = await AcquireAsync(service);

		McpStopResult result = await service.EnsureStoppedAsync(lease.LeaseId, TimeSpan.FromSeconds(10));

		Assert.True(result.StopConfirmed);
		Assert.Equal(McpStopReason.Pause, result.Reason);
		Assert.Equal(0, api.PauseCalls);
	}

	[Fact]
	public async Task EnsureStopped_RegistersBeforePauseAndWaitsForConfirmation()
	{
		FakeMcpEmulatorApi api = CreateApi();
		api.IsExecutionStoppedHandler = () => false;
		using McpEmulatorService service = CreateService(api);
		api.PauseHandler = () => service.ProcessNotification(Notification(ConsoleNotificationType.GamePaused));
		await using McpExecutionLease lease = await AcquireAsync(service);

		McpStopResult result = await service.EnsureStoppedAsync(lease.LeaseId, TimeSpan.FromSeconds(10));

		Assert.True(result.StopConfirmed);
		Assert.Equal(McpStopReason.Pause, result.Reason);
		Assert.Equal(1, api.PauseCalls);
		Assert.False(service.ExecutionCoordinator.IsQuarantined);
	}

	[Fact]
	public async Task EnsureStopped_FailedConfirmationQuarantinesBeforeLeaseRelease()
	{
		FakeMcpEmulatorApi api = CreateApi();
		api.IsExecutionStoppedHandler = () => false;
		using McpEmulatorService service = CreateService(api);
		McpExecutionLease lease = await AcquireAsync(service);

		McpStopResult result = await service.EnsureStoppedAsync(lease.LeaseId, TimeSpan.FromMilliseconds(10));

		Assert.False(result.StopConfirmed);
		Assert.Equal(McpStopReason.Timeout, result.Reason);
		Assert.True(service.ExecutionCoordinator.IsQuarantined);
		Assert.Equal("execution_state_unknown", (await service.ExecutionCoordinator.TryAcquireAsync(CancellationToken.None)).Error?.Code);
		await lease.DisposeAsync();
		Assert.Equal("execution_state_unknown", (await service.ExecutionCoordinator.TryAcquireAsync(CancellationToken.None)).Error?.Code);
	}

	[Fact]
	public async Task EnsureStopped_NativePauseFailureQuarantinesOwner()
	{
		FakeMcpEmulatorApi api = CreateApi();
		api.IsExecutionStoppedHandler = () => false;
		api.PauseHandler = () => throw new InvalidOperationException("native details");
		using McpEmulatorService service = CreateService(api);
		await using McpExecutionLease lease = await AcquireAsync(service);

		McpStopResult result = await service.EnsureStoppedAsync(lease.LeaseId, TimeSpan.FromSeconds(1));

		Assert.Equal(McpStopReason.NativeFailure, result.Reason);
		Assert.False(result.StopConfirmed);
		Assert.True(service.ExecutionCoordinator.IsQuarantined);
	}

	private static FakeMcpEmulatorApi CreateApi()
	{
		FakeMcpEmulatorApi api = FakeMcpEmulatorApi.RunningNes();
		api.MemorySizes[MemoryType.NesMemory] = 0x10000;
		return api;
	}

	private static McpEmulatorService CreateService(FakeMcpEmulatorApi api) => new(
		api,
		debuggerLifetime: new DebuggerLifetimeCoordinator(() => { }, () => { }),
		breakpointCollection: new NullBreakpointCollection());

	private static async Task<McpExecutionLease> AcquireAsync(McpEmulatorService service) =>
		(await service.ExecutionCoordinator.TryAcquireAsync(CancellationToken.None)).Value!;

	private static NotificationEventArgs Notification(ConsoleNotificationType type, IntPtr parameter = default) =>
		new() { NotificationType = type, Parameter = parameter };

	private static void SendBreak(McpEmulatorService service, BreakEvent breakEvent)
	{
		IntPtr pointer = Marshal.AllocHGlobal(Marshal.SizeOf<BreakEvent>());
		try {
			Marshal.StructureToPtr(breakEvent, pointer, false);
			service.ProcessNotification(Notification(ConsoleNotificationType.CodeBreak, pointer));
		} finally {
			Marshal.FreeHGlobal(pointer);
		}
	}

	private sealed class NullBreakpointCollection : IMcpBreakpointCollection
	{
		public void Replace(IReadOnlyList<BreakpointManager.ExternalBreakpoint> breakpoints) { }

		public bool TryGetStableId(int nativeBreakpointId, out long stableId)
		{
			stableId = 0;
			return false;
		}

		public void Dispose() { }
	}
}
