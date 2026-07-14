using Mesen.Config;
using Mesen.Debugger;
using Mesen.Debugger.Utilities;
using Mesen.Interop;
using Mesen.Mcp;

namespace UI.Tests.Mcp;

public sealed class McpAutomationServiceTests
{
	[Fact]
	public void OperationsWithoutGame_ReturnNoGameWithoutNativeMutation()
	{
		FakeMcpEmulatorApi api = new();
		using AutomationFixture fixture = new(api);

		Assert.Equal("no_game", fixture.Automation.GetCapabilities().Error?.Code);
		Assert.Equal("no_game", fixture.Automation.CreateSaveState().Error?.Code);
		Assert.Equal("no_game", fixture.Automation.CaptureScreenshot().Error?.Code);
		Assert.Equal(0, api.CreateSaveStateCalls);
		Assert.Equal(0, api.CaptureScreenshotCalls);
	}

	[Fact]
	public void Capabilities_ReportAdapterSupportAndEveryResourceLimit()
	{
		FakeMcpEmulatorApi api = FakeMcpEmulatorApi.RunningNes();
		api.ControllerTopology = [new(0, 0, ControllerType.NesZapper, [new("trigger", 0, false)])];
		using AutomationFixture fixture = new(api);

		McpAutomationCapabilities capabilities = AssertSuccess(fixture.Automation.GetCapabilities());

		Assert.True(capabilities.SaveStates);
		Assert.True(capabilities.Screenshots);
		Assert.False(capabilities.DeterministicFrames);
		Assert.False(Assert.Single(capabilities.Controllers).ExclusiveInput);
		Assert.Equal(McpAutomationLimits.MaxSaveStates, capabilities.Limits.MaxSaveStates);
		Assert.Equal(McpAutomationLimits.MaxSaveStateBytes, capabilities.Limits.MaxSaveStateBytes);
		Assert.Equal(McpAutomationLimits.MaxAggregateSaveStateBytes, capabilities.Limits.MaxAggregateSaveStateBytes);
		Assert.Equal(McpAutomationLimits.MaxMemorySnapshots, capabilities.Limits.MaxMemorySnapshots);
		Assert.Equal(McpAutomationLimits.MaxMemorySnapshotBytes, capabilities.Limits.MaxMemorySnapshotBytes);
		Assert.Equal(McpAutomationLimits.MaxAggregateMemorySnapshotBytes, capabilities.Limits.MaxAggregateMemorySnapshotBytes);
		Assert.Equal(McpAutomationLimits.MaxMemorySearches, capabilities.Limits.MaxMemorySearches);
		Assert.Equal(McpAutomationLimits.MaxSearchRangeBytes, capabilities.Limits.MaxSearchRangeBytes);
		Assert.Equal(McpAutomationLimits.MaxSearchAllocationBytes, capabilities.Limits.MaxSearchAllocationBytes);
		Assert.Equal(McpAutomationLimits.MaxAggregateSearchAllocationBytes, capabilities.Limits.MaxAggregateSearchAllocationBytes);
		Assert.Equal(McpAutomationLimits.MaxSegments, capabilities.Limits.MaxSegments);
		Assert.Equal(McpAutomationLimits.MaxExperimentFrames, capabilities.Limits.MaxExperimentFrames);
		Assert.Equal(McpAutomationLimits.MaxObservations, capabilities.Limits.MaxObservations);
		Assert.Equal(McpAutomationLimits.MaxAssertions, capabilities.Limits.MaxAssertions);
		Assert.Equal(McpAutomationLimits.MaxObservedBytes, capabilities.Limits.MaxObservedBytes);
		Assert.Equal(McpAutomationLimits.MaxPngBytes, capabilities.Limits.MaxPngBytes);
		Assert.Equal(McpAutomationLimits.MaxScreenshotDimension, capabilities.Limits.MaxScreenshotDimension);
		Assert.Equal(McpAutomationLimits.MaxScreenshotPixels, capabilities.Limits.MaxScreenshotPixels);
		Assert.Equal(McpAutomationLimits.MaxResultPage, capabilities.Limits.MaxResultPage);
		Assert.Equal(McpAutomationLimits.MaxRunSampleBytes, capabilities.Limits.MaxRunSampleBytes);
		Assert.Equal(McpAutomationLimits.MinExperimentTimeoutMs, capabilities.Limits.MinExperimentTimeoutMs);
		Assert.Equal(McpAutomationLimits.MaxExperimentTimeoutMs, capabilities.Limits.MaxExperimentTimeoutMs);
		Assert.Equal((int)McpAutomationLimits.ResourceIdleExpiration.TotalMinutes, capabilities.Limits.ResourceIdleExpirationMinutes);
	}

	[Fact]
	public void Capabilities_FirstPausedCallInitializesDebuggerBeforeTopologyInspection()
	{
		FakeMcpEmulatorApi api = FakeMcpEmulatorApi.RunningNes(paused: true);
		api.ControllerTopology = [new(0, 0, ControllerType.NesController, [new("A", 0, false)])];
		bool initialized = false;
		api.GetControllerTopologyHandler = () => {
			if(!initialized) {
				api.SetDebuggerRequestBlocked(true);
				api.SetDebuggerRequestBlocked(false);
				initialized = true;
			}
			return api.ControllerTopology;
		};
		using AutomationFixture fixture = new(api, () => {
			api.SetDebuggerRequestBlocked(true);
			api.SetDebuggerRequestBlocked(false);
			initialized = true;
		});

		McpAutomationCapabilities capabilities = AssertSuccess(fixture.Automation.GetCapabilities());

		Assert.True(capabilities.DeterministicFrames);
		Assert.True(initialized);
		Assert.Equal(1, api.GetControllerTopologyCalls);
	}

	[Fact]
	public void CreateSaveState_EnforcesStoreQuota()
	{
		FakeMcpEmulatorApi api = FakeMcpEmulatorApi.RunningNes();
		api.CreateSaveStateHandler = () => McpServiceResult<byte[]>.Success([0x42]);
		using AutomationFixture fixture = new(api);

		for(int i = 0; i < McpAutomationLimits.MaxSaveStates; i++) {
			AssertSuccess(fixture.Automation.CreateSaveState());
		}

		Assert.Equal("resource_limit", fixture.Automation.CreateSaveState().Error?.Code);
	}

	[Fact]
	public async Task LoadSaveState_OwnsExecutionPinsBytesAndAcceptsExpectedGenerationChange()
	{
		FakeMcpEmulatorApi api = FakeMcpEmulatorApi.RunningNes();
		api.CreateSaveStateHandler = () => McpServiceResult<byte[]>.Success([0x12, 0x34]);
		using AutomationFixture fixture = new(api);
		McpSaveStateMetadata state = AssertSuccess(fixture.Automation.CreateSaveState());
		api.LoadSaveStateHandler = data => {
			Assert.Equal([0x12, 0x34], data);
			Assert.True(fixture.SaveStates.Delete(state.Id).IsSuccess);
			fixture.Emulator.ProcessNotification(new NotificationEventArgs { NotificationType = ConsoleNotificationType.StateLoaded });
			return McpServiceResult<bool>.Success(true);
		};

		McpSaveStateLoadResult loaded = AssertSuccess(await fixture.Automation.LoadSaveStateAsync(state.Id, CancellationToken.None));

		Assert.Equal(0, loaded.PreviousMutableStateGeneration);
		Assert.Equal(1, loaded.MutableStateGeneration);
		Assert.Equal(1, api.LoadSaveStateCalls);
		Assert.Equal("resource_not_found", fixture.SaveStates.Pin(state.Id).Error?.Code);
	}

	[Theory]
	[InlineData("none")]
	[InlineData("reset")]
	[InlineData("multiple")]
	[InlineData("rom")]
	[InlineData("transition")]
	[InlineData("blocked")]
	public async Task LoadSaveState_RequiresExactlyOneAttributedStateLoadedNotification(string notifications)
	{
		FakeMcpEmulatorApi api = FakeMcpEmulatorApi.RunningNes();
		api.CreateSaveStateHandler = () => McpServiceResult<byte[]>.Success([1]);
		using AutomationFixture fixture = new(api);
		McpSaveStateMetadata state = AssertSuccess(fixture.Automation.CreateSaveState());
		api.LoadSaveStateHandler = _ => {
			if(notifications == "reset") {
				fixture.Emulator.ProcessNotification(new NotificationEventArgs { NotificationType = ConsoleNotificationType.GameReset });
			} else if(notifications == "multiple") {
				fixture.Emulator.ProcessNotification(new NotificationEventArgs { NotificationType = ConsoleNotificationType.StateLoaded });
				fixture.Emulator.ProcessNotification(new NotificationEventArgs { NotificationType = ConsoleNotificationType.GameReset });
			} else if(notifications == "rom") {
				fixture.Identity.NotifyRomChanged();
				fixture.Emulator.ProcessNotification(new NotificationEventArgs { NotificationType = ConsoleNotificationType.StateLoaded });
			} else if(notifications == "transition") {
				fixture.Emulator.ProcessNotification(new NotificationEventArgs { NotificationType = ConsoleNotificationType.StateLoaded });
				fixture.Emulator.ProcessNotification(new NotificationEventArgs { NotificationType = ConsoleNotificationType.BeforeGameLoad });
			} else if(notifications == "blocked") {
				fixture.Emulator.ProcessNotification(new NotificationEventArgs { NotificationType = ConsoleNotificationType.StateLoaded });
				api.SetDebuggerRequestBlocked(true);
			}
			return McpServiceResult<bool>.Success(true);
		};

		McpServiceResult<McpSaveStateLoadResult> result =
			await fixture.Automation.LoadSaveStateAsync(state.Id, CancellationToken.None);

		Assert.Equal("state_changed", result.Error?.Code);
	}

	[Fact]
	public async Task LoadSaveState_RejectsConcurrentExecutionAndStaleRomBeforeNativeMutation()
	{
		FakeMcpEmulatorApi api = FakeMcpEmulatorApi.RunningNes();
		api.CreateSaveStateHandler = () => McpServiceResult<byte[]>.Success([1]);
		using AutomationFixture fixture = new(api);
		McpSaveStateMetadata state = AssertSuccess(fixture.Automation.CreateSaveState());
		await using(McpExecutionLease lease = AssertSuccess(await fixture.Coordinator.TryAcquireAsync(CancellationToken.None))) {
			Assert.Equal("operation_in_progress", (await fixture.Automation.LoadSaveStateAsync(state.Id, CancellationToken.None)).Error?.Code);
		}

		fixture.Identity.NotifyRomChanged();
		fixture.Automation.InvalidateRomResources();
		Assert.Equal("stale_resource", (await fixture.Automation.LoadSaveStateAsync(state.Id, CancellationToken.None)).Error?.Code);
		Assert.Equal(0, api.LoadSaveStateCalls);
	}

	[Fact]
	public async Task StateLoaded_InvalidatesDebuggerGenerationAndDefersTraceCleanupUntilNativeLoadReturns()
	{
		FakeMcpEmulatorApi api = FakeMcpEmulatorApi.RunningNes();
		api.CreateSaveStateHandler = () => McpServiceResult<byte[]>.Success([1]);
		using AutomationFixture fixture = new(api);
		McpSaveStateMetadata state = AssertSuccess(fixture.Automation.CreateSaveState());
		AssertSuccess(fixture.Emulator.ConfigureExecutionTrace(nameof(CpuType.Nes), "enable", false, false, null, null));
		bool loadReturned = false;
		api.ClearExecutionTraceHandler = () => Assert.True(loadReturned);
		api.LoadSaveStateHandler = _ => {
			fixture.Emulator.ProcessNotification(new NotificationEventArgs { NotificationType = ConsoleNotificationType.StateLoaded });
			Assert.Equal(0, api.ClearExecutionTraceCalls);
			loadReturned = true;
			return McpServiceResult<bool>.Success(true);
		};

		McpServiceResult<McpSaveStateLoadResult> result = await fixture.Automation.LoadSaveStateAsync(state.Id, CancellationToken.None);

		Assert.True(result.IsSuccess, result.Error?.Message);
		Assert.Equal(1, api.ClearExecutionTraceCalls);
		Assert.Equal("trace_not_owned", fixture.Emulator.GetExecutionTrace(1).Error?.Code);
		Assert.Equal("stale_context", fixture.Emulator.GetBreakContext(0, 0, 1).Error?.Code);
	}

	[Theory]
	[InlineData(McpAutomationLimits.MaxScreenshotDimension + 1, 1, 1)]
	[InlineData(1, McpAutomationLimits.MaxScreenshotDimension + 1, 1)]
	[InlineData(1, 1, McpAutomationLimits.MaxPngBytes + 1)]
	public void CaptureScreenshot_RejectsManagedLimitViolations(int width, int height, int pngBytes)
	{
		FakeMcpEmulatorApi api = FakeMcpEmulatorApi.RunningNes();
		api.CaptureScreenshotHandler = () => McpServiceResult<McpScreenshotCapture>.Success(new(
			new(width, height, 3, pngBytes, 0, 0),
			new byte[pngBytes]));
		using AutomationFixture fixture = new(api);

		Assert.Equal("payload_too_large", fixture.Automation.CaptureScreenshot().Error?.Code);
	}

	[Theory]
	[InlineData(0, 1)]
	[InlineData(1, 0)]
	public void CaptureScreenshot_RejectsZeroDimensions(int width, int height)
	{
		FakeMcpEmulatorApi api = FakeMcpEmulatorApi.RunningNes();
		api.CaptureScreenshotHandler = () => McpServiceResult<McpScreenshotCapture>.Success(new(
			new(width, height, 3, 1, 0, 0),
			[1]));
		using AutomationFixture fixture = new(api);

		Assert.Equal("interop_failure", fixture.Automation.CaptureScreenshot().Error?.Code);
	}

	[Fact]
	public void CaptureScreenshot_RejectsExcessivePixelArea()
	{
		FakeMcpEmulatorApi api = FakeMcpEmulatorApi.RunningNes();
		api.CaptureScreenshotHandler = () => McpServiceResult<McpScreenshotCapture>.Success(new(
			new(McpAutomationLimits.MaxScreenshotDimension, McpAutomationLimits.MaxScreenshotDimension + 1, 3, 1, 0, 0),
			[1]));
		using AutomationFixture fixture = new(api);

		Assert.Equal("payload_too_large", fixture.Automation.CaptureScreenshot().Error?.Code);
	}

	[Fact]
	public void CaptureScreenshot_RejectsPngMetadataLengthMismatch()
	{
		FakeMcpEmulatorApi api = FakeMcpEmulatorApi.RunningNes();
		api.CaptureScreenshotHandler = () => McpServiceResult<McpScreenshotCapture>.Success(new(
			new(1, 1, 3, 2, 0, 0),
			[1]));
		using AutomationFixture fixture = new(api);

		Assert.Equal("interop_failure", fixture.Automation.CaptureScreenshot().Error?.Code);
	}

	private static T AssertSuccess<T>(McpServiceResult<T> result)
	{
		Assert.True(result.IsSuccess, result.Error?.Code);
		return result.Value!;
	}

	private sealed class AutomationFixture : IDisposable
	{
		internal AutomationFixture(FakeMcpEmulatorApi api, Action? debuggerInitialize = null)
		{
			Identity = new();
			Coordinator = new();
			Emulator = new(
				api,
				debuggerLifetime: new DebuggerLifetimeCoordinator(debuggerInitialize ?? (() => { }), () => { }),
				breakpointCollection: new NoOpBreakpointCollection(),
				traceCoordinator: new TraceLoggerCoordinator(),
				emulatorIdentity: Identity,
				executionCoordinator: Coordinator);
			SaveStates = new();
			Automation = new(Emulator, new McpAutomationAdapterRegistry(api), SaveStates);
		}

		internal McpEmulatorIdentity Identity { get; }
		internal McpExecutionCoordinator Coordinator { get; }
		internal McpEmulatorService Emulator { get; }
		internal McpSaveStateStore SaveStates { get; }
		internal McpAutomationService Automation { get; }

		public void Dispose()
		{
			SaveStates.Dispose();
			Emulator.Dispose();
		}
	}

	private sealed class NoOpBreakpointCollection : IMcpBreakpointCollection
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
