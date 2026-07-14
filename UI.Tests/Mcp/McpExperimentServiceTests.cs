using Mesen.Config;
using Mesen.Debugger;
using Mesen.Debugger.Utilities;
using Mesen.Interop;
using Mesen.Mcp;
using System.Runtime.InteropServices;

namespace UI.Tests.Mcp;

public sealed class McpExperimentServiceTests
{
	[Fact]
	public async Task RunAsync_ComposesSegmentsWithCompleteNeutralInputAndFinalArtifacts()
	{
		using ExperimentFixture fixture = new();
		fixture.Api.ReadData = [0x2A];
		fixture.Api.CaptureScreenshotHandler = () => McpServiceResult<McpScreenshotCapture>.Success(
			new(new(1, 1, 7, 1, 0, 0), [0x89]));
		fixture.CompleteEveryStep();
		RunExperimentRequest request = new(
			"Nes", null,
			[
				new(2, [new(0, ["A"]), new(1, ["B"])], "first"),
				new(3, [new(0, ["Right"])], null)
			],
			1000,
			[new("initial-value", "initial", nameof(MemoryType.NesMemory), 0, 1, null),
			 new("final-value", "final", nameof(MemoryType.NesMemory), 0, 1, null)],
			[], true, false);

		McpExperimentCapture capture = AssertCapture(await fixture.Experiments.RunAsync(request, CancellationToken.None));
		RunExperimentResult result = capture.Result;

		Assert.Equal(McpExperimentStatus.Completed, result.Status);
		Assert.Equal([0, 1], result.CompletedSegments);
		Assert.Equal(5, result.CompletedFrames);
		Assert.Equal(["initial", "first", "final"], result.Checkpoints.Select(checkpoint => checkpoint.Name));
		Assert.NotNull(result.Screenshot);
		Assert.Equal([0x89], capture.Png);
		Assert.True(result.Cleanup.StopConfirmed);
		Assert.True(result.Cleanup.InputReleased);
		Assert.True(result.Cleanup.LeaseReleased);
		Assert.False(result.Cleanup.Quarantined);

		McpExclusiveControllerState[] portOneStates = fixture.Api.ExclusiveControllerStates
			.Where(state => state.Port == 1).ToArray();
		Assert.Equal(3, portOneStates.Length);
		Assert.Empty(portOneStates[0].Values);
		Assert.Single(portOneStates[1].Values);
		Assert.Empty(portOneStates[2].Values);
		Assert.True(fixture.Api.NativeCallLog.IndexOf("screenshot") < fixture.Api.NativeCallLog.IndexOf("clear-input"));
	}

	[Fact]
	public async Task RunAsync_PreflightFailureDoesNotAcquireOrMutate()
	{
		using ExperimentFixture fixture = new();
		RunExperimentRequest request = BasicRequest() with { CaptureFinalScreenshot = true, Cpu = "nes" };

		McpServiceResult<McpExperimentCapture> result = await fixture.Experiments.RunAsync(request, CancellationToken.None);

		Assert.Equal("invalid_request", result.Error?.Code);
		Assert.Empty(fixture.Api.NativeCallLog);
		await using McpExecutionLease lease = AssertSuccess(await fixture.Coordinator.TryAcquireAsync(CancellationToken.None));
	}

	[Fact]
	public async Task RunAsync_FailFastReturnsAssertionFailureWithSkippedSegments()
	{
		using ExperimentFixture fixture = new();
		fixture.Api.ReadData = [2];
		fixture.CompleteEveryStep();
		RunExperimentRequest request = BasicRequest() with {
			Segments = [new(2, [], "check"), new(4, [], null)],
			Observations = [new("value", "check", nameof(MemoryType.NesMemory), 0, 1, null)],
			Assertions = [new("expected", "check", "value", "equal", [1], null, null, null, null, null)],
			FailFast = true
		};

		RunExperimentResult result = AssertSuccess(await fixture.Experiments.RunAsync(request, CancellationToken.None));

		Assert.Equal(McpExperimentStatus.AssertionFailed, result.Status);
		Assert.Equal([0], result.CompletedSegments);
		Assert.Equal([1], result.SkippedSegments);
		Assert.Equal(2, result.CompletedFrames);
		Assert.Equal(["initial", "check"], result.Checkpoints.Select(checkpoint => checkpoint.Name));
		Assert.Equal(1, fixture.Api.StepCalls);
	}

	[Fact]
	public async Task RunAsync_FailFastAtLastSegmentDoesNotCaptureFinalArtifacts()
	{
		using ExperimentFixture fixture = new();
		fixture.Api.ReadData = [2];
		fixture.CompleteEveryStep();
		RunExperimentRequest request = BasicRequest() with {
			Segments = [new(1, [], "check")],
			Observations = [new("value", "check", nameof(MemoryType.NesMemory), 0, 1, null)],
			Assertions = [new("expected", "check", "value", "equal", [1], null, null, null, null, null)],
			CaptureFinalScreenshot = true,
			FailFast = true
		};

		RunExperimentResult result = AssertSuccess(await fixture.Experiments.RunAsync(request, CancellationToken.None));

		Assert.Equal(McpExperimentStatus.AssertionFailed, result.Status);
		Assert.Equal(["initial", "check"], result.Checkpoints.Select(checkpoint => checkpoint.Name));
		Assert.Null(result.Screenshot);
		Assert.Equal(0, fixture.Api.CaptureScreenshotCalls);
	}

	[Fact]
	public async Task RunAsync_FinalFailFastAssertionSkipsScreenshot()
	{
		using ExperimentFixture fixture = new();
		fixture.Api.ReadData = [2];
		fixture.CompleteEveryStep();
		RunExperimentRequest request = BasicRequest() with {
			Observations = [new("value", "final", nameof(MemoryType.NesMemory), 0, 1, null)],
			Assertions = [new("expected", "final", "value", "equal", [1], null, null, null, null, null)],
			CaptureFinalScreenshot = true,
			FailFast = true
		};

		RunExperimentResult result = AssertSuccess(await fixture.Experiments.RunAsync(request, CancellationToken.None));

		Assert.Equal(McpExperimentStatus.AssertionFailed, result.Status);
		Assert.Equal(["initial", "final"], result.Checkpoints.Select(checkpoint => checkpoint.Name));
		Assert.Null(result.Screenshot);
		Assert.Equal(0, fixture.Api.CaptureScreenshotCalls);
	}

	[Fact]
	public async Task RunAsync_BreakpointReturnsInterruptedPartialResultAndReleasesAfterStop()
	{
		using ExperimentFixture fixture = new();
		fixture.Api.StepHandler = (_, _, _) => SendBreak(fixture.Emulator, new BreakEvent {
			Source = BreakSource.Breakpoint,
			SourceCpu = CpuType.Nes,
			BreakpointId = 9
		});

		RunExperimentResult result = AssertSuccess(await fixture.Experiments.RunAsync(BasicRequest(), CancellationToken.None));

		Assert.Equal(McpExperimentStatus.Interrupted, result.Status);
		Assert.Equal(McpExperimentReason.Breakpoint, result.Reason);
		Assert.Empty(result.CompletedSegments);
		Assert.Equal([0], result.SkippedSegments);
		Assert.Equal(0, result.CompletedFrames);
		Assert.True(result.Cleanup.StopConfirmed);
		Assert.Equal("clear-input", fixture.Api.NativeCallLog[^1]);
	}

	[Fact]
	public async Task RunAsync_RestoresPinnedStateBeforeInputAndAcceptsOneStateLoadedChange()
	{
		using ExperimentFixture fixture = new();
		fixture.Api.CreateSaveStateHandler = () => McpServiceResult<byte[]>.Success([0x12]);
		McpSaveStateMetadata state = AssertSuccess(fixture.Automation.CreateSaveState());
		fixture.Api.LoadSaveStateHandler = data => {
			Assert.Equal([0x12], data);
			Assert.True(fixture.SaveStates.Delete(state.Id).IsSuccess);
			fixture.Emulator.ProcessNotification(new() { NotificationType = ConsoleNotificationType.StateLoaded });
			return McpServiceResult<bool>.Success(true);
		};
		fixture.CompleteEveryStep();

		RunExperimentResult result = AssertSuccess(await fixture.Experiments.RunAsync(
			BasicRequest() with { SaveStateId = state.Id, Segments = [new(1, [new(0, ["A"])], null)] },
			CancellationToken.None));

		Assert.Equal(McpExperimentStatus.Completed, result.Status);
		Assert.Equal("load-state", fixture.Api.NativeCallLog[0]);
		Assert.StartsWith("input:", fixture.Api.NativeCallLog[1]);
		Assert.Equal("resource_not_found", fixture.SaveStates.Pin(state.Id).Error?.Code);
	}

	[Theory]
	[InlineData(ConsoleNotificationType.GameReset, McpExperimentReason.Reset)]
	[InlineData(ConsoleNotificationType.BeforeGameLoad, McpExperimentReason.RomTransition)]
	public async Task RunAsync_LifecycleChangeReturnsInterruptedPartialResult(
		ConsoleNotificationType notification,
		string expectedReason)
	{
		using ExperimentFixture fixture = new();
		fixture.Api.StepHandler = (_, _, _) => {
			fixture.Emulator.ProcessNotification(new() { NotificationType = notification });
			if(notification == ConsoleNotificationType.BeforeGameLoad) {
				fixture.Emulator.ProcessNotification(new() { NotificationType = ConsoleNotificationType.GameLoaded });
			}
		};

		RunExperimentResult result = AssertSuccess(await fixture.Experiments.RunAsync(BasicRequest(), CancellationToken.None));

		Assert.Equal(McpExperimentStatus.Interrupted, result.Status);
		Assert.Equal(expectedReason, result.Reason);
		Assert.Equal(0, result.CompletedFrames);
	}

	[Theory]
	[InlineData(ConsoleNotificationType.GameReset)]
	[InlineData(ConsoleNotificationType.BeforeGameLoad)]
	public async Task RunAsync_LifecycleChangeBetweenInputAndStepDoesNotUsePreparedNativeIndices(
		ConsoleNotificationType notification)
	{
		using ExperimentFixture fixture = new();
		fixture.Api.SetExclusiveControllerOverrideHandler = _ => {
			fixture.Emulator.ProcessNotification(new() { NotificationType = notification });
			if(notification == ConsoleNotificationType.BeforeGameLoad) {
				fixture.Emulator.ProcessNotification(new() { NotificationType = ConsoleNotificationType.GameLoaded });
			}
			return true;
		};

		RunExperimentResult result = AssertSuccess(await fixture.Experiments.RunAsync(
			BasicRequest() with { Segments = [new(1, [new(0, ["A"])], null)] }, CancellationToken.None));

		Assert.Equal(McpExperimentStatus.Interrupted, result.Status);
		Assert.Equal(McpExperimentReason.StateChanged, result.Reason);
		Assert.Equal(0, fixture.Api.StepCalls);
		Assert.Empty(result.CompletedSegments);
	}

	[Fact]
	public async Task RunAsync_StateChangeAfterCorrelatedStepPreservesFramesAndSkipsCheckpoint()
	{
		using ExperimentFixture fixture = new();
		fixture.Api.StepHandler = (_, _, _) => {
			SendBreak(fixture.Emulator, new BreakEvent { Source = BreakSource.PpuStep, SourceCpu = CpuType.Nes });
			fixture.Emulator.ProcessNotification(new() { NotificationType = ConsoleNotificationType.GameReset });
		};
		RunExperimentRequest request = BasicRequest() with { Segments = [new(3, [], "after")] };

		RunExperimentResult result = AssertSuccess(await fixture.Experiments.RunAsync(request, CancellationToken.None));

		Assert.Equal(McpExperimentStatus.Interrupted, result.Status);
		Assert.Equal(McpExperimentReason.StateChanged, result.Reason);
		Assert.Equal(3, result.CompletedFrames);
		Assert.Equal([0], result.CompletedSegments);
		Assert.Equal(["initial"], result.Checkpoints.Select(item => item.Name));
	}

	[Fact]
	public async Task RunAsync_StateChangeBeforeScreenshotSkipsStaleCapture()
	{
		using ExperimentFixture fixture = new();
		fixture.CompleteEveryStep();
		int stoppedCalls = 0;
		fixture.Api.IsExecutionStoppedHandler = () => {
			if(++stoppedCalls == 4) {
				fixture.Emulator.ProcessNotification(new() { NotificationType = ConsoleNotificationType.GameReset });
			}
			return true;
		};
		fixture.Api.CaptureScreenshotHandler = () => McpServiceResult<McpScreenshotCapture>.Success(
			new(new(1, 1, 1, 1, 0, 0), [1]));

		RunExperimentResult result = AssertSuccess(await fixture.Experiments.RunAsync(
			BasicRequest() with { CaptureFinalScreenshot = true }, CancellationToken.None));

		Assert.Equal(McpExperimentStatus.Interrupted, result.Status);
		Assert.Equal(McpExperimentReason.StateChanged, result.Reason);
		Assert.Null(result.Screenshot);
		Assert.Equal(0, fixture.Api.CaptureScreenshotCalls);
	}

	[Fact]
	public async Task RunAsync_CancellationAfterStepStartsReturnsPartialResult()
	{
		using ExperimentFixture fixture = new();
		using CancellationTokenSource cancellation = new();
		fixture.Api.StepHandler = (_, _, _) => cancellation.Cancel();

		RunExperimentResult result = AssertSuccess(await fixture.Experiments.RunAsync(BasicRequest(), cancellation.Token));

		Assert.Equal(McpExperimentStatus.Interrupted, result.Status);
		Assert.Equal(McpExperimentReason.Cancelled, result.Reason);
		Assert.Equal(0, result.CompletedFrames);
		Assert.True(result.Cleanup.StopConfirmed);
	}

	[Fact]
	public async Task RunAsync_CancellationDuringInitialStopSkipsCheckpointsAndSegments()
	{
		using ExperimentFixture fixture = new();
		using CancellationTokenSource cancellation = new();
		bool stopped = false;
		fixture.Api.IsExecutionStoppedHandler = () => stopped;
		fixture.Api.PauseHandler = () => {
			stopped = true;
			cancellation.Cancel();
		};

		RunExperimentResult result = AssertSuccess(await fixture.Experiments.RunAsync(BasicRequest(), cancellation.Token));

		Assert.Equal(McpExperimentStatus.Interrupted, result.Status);
		Assert.Equal(McpExperimentReason.Cancelled, result.Reason);
		Assert.Empty(result.Checkpoints);
		Assert.Equal(0, fixture.Api.StepCalls);
		Assert.True(result.Cleanup.StopConfirmed);
	}

	[Fact]
	public async Task RunAsync_BreakpointDuringInitialStopInterruptsBeforeInitialCheckpoint()
	{
		using ExperimentFixture fixture = new();
		bool stopped = false;
		fixture.Api.IsExecutionStoppedHandler = () => stopped;
		fixture.Api.PauseHandler = () => {
			stopped = true;
			SendBreak(fixture.Emulator, new BreakEvent {
				Source = BreakSource.Breakpoint,
				SourceCpu = CpuType.Nes,
				BreakpointId = 7
			});
		};

		RunExperimentResult result = AssertSuccess(await fixture.Experiments.RunAsync(
			BasicRequest() with { CaptureFinalScreenshot = true }, CancellationToken.None));

		Assert.Equal(McpExperimentStatus.Interrupted, result.Status);
		Assert.Equal(McpExperimentReason.Breakpoint, result.Reason);
		Assert.Equal("breakpoint", result.Interruption?.BreakContext?.Reason);
		Assert.Empty(result.Checkpoints);
		Assert.Empty(result.CompletedSegments);
		Assert.Equal(0, fixture.Api.StepCalls);
		Assert.Equal(0, fixture.Api.CaptureScreenshotCalls);
	}

	[Theory]
	[InlineData(ConsoleNotificationType.GameReset, McpExperimentReason.Reset)]
	[InlineData(ConsoleNotificationType.BeforeGameLoad, McpExperimentReason.RomTransition)]
	public async Task RunAsync_LifecycleDuringInitialStopRetainsDedicatedReason(
		ConsoleNotificationType notification,
		string expectedReason)
	{
		using ExperimentFixture fixture = new();
		bool stopped = false;
		fixture.Api.IsExecutionStoppedHandler = () => stopped;
		fixture.Api.PauseHandler = () => {
			stopped = true;
			fixture.Emulator.ProcessNotification(new() { NotificationType = notification });
			if(notification == ConsoleNotificationType.BeforeGameLoad) {
				fixture.Emulator.ProcessNotification(new() { NotificationType = ConsoleNotificationType.GameLoaded });
			}
		};

		RunExperimentResult result = AssertSuccess(await fixture.Experiments.RunAsync(
			BasicRequest() with { CaptureFinalScreenshot = true }, CancellationToken.None));

		Assert.Equal(McpExperimentStatus.Interrupted, result.Status);
		Assert.Equal(expectedReason, result.Reason);
		Assert.Null(result.Interruption?.BreakContext);
		Assert.Empty(result.Checkpoints);
		Assert.Empty(result.CompletedSegments);
		Assert.Equal(0, fixture.Api.StepCalls);
		Assert.Equal(0, fixture.Api.CaptureScreenshotCalls);
	}

	[Fact]
	public async Task RunAsync_CancellationDuringInitialCheckpointKeepsOnlyInitialCheckpoint()
	{
		using ExperimentFixture fixture = new();
		using CancellationTokenSource cancellation = new();
		fixture.Api.GetMemoryValuesHandler = (_, _, _) => {
			cancellation.Cancel();
			return [1];
		};
		RunExperimentRequest request = BasicRequest() with {
			Observations = [new("value", "initial", nameof(MemoryType.NesMemory), 0, 1, null)]
		};

		RunExperimentResult result = AssertSuccess(await fixture.Experiments.RunAsync(request, cancellation.Token));

		Assert.Equal(McpExperimentStatus.Interrupted, result.Status);
		Assert.Equal(McpExperimentReason.Cancelled, result.Reason);
		Assert.Equal(["initial"], result.Checkpoints.Select(item => item.Name));
		Assert.Equal(0, fixture.Api.StepCalls);
	}

	[Fact]
	public async Task RunAsync_CancellationDuringNamedCheckpointSkipsRemainingSegmentsAndFinal()
	{
		using ExperimentFixture fixture = new();
		using CancellationTokenSource cancellation = new();
		fixture.CompleteEveryStep();
		fixture.Api.GetMemoryValuesHandler = (_, _, _) => {
			cancellation.Cancel();
			return [1];
		};
		RunExperimentRequest request = BasicRequest() with {
			Segments = [new(1, [], "named"), new(1, [], null)],
			Observations = [new("value", "named", nameof(MemoryType.NesMemory), 0, 1, null)]
		};

		RunExperimentResult result = AssertSuccess(await fixture.Experiments.RunAsync(request, cancellation.Token));

		Assert.Equal(McpExperimentStatus.Interrupted, result.Status);
		Assert.Equal(McpExperimentReason.Cancelled, result.Reason);
		Assert.Equal(["initial", "named"], result.Checkpoints.Select(item => item.Name));
		Assert.Equal([0], result.CompletedSegments);
		Assert.Equal(1, fixture.Api.StepCalls);
	}

	[Fact]
	public async Task RunAsync_CancellationDuringFinalCheckpointSkipsScreenshot()
	{
		using ExperimentFixture fixture = new();
		using CancellationTokenSource cancellation = new();
		fixture.CompleteEveryStep();
		fixture.Api.GetMemoryValuesHandler = (_, _, _) => {
			cancellation.Cancel();
			return [1];
		};
		fixture.Api.CaptureScreenshotHandler = () => McpServiceResult<McpScreenshotCapture>.Success(
			new(new(1, 1, 1, 1, 0, 0), [1]));
		RunExperimentRequest request = BasicRequest() with {
			Observations = [new("value", "final", nameof(MemoryType.NesMemory), 0, 1, null)],
			CaptureFinalScreenshot = true
		};

		RunExperimentResult result = AssertSuccess(await fixture.Experiments.RunAsync(request, cancellation.Token));

		Assert.Equal(McpExperimentStatus.Interrupted, result.Status);
		Assert.Equal(McpExperimentReason.Cancelled, result.Reason);
		Assert.Equal(["initial", "final"], result.Checkpoints.Select(item => item.Name));
		Assert.Null(result.Screenshot);
		Assert.Equal(0, fixture.Api.CaptureScreenshotCalls);
	}

	[Fact]
	public async Task RunAsync_CancellationDuringScreenshotDoesNotAttachCaptureOrComplete()
	{
		using ExperimentFixture fixture = new();
		using CancellationTokenSource cancellation = new();
		fixture.CompleteEveryStep();
		fixture.Api.CaptureScreenshotHandler = () => {
			cancellation.Cancel();
			return McpServiceResult<McpScreenshotCapture>.Success(new(new(1, 1, 1, 1, 0, 0), [1]));
		};

		McpExperimentCapture capture = AssertCapture(await fixture.Experiments.RunAsync(
			BasicRequest() with { CaptureFinalScreenshot = true }, cancellation.Token));
		RunExperimentResult result = capture.Result;

		Assert.Equal(McpExperimentStatus.Interrupted, result.Status);
		Assert.Equal(McpExperimentReason.Cancelled, result.Reason);
		Assert.Null(result.Screenshot);
		Assert.Null(capture.Png);
		Assert.Equal(1, fixture.Api.CaptureScreenshotCalls);
	}

	[Fact]
	public async Task RunAsync_CancellationAfterScreenshotAssignmentClearsCapture()
	{
		using CancellationTokenSource cancellation = new();
		using ExperimentFixture fixture = new(afterScreenshotAssigned: cancellation.Cancel);
		fixture.CompleteEveryStep();
		fixture.Api.CaptureScreenshotHandler = () => McpServiceResult<McpScreenshotCapture>.Success(
			new(new(1, 1, 1, 1, 0, 0), [1]));

		McpExperimentCapture capture = AssertCapture(await fixture.Experiments.RunAsync(
			BasicRequest() with { CaptureFinalScreenshot = true }, cancellation.Token));
		RunExperimentResult result = capture.Result;

		Assert.Equal(McpExperimentStatus.Interrupted, result.Status);
		Assert.Equal(McpExperimentReason.Cancelled, result.Reason);
		Assert.Null(result.Screenshot);
		Assert.Null(capture.Png);
		Assert.Equal(1, fixture.Api.CaptureScreenshotCalls);
	}

	[Fact]
	public async Task RunAsync_UncorrelatedIdentityDriftDuringCheckpointUsesStateChanged()
	{
		using ExperimentFixture fixture = new();
		fixture.Api.GetMemoryValuesHandler = (_, _, _) => {
			fixture.Emulator.ProcessNotification(new() { NotificationType = ConsoleNotificationType.GameReset });
			return [1];
		};
		RunExperimentRequest request = BasicRequest() with {
			Observations = [new("value", "initial", nameof(MemoryType.NesMemory), 0, 1, null)]
		};

		RunExperimentResult result = AssertSuccess(await fixture.Experiments.RunAsync(request, CancellationToken.None));

		Assert.Equal(McpExperimentStatus.Interrupted, result.Status);
		Assert.Equal(McpExperimentReason.StateChanged, result.Reason);
		Assert.Empty(result.Checkpoints);
		Assert.Equal(0, fixture.Api.StepCalls);
	}

	[Fact]
	public async Task RunAsync_TimeoutDoesNotClaimUncorrelatedFrames()
	{
		using ExperimentFixture fixture = new();

		RunExperimentResult result = AssertSuccess(await fixture.Experiments.RunAsync(
			BasicRequest() with { TimeoutMs = 1 }, CancellationToken.None));

		Assert.Equal(McpExperimentStatus.Failed, result.Status);
		Assert.Equal(McpExperimentReason.Timeout, result.Reason);
		Assert.Equal(0, result.CompletedFrames);
		Assert.Empty(result.CompletedSegments);
		Assert.True(result.Cleanup.StopConfirmed);
		Assert.False(result.Cleanup.Quarantined);
	}

	[Fact]
	public async Task RunAsync_DeadlineExpiredBeforeRestoreDoesNotLoadState()
	{
		ManualClock clock = new();
		using ExperimentFixture fixture = new(clock);
		fixture.Api.CreateSaveStateHandler = () => McpServiceResult<byte[]>.Success([1]);
		McpSaveStateMetadata state = AssertSuccess(fixture.Automation.CreateSaveState());
		IReadOnlyList<McpControllerTopology> topology = fixture.Api.ControllerTopology;
		fixture.Api.GetControllerTopologyHandler = () => {
			clock.Advance(TimeSpan.FromMilliseconds(1001));
			return topology;
		};

		RunExperimentResult result = AssertSuccess(await fixture.Experiments.RunAsync(
			BasicRequest() with { SaveStateId = state.Id }, CancellationToken.None));

		Assert.Equal(McpExperimentReason.Timeout, result.Reason);
		Assert.Equal(0, fixture.Api.LoadSaveStateCalls);
		Assert.Empty(result.Checkpoints);
	}

	[Fact]
	public async Task RunAsync_ObservationOverrunKeepsCheckpointButSkipsExecution()
	{
		ManualClock clock = new();
		using ExperimentFixture fixture = new(clock);
		fixture.Api.GetMemoryValuesHandler = (_, _, _) => {
			clock.Advance(TimeSpan.FromMilliseconds(1001));
			return [1];
		};
		RunExperimentRequest request = BasicRequest() with {
			Observations = [new("value", "initial", nameof(MemoryType.NesMemory), 0, 1, null)]
		};

		RunExperimentResult result = AssertSuccess(await fixture.Experiments.RunAsync(request, CancellationToken.None));

		Assert.Equal(McpExperimentReason.Timeout, result.Reason);
		Assert.Equal(["initial"], result.Checkpoints.Select(item => item.Name));
		Assert.Equal(0, fixture.Api.StepCalls);
	}

	[Fact]
	public async Task RunAsync_ExpirationAfterFinalCheckpointSkipsScreenshotAndCompletion()
	{
		ManualClock clock = new();
		using ExperimentFixture fixture = new(clock);
		fixture.CompleteEveryStep();
		fixture.Api.GetMemoryValuesHandler = (_, _, _) => {
			clock.Advance(TimeSpan.FromMilliseconds(1001));
			return [1];
		};
		fixture.Api.CaptureScreenshotHandler = () => McpServiceResult<McpScreenshotCapture>.Success(
			new(new(1, 1, 1, 1, 0, 0), [1]));
		RunExperimentRequest request = BasicRequest() with {
			Observations = [new("value", "final", nameof(MemoryType.NesMemory), 0, 1, null)],
			CaptureFinalScreenshot = true
		};

		RunExperimentResult result = AssertSuccess(await fixture.Experiments.RunAsync(request, CancellationToken.None));

		Assert.Equal(McpExperimentReason.Timeout, result.Reason);
		Assert.Equal(["initial", "final"], result.Checkpoints.Select(item => item.Name));
		Assert.Null(result.Screenshot);
		Assert.Equal(0, fixture.Api.CaptureScreenshotCalls);
	}

	[Fact]
	public async Task RunAsync_StaleBreakpointIdentityReturnsStateChangedWithoutContext()
	{
		using ExperimentFixture fixture = new();
		fixture.Api.StepHandler = (_, _, _) => {
			SendBreak(fixture.Emulator, new BreakEvent { Source = BreakSource.Breakpoint, SourceCpu = CpuType.Nes });
			fixture.Emulator.ProcessNotification(new() { NotificationType = ConsoleNotificationType.GameReset });
		};

		RunExperimentResult result = AssertSuccess(await fixture.Experiments.RunAsync(BasicRequest(), CancellationToken.None));

		Assert.Equal(McpExperimentReason.StateChanged, result.Reason);
		Assert.Null(result.Interruption?.BreakContext);
	}

	[Fact]
	public async Task RunAsync_BreakContextBuildFailureReturnsStateChangedWithoutContext()
	{
		using ExperimentFixture fixture = new();
		fixture.Api.StepHandler = (_, _, _) => SendBreak(fixture.Emulator, new BreakEvent {
			Source = BreakSource.Breakpoint,
			SourceCpu = CpuType.Spc
		});

		RunExperimentResult result = AssertSuccess(await fixture.Experiments.RunAsync(BasicRequest(), CancellationToken.None));

		Assert.Equal(McpExperimentStatus.Interrupted, result.Status);
		Assert.Equal(McpExperimentReason.StateChanged, result.Reason);
		Assert.Null(result.Interruption?.BreakContext);
	}

	[Fact]
	public async Task RunAsync_UnconfirmedCleanupReleasesInputThenQuarantines()
	{
		using ExperimentFixture fixture = new();
		fixture.Api.IsExecutionStoppedHandler = () => fixture.Api.StepCalls == 0;
		RunExperimentRequest request = BasicRequest() with {
			Segments = [new(1, [new(0, ["A"])], null)],
			TimeoutMs = 10
		};

		RunExperimentResult result = AssertSuccess(await fixture.Experiments.RunAsync(request, CancellationToken.None));

		Assert.Equal(McpExperimentStatus.Failed, result.Status);
		Assert.Equal(McpExperimentReason.CleanupFailed, result.Reason);
		Assert.False(result.Cleanup.StopConfirmed);
		Assert.True(result.Cleanup.InputReleased);
		Assert.True(result.Cleanup.Quarantined);
		Assert.Equal("clear-input", fixture.Api.NativeCallLog[^1]);
		Assert.Equal("execution_state_unknown", (await fixture.Coordinator.TryAcquireAsync(CancellationToken.None)).Error?.Code);
	}

	[Fact]
	public async Task RunAsync_InputReleaseFailureQuarantinesAndStillReleasesLease()
	{
		using ExperimentFixture fixture = new();
		fixture.CompleteEveryStep();
		fixture.Api.ClearExclusiveControllerOverridesHandler = () => throw new InvalidOperationException("native details");

		RunExperimentResult result = AssertSuccess(await fixture.Experiments.RunAsync(BasicRequest(), CancellationToken.None));

		Assert.Equal(McpExperimentReason.CleanupFailed, result.Reason);
		Assert.True(result.Cleanup.StopConfirmed);
		Assert.False(result.Cleanup.InputReleased);
		Assert.True(result.Cleanup.LeaseReleased);
		Assert.True(result.Cleanup.Quarantined);
		Assert.Equal("execution_state_unknown", (await fixture.Coordinator.TryAcquireAsync(CancellationToken.None)).Error?.Code);
	}

	[Fact]
	public async Task RunAsync_StopObservationExceptionReportsCleanupAndReleasesLease()
	{
		using ExperimentFixture fixture = new();
		fixture.CompleteEveryStep();
		int calls = 0;
		fixture.Api.IsExecutionStoppedHandler = () => {
			if(++calls >= 4) {
				throw new InvalidOperationException("native details");
			}
			return true;
		};

		RunExperimentResult result = AssertSuccess(await fixture.Experiments.RunAsync(BasicRequest(), CancellationToken.None));

		Assert.Equal(McpExperimentReason.CleanupFailed, result.Reason);
		Assert.False(result.Cleanup.StopConfirmed);
		Assert.True(result.Cleanup.InputReleased);
		Assert.True(result.Cleanup.LeaseReleased);
		Assert.True(result.Cleanup.Quarantined);
	}

	private static RunExperimentRequest BasicRequest() => new(
		"Nes", null, [new(1, [], null)], 1000, [], [], false, false);

	private static T AssertSuccess<T>(McpServiceResult<T> result)
	{
		Assert.True(result.IsSuccess, result.Error?.Code);
		return result.Value!;
	}

	private static RunExperimentResult AssertSuccess(McpServiceResult<McpExperimentCapture> result) =>
		AssertCapture(result).Result;

	private static McpExperimentCapture AssertCapture(McpServiceResult<McpExperimentCapture> result)
	{
		Assert.True(result.IsSuccess, result.Error?.Code);
		return result.Value!;
	}

	private static void SendBreak(McpEmulatorService service, BreakEvent breakEvent)
	{
		IntPtr pointer = Marshal.AllocHGlobal(Marshal.SizeOf<BreakEvent>());
		try {
			Marshal.StructureToPtr(breakEvent, pointer, false);
			service.ProcessNotification(new() { NotificationType = ConsoleNotificationType.CodeBreak, Parameter = pointer });
		} finally {
			Marshal.FreeHGlobal(pointer);
		}
	}

	private sealed class ExperimentFixture : IDisposable
	{
		internal ExperimentFixture(IMcpMonotonicClock? clock = null, Action? afterScreenshotAssigned = null)
		{
			Api = FakeMcpEmulatorApi.RunningNes();
			Api.MemorySizes[MemoryType.NesMemory] = 0x100;
			Api.ControllerTopology = [
				new(0, 0, ControllerType.NesController, [new("A", 0, false), new("Right", 7, false)]),
				new(1, 1, ControllerType.NesController, [new("B", 1, false)])
			];
			Coordinator = new();
			Emulator = new(
				Api,
				debuggerLifetime: new DebuggerLifetimeCoordinator(() => { }, () => { }),
				breakpointCollection: new NoOpBreakpointCollection(),
				traceCoordinator: new TraceLoggerCoordinator(),
				executionCoordinator: Coordinator);
			SaveStates = new();
			Automation = new(Emulator, new McpAutomationAdapterRegistry(Api), SaveStates);
			Experiments = new(
				Emulator, new McpAutomationAdapterRegistry(Api), SaveStates, clock, afterScreenshotAssigned);
		}

		internal FakeMcpEmulatorApi Api { get; }
		internal McpExecutionCoordinator Coordinator { get; }
		internal McpEmulatorService Emulator { get; }
		internal McpSaveStateStore SaveStates { get; }
		internal McpAutomationService Automation { get; }
		internal McpExperimentService Experiments { get; }

		internal void CompleteEveryStep() => Api.StepHandler = (_, _, _) =>
			SendBreak(Emulator, new BreakEvent { Source = BreakSource.PpuStep, SourceCpu = CpuType.Nes });

		public void Dispose()
		{
			SaveStates.Dispose();
			Emulator.Dispose();
		}
	}

	private sealed class NoOpBreakpointCollection : IMcpBreakpointCollection
	{
		public void Replace(IReadOnlyList<BreakpointManager.ExternalBreakpoint> breakpoints) { }
		public bool TryGetStableId(int nativeBreakpointId, out long stableId) { stableId = 0; return false; }
		public void Dispose() { }
	}

	private sealed class ManualClock : IMcpMonotonicClock
	{
		private long _milliseconds;

		public long GetTimestamp() => Interlocked.Read(ref _milliseconds);
		public TimeSpan GetElapsedTime(long start, long end) => TimeSpan.FromMilliseconds(end - start);
		internal void Advance(TimeSpan elapsed) => Interlocked.Add(ref _milliseconds, (long)elapsed.TotalMilliseconds);
	}
}
