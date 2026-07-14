using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mesen.Debugger;
using Mesen.Interop;

namespace Mesen.Mcp;

internal sealed class McpExperimentService
{
	private readonly McpEmulatorService _emulator;
	private readonly McpAutomationAdapterRegistry _adapters;
	private readonly McpSaveStateStore _saveStates;
	private readonly McpAutomationService _automation;
	private readonly IMcpMonotonicClock _clock;
	private readonly Action _afterScreenshotAssigned;

	internal McpExperimentService(
		McpEmulatorService emulator,
		McpAutomationAdapterRegistry adapters,
		McpSaveStateStore saveStates,
		McpAutomationService automation,
		IMcpMonotonicClock? clock = null,
		Action? afterScreenshotAssigned = null)
	{
		_emulator = emulator;
		_adapters = adapters;
		_saveStates = saveStates;
		_automation = automation;
		_clock = clock ?? McpMonotonicClock.Instance;
		_afterScreenshotAssigned = afterScreenshotAssigned ?? (() => { });
	}

	internal async Task<McpServiceResult<McpExperimentCapture>> RunAsync(
		RunExperimentRequest request,
		CancellationToken cancellationToken)
	{
		long started = _clock.GetTimestamp();
		McpServiceResult<PreparedExperiment> preparation = Prepare(request);
		if(!preparation.IsSuccess) {
			return FailureFrom<McpExperimentCapture, PreparedExperiment>(preparation);
		}
		PreparedExperiment prepared = preparation.Value!;

		McpServiceResult<McpExecutionLease> acquisition = await _emulator.ExecutionCoordinator
			.TryAcquireAsync(cancellationToken)
			.ConfigureAwait(false);
		if(!acquisition.IsSuccess) {
			return FailureFrom<McpExperimentCapture, McpExecutionLease>(acquisition);
		}

		McpExecutionLease lease = acquisition.Value!;
		McpPinnedResource<McpSaveStateResource>? statePin = null;
		List<McpCheckpointResult> checkpoints = [];
		List<McpObservationResult?> observationSlots = Enumerable
			.Repeat<McpObservationResult?>(null, prepared.Experiment.Observations.Count).ToList();
		List<McpAssertionResult> assertionResults = [];
		List<int> completedSegments = [];
		int completedFrames = 0;
		string status = McpExperimentStatus.Completed;
		string? reason = null;
		McpExperimentInterruption? interruption = null;
		McpScreenshotMetadata? screenshot = null;
		byte[]? screenshotPng = null;
		bool stopConfirmed = false;
		bool inputReleased = prepared.ControlledPorts.Count == 0;
		bool leaseReleased = false;
		bool quarantined = false;
		bool halted = false;
		McpStateIdentity expectedIdentity = prepared.PreflightIdentity;
		McpAutomationStateContext? stateContext = null;

		try {
			if(prepared.Experiment.SaveStateId is not null) {
				McpServiceResult<McpPinnedResource<McpSaveStateResource>> pinResult =
					_saveStates.Pin(prepared.Experiment.SaveStateId);
				if(!pinResult.IsSuccess) {
					await lease.DisposeAsync().ConfigureAwait(false);
					return FailureFrom<McpExperimentCapture, McpPinnedResource<McpSaveStateResource>>(pinResult);
				}
				statePin = pinResult.Value!;
				if(CheckCancellation() || CheckDeadline()) {
					goto Cleanup;
				}
				McpServiceResult<McpOwnedSaveStateLoad> load = _automation.LoadOwnedSaveState(
					lease.LeaseId, prepared.Experiment.SaveStateId, statePin.Value, cancellationToken);
				if(!load.IsSuccess) {
					SetFailure(load.Error!.Code);
					goto Cleanup;
				}
				stateContext = load.Value!.Context;
				expectedIdentity = stateContext.Identity;
				if(CheckCancellation() || CheckDeadline()) {
					goto Cleanup;
				}
			}

			if(CheckCancellation() || CheckDeadline()) {
				goto Cleanup;
			}

			McpServiceResult<bool> enabled = ApplyInput(
				lease.LeaseId, expectedIdentity, stateContext, prepared.InitialStates);
			if(!enabled.IsSuccess) {
				SetFailure(enabled.Error!.Code);
				goto Cleanup;
			}
			if(CheckCancellation() || CheckDeadline()) {
				goto Cleanup;
			}

			McpStopResult initialStop = await _emulator.EnsureStoppedAsync(
				lease.LeaseId,
				Remaining(started, prepared.Experiment.TimeoutMs),
				quarantineOnFailure: false,
				expectedIdentity: expectedIdentity,
				cancellationToken: cancellationToken,
				expectedTicket: stateContext?.Ticket).ConfigureAwait(false);
			if(!initialStop.StopConfirmed || initialStop.Reason != McpStopReason.Pause) {
				SetStopFailure(initialStop);
				goto Cleanup;
			}
			McpServiceResult<bool> initialIdentity = VerifyExpectedIdentity(
				lease.LeaseId, expectedIdentity, stateContext);
			if(!initialIdentity.IsSuccess) {
				SetFailure(initialIdentity.Error!.Code);
				goto Cleanup;
			}
			if(CheckCancellation() || CheckDeadline()) {
				goto Cleanup;
			}

			McpServiceResult<CheckpointCapture> initial = CaptureCheckpoint(
				lease.LeaseId, expectedIdentity, stateContext, prepared, 0,
				completedFrames, observationSlots, assertionResults);
			if(!initial.IsSuccess) {
				SetFailure(initial.Error!.Code);
				goto Cleanup;
			}
			AddCheckpoint(initial.Value!.Result);
			if(CheckCancellation() || CheckDeadline()) {
				goto Cleanup;
			}
			if(ApplyAssertionOutcome(initial.Value)) {
				goto Cleanup;
			}

			foreach(McpValidatedSegment segment in prepared.Experiment.Segments) {
				if(CheckCancellation()) {
					break;
				}
				TimeSpan remaining = Remaining(started, prepared.Experiment.TimeoutMs);
				if(remaining <= TimeSpan.Zero) {
					SetFailure("timeout");
					break;
				}
				McpServiceResult<bool> input = ApplyInput(
					lease.LeaseId, expectedIdentity, stateContext, prepared.SegmentStates[segment.Index]);
				if(!input.IsSuccess) {
					SetFailure(input.Error!.Code);
					break;
				}
				if(CheckCancellation() || CheckDeadline()) {
					break;
				}
				remaining = Remaining(started, prepared.Experiment.TimeoutMs);
				if(remaining <= TimeSpan.Zero) {
					SetFailure("timeout");
					break;
				}

				if(CheckCancellation()) {
					break;
				}
				McpStopResult step = await _emulator.StepAndWaitAsync(
					lease.LeaseId,
					prepared.CpuType,
					segment.Frames,
					expectedIdentity,
					remaining,
					cancellationToken,
					stateContext?.Ticket).ConfigureAwait(false);
				if(step.Reason != McpStopReason.StepCompleted || step.CompletedFrames != segment.Frames) {
					SetStopFailure(step);
					break;
				}

				completedFrames += segment.Frames;
				completedSegments.Add(segment.Index);
				if(CheckCancellation()) {
					break;
				}
				McpServiceResult<bool> postStepIdentity = VerifyExpectedIdentity(
					lease.LeaseId, expectedIdentity, stateContext);
				if(!postStepIdentity.IsSuccess) {
					SetFailure(postStepIdentity.Error!.Code);
					break;
				}
				if(CheckCancellation() || CheckDeadline()) {
					break;
				}
				if(segment.CheckpointIndex.HasValue) {
					if(CheckCancellation() || CheckDeadline()) {
						break;
					}
					McpServiceResult<CheckpointCapture> checkpoint = CaptureCheckpoint(
						lease.LeaseId, expectedIdentity, stateContext, prepared, segment.CheckpointIndex.Value,
						completedFrames, observationSlots, assertionResults);
					if(!checkpoint.IsSuccess) {
						SetFailure(checkpoint.Error!.Code);
						break;
					}
					AddCheckpoint(checkpoint.Value!.Result);
					if(CheckCancellation() || CheckDeadline()) {
						break;
					}
					if(ApplyAssertionOutcome(checkpoint.Value)) {
						break;
					}
				}
			}

			if(!halted && completedSegments.Count == prepared.Experiment.Segments.Count) {
				if(CheckCancellation() || CheckDeadline()) {
					goto Cleanup;
				}
				int finalIndex = prepared.Experiment.Checkpoints.Count - 1;
				McpServiceResult<CheckpointCapture> final = CaptureCheckpoint(
					lease.LeaseId, expectedIdentity, stateContext, prepared, finalIndex,
					completedFrames, observationSlots, assertionResults);
				if(!final.IsSuccess) {
					SetFailure(final.Error!.Code);
					goto Cleanup;
				}
				AddCheckpoint(final.Value!.Result);
				if(CheckCancellation() || CheckDeadline()) {
					goto Cleanup;
				}
				if(ApplyAssertionOutcome(final.Value)) {
					goto Cleanup;
				}
				if(prepared.Experiment.CaptureFinalScreenshot) {
					if(CheckCancellation() || CheckDeadline()) {
						goto Cleanup;
					}
					McpServiceResult<McpScreenshotCapture> capture = CaptureScreenshot(
						lease.LeaseId, expectedIdentity, stateContext);
					if(!capture.IsSuccess) {
						SetFailure(capture.Error!.Code);
						goto Cleanup;
					}
					if(CheckCancellation() || CheckDeadline()) {
						goto Cleanup;
					}
					screenshot = capture.Value!.Metadata;
					screenshotPng = capture.Value.Png;
					_afterScreenshotAssigned();
				}
				if(CheckFinalInterruption()) {
					goto Cleanup;
				}
			}
		} catch(Exception) {
			SetFailure("native_failure");
		}

	Cleanup:
		McpServiceResult<bool> cleanupIdentity = VerifyExpectedIdentity(
			lease.LeaseId, expectedIdentity, stateContext);
		if(!cleanupIdentity.IsSuccess && !HasEstablishedSpecificReason()) {
			SetFailure(cleanupIdentity.Error!.Code);
		}
		TimeSpan cleanupBudget = Remaining(started, prepared.Experiment.TimeoutMs);
		if(cleanupBudget <= TimeSpan.Zero && !HasEstablishedSpecificReason()
			&& reason != McpExperimentReason.StateChanged) {
			SetFailure("timeout");
		}
		try {
			McpStopResult cleanupStop = await _emulator.EnsureStoppedAsync(
				lease.LeaseId, cleanupBudget, quarantineOnFailure: false).ConfigureAwait(false);
			stopConfirmed = cleanupStop.StopConfirmed;
		} catch(Exception) {
			stopConfirmed = false;
		}
		if(Remaining(started, prepared.Experiment.TimeoutMs) <= TimeSpan.Zero
			&& !HasEstablishedSpecificReason() && reason != McpExperimentReason.StateChanged) {
			SetFailure("timeout");
		}

		try {
			bool cleanupIdentityMatched = true;
			McpServiceResult<bool> release = _emulator.ExecuteOwned(lease.LeaseId, (api, identity) => {
				cleanupIdentityMatched = identity == expectedIdentity;
				api.ClearExclusiveControllerOverrides();
				cleanupIdentityMatched &= _emulator.EmulatorIdentity.Current == expectedIdentity;
				return McpServiceResult<bool>.Success(true);
			});
			inputReleased = release.IsSuccess;
			if(release.IsSuccess && !cleanupIdentityMatched && !HasEstablishedSpecificReason()) {
				SetFailure("state_changed");
			}
		} catch(Exception) {
			inputReleased = false;
		}
		if(Remaining(started, prepared.Experiment.TimeoutMs) <= TimeSpan.Zero
			&& !HasEstablishedSpecificReason() && reason != McpExperimentReason.StateChanged) {
			SetFailure("timeout");
		}
		if(!stopConfirmed || !inputReleased) {
			_emulator.ExecutionCoordinator.EnterQuarantineForOwner(lease.LeaseId);
			quarantined = true;
			status = McpExperimentStatus.Failed;
			reason = McpExperimentReason.CleanupFailed;
			interruption = new(McpExperimentReason.CleanupFailed, interruption?.BreakContext);
		}
		try {
			statePin?.Dispose();
		} catch(Exception) {
			status = McpExperimentStatus.Failed;
			reason = McpExperimentReason.CleanupFailed;
			interruption = new(McpExperimentReason.CleanupFailed, interruption?.BreakContext);
		}
		try {
			await lease.DisposeAsync().ConfigureAwait(false);
			leaseReleased = true;
		} catch(Exception) {
			leaseReleased = false;
			_emulator.ExecutionCoordinator.EnterQuarantineForOwner(lease.LeaseId);
			quarantined = true;
			status = McpExperimentStatus.Failed;
			reason = McpExperimentReason.CleanupFailed;
			interruption = new(McpExperimentReason.CleanupFailed, interruption?.BreakContext);
		}

		McpAssertionSummary summary = new(
			prepared.Experiment.Assertions.Count,
			assertionResults.Count(item => item.Passed),
			assertionResults.Count(item => !item.Passed),
			prepared.Experiment.Assertions.Count - assertionResults.Count);
		int[] skipped = prepared.Experiment.Segments.Select(item => item.Index)
			.Except(completedSegments).ToArray();
		RunExperimentResult result = new(
			status,
			reason,
			summary,
			checkpoints,
			completedSegments,
			skipped,
			completedFrames,
			stopConfirmed ? "paused" : "unknown",
			screenshot,
			interruption,
			new(stopConfirmed, inputReleased, leaseReleased, quarantined));
		return McpServiceResult<McpExperimentCapture>.Success(new(result, screenshot is null ? null : screenshotPng));

		void AddCheckpoint(McpCheckpointResult checkpoint)
		{
			checkpoints.Add(checkpoint);
			assertionResults.AddRange(checkpoint.Assertions);
		}

		bool ApplyAssertionOutcome(CheckpointCapture capture)
		{
			if(capture.Result.Assertions.Any(item => !item.Passed)) {
				status = McpExperimentStatus.AssertionFailed;
			}
			halted = capture.StopRequested;
			return capture.StopRequested;
		}

		void SetFailure(string errorCode)
		{
			halted = true;
			string mapped = MapFailureReason(errorCode);
			if(mapped is McpExperimentReason.Cancelled or McpExperimentReason.Timeout
				or McpExperimentReason.StateChanged or McpExperimentReason.Reset
				or McpExperimentReason.RomTransition) {
				screenshot = null;
			}
			status = mapped is McpExperimentReason.Reset or McpExperimentReason.RomTransition
				or McpExperimentReason.StateChanged or McpExperimentReason.Cancelled
				? McpExperimentStatus.Interrupted
				: McpExperimentStatus.Failed;
			reason = mapped;
			interruption = new(mapped, null);
		}

		void SetStopFailure(McpStopResult stop)
		{
			halted = true;
			if(stop.Reason is McpStopReason.Reset or McpStopReason.RomTransition
				or McpStopReason.Cancelled or McpStopReason.Timeout) {
				SetFailure(MapStopReason(stop.Reason));
				return;
			}
			McpServiceResult<bool> stopIdentity = VerifyExpectedIdentity(
				lease.LeaseId, expectedIdentity, stateContext);
			if(!stopIdentity.IsSuccess) {
				SetFailure(stopIdentity.Error!.Code);
				return;
			}
			string mapped = MapStopReason(stop.Reason);
			status = stop.Reason is McpStopReason.Breakpoint or McpStopReason.Reset
				or McpStopReason.RomTransition or McpStopReason.StateChanged
				? McpExperimentStatus.Interrupted
				: McpExperimentStatus.Failed;
			reason = mapped;
			BreakContext? context = null;
			if(stop.Reason == McpStopReason.Breakpoint && stop.BreakEvent.HasValue && stop.EventStateIdentity.HasValue) {
				McpServiceResult<BreakContext> captured = _emulator.GetOwnedBreakContext(
					lease.LeaseId, stop.BreakEvent.Value, stop.EventStateIdentity.Value, expectedIdentity);
				if(!captured.IsSuccess) {
					SetFailure("state_changed");
					return;
				}
				context = captured.Value;
			}
			interruption = new(mapped, context);
		}

		bool CheckDeadline()
		{
			if(Remaining(started, prepared.Experiment.TimeoutMs) > TimeSpan.Zero) {
				return false;
			}
			SetFailure("timeout");
			return true;
		}

		bool CheckCancellation()
		{
			if(!cancellationToken.IsCancellationRequested) {
				return false;
			}
			SetFailure("cancelled");
			return true;
		}

		bool CheckFinalInterruption()
		{
			if(CheckCancellation() || CheckDeadline()) {
				screenshot = null;
				return true;
			}
			McpServiceResult<bool> finalIdentity = VerifyExpectedIdentity(
				lease.LeaseId, expectedIdentity, stateContext);
			if(finalIdentity.IsSuccess) {
				return false;
			}
			SetFailure(finalIdentity.Error!.Code);
			screenshot = null;
			return true;
		}

		bool HasEstablishedSpecificReason() => reason is McpExperimentReason.Reset
			or McpExperimentReason.RomTransition
			or McpExperimentReason.Cancelled
			or McpExperimentReason.Timeout;
	}

	private McpServiceResult<PreparedExperiment> Prepare(RunExperimentRequest request)
	{
		return _emulator.ExecuteAutomationWithDebuggerLease((api, identity) => {
			if(!api.IsRunning()) {
				return McpServiceResult<PreparedExperiment>.Failure("no_game", "No game is currently loaded.");
			}
			RomInfo rom = api.GetRomInfo();
			IMcpAutomationAdapter? adapter = _adapters.GetAdapter(rom.ConsoleType);
			if(adapter is null) {
				return McpServiceResult<PreparedExperiment>.Failure("unsupported_system", "Automation is not supported for the current system.");
			}
			McpAutomationCapabilities capabilities = adapter.GetCapabilities(api, identity);
			if(!capabilities.DeterministicFrames) {
				return McpServiceResult<PreparedExperiment>.Failure("unsupported_capability", "Deterministic frame automation is unavailable.");
			}
			if(request.CaptureFinalScreenshot && !capabilities.Screenshots) {
				return McpServiceResult<PreparedExperiment>.Failure("unsupported_capability", "Screenshot capture is unavailable.");
			}

			IReadOnlyList<MemorySpace> spaces = Enum.GetValues<MemoryType>()
				.Where(type => type != MemoryType.None)
				.Select(type => (Type: type, Size: api.GetMemorySize(type)))
				.Where(item => item.Size > 0)
				.Select(item => new MemorySpace(item.Type.ToString(), item.Type.GetShortName(), item.Size, true, McpMemoryCapabilities.CanWrite(item.Type)))
				.ToArray();
			McpServiceResult<McpValidatedExperiment> validation = McpExperimentAssertions.Validate(
				request, new(capabilities.System, spaces));
			if(!validation.IsSuccess) {
				return FailureFrom<PreparedExperiment, McpValidatedExperiment>(validation);
			}
			McpValidatedExperiment experiment = validation.Value!;
			McpServiceResult<CpuType> cpu = adapter.ResolveFrameCpu(experiment.Cpu);
			if(!cpu.IsSuccess || !rom.CpuTypes.Contains(cpu.Value)) {
				return cpu.IsSuccess
					? McpServiceResult<PreparedExperiment>.Failure("unknown_cpu", "The selected CPU is not available.")
					: FailureFrom<PreparedExperiment, CpuType>(cpu);
			}

			if(experiment.SaveStateId is not null) {
				McpServiceResult<McpSaveStateResource> state = _saveStates.Inspect(experiment.SaveStateId);
				if(!state.IsSuccess) {
					return FailureFrom<PreparedExperiment, McpSaveStateResource>(state);
				}
				if(state.Value!.Identity.RomIdentity != identity.RomIdentity) {
					return McpServiceResult<PreparedExperiment>.Failure(
						"stale_resource", $"Resource '{experiment.SaveStateId}' is no longer compatible with the active ROM or memory topology.");
				}
			}

			HashSet<int> controlledPorts = experiment.Segments
				.SelectMany(segment => segment.Controllers).Select(controller => controller.Port).ToHashSet();
			List<IReadOnlyList<McpExclusiveControllerState>> segmentStates = [];
			foreach(McpValidatedSegment segment in experiment.Segments) {
				McpServiceResult<IReadOnlyList<McpExclusiveControllerState>> input = adapter.ValidateInput(segment.Controllers);
				if(!input.IsSuccess) {
					return FailureFrom<PreparedExperiment, IReadOnlyList<McpExclusiveControllerState>>(input);
				}
				segmentStates.Add(input.Value!.Where(state => controlledPorts.Contains(state.Port)).ToArray());
			}
			McpServiceResult<IReadOnlyList<McpExclusiveControllerState>> neutral = adapter.ValidateInput([]);
			if(!neutral.IsSuccess) {
				return FailureFrom<PreparedExperiment, IReadOnlyList<McpExclusiveControllerState>>(neutral);
			}
			return McpServiceResult<PreparedExperiment>.Success(new(
				experiment,
				cpu.Value!,
				identity,
				controlledPorts,
				neutral.Value!.Where(state => controlledPorts.Contains(state.Port)).ToArray(),
				segmentStates));
		});
	}

	private McpServiceResult<bool> ApplyInput(
		long leaseId,
		McpStateIdentity expectedIdentity,
		McpAutomationStateContext? stateContext,
		IReadOnlyList<McpExclusiveControllerState> states)
	{
		return ExecuteOwned(leaseId, stateContext, (api, identity) => {
			if(identity != expectedIdentity) {
				return StateChanged<bool>();
			}
			foreach(McpExclusiveControllerState state in states) {
				if(!api.SetExclusiveControllerOverride(state)) {
					return McpServiceResult<bool>.Failure("native_failure", "Native exclusive input failed.");
				}
				if(_emulator.EmulatorIdentity.Current != expectedIdentity) {
					return StateChanged<bool>();
				}
			}
			return McpServiceResult<bool>.Success(true);
		});
	}

	private McpServiceResult<bool> VerifyExpectedIdentity(
		long leaseId,
		McpStateIdentity expectedIdentity,
		McpAutomationStateContext? stateContext) =>
		ExecuteOwned(leaseId, stateContext, (_, identity) => identity == expectedIdentity
			? McpServiceResult<bool>.Success(true)
			: StateChanged<bool>());

	private McpServiceResult<CheckpointCapture> CaptureCheckpoint(
		long leaseId,
		McpStateIdentity expectedIdentity,
		McpAutomationStateContext? stateContext,
		PreparedExperiment prepared,
		int checkpointIndex,
		int completedFrames,
		IList<McpObservationResult?> observationSlots,
		IReadOnlyList<McpAssertionResult> previousAssertions)
	{
		return ExecuteOwned(leaseId, stateContext, (api, identity) => {
			if(identity != expectedIdentity || !api.IsExecutionStopped()) {
				return McpServiceResult<CheckpointCapture>.Failure("state_changed", "Checkpoint capture requires stopped execution.");
			}
			if(_emulator.EmulatorIdentity.Current != expectedIdentity) {
				return StateChanged<CheckpointCapture>();
			}
			McpValidatedCheckpoint checkpoint = prepared.Experiment.Checkpoints[checkpointIndex];
			List<McpObservationResult> observations = [];
			foreach(int observationIndex in checkpoint.ObservationIndices) {
				McpValidatedObservation observation = prepared.Experiment.Observations[observationIndex];
				MemoryType type = Enum.Parse<MemoryType>(observation.Space, ignoreCase: false);
				byte[] data = api.GetMemoryValues(type, observation.Address, observation.Address + (uint)observation.Count - 1);
				if(_emulator.EmulatorIdentity.Current != expectedIdentity) {
					return StateChanged<CheckpointCapture>();
				}
				if(data.Length != observation.Count) {
					return McpServiceResult<CheckpointCapture>.Failure("native_failure", "Native memory read returned an unexpected byte count.");
				}
				McpObservationResult result = McpExperimentAssertions.CreateObservationResult(observation, data);
				observationSlots[observationIndex] = result;
				observations.Add(result);
			}
			McpAssertionEvaluation evaluation = McpExperimentAssertions.EvaluateCheckpoint(
				prepared.Experiment, checkpointIndex, (IReadOnlyList<McpObservationResult?>)observationSlots, previousAssertions);
			if(_emulator.EmulatorIdentity.Current != expectedIdentity) {
				return StateChanged<CheckpointCapture>();
			}
			return McpServiceResult<CheckpointCapture>.Success(new(
				new(checkpoint.Name, completedFrames, observations, evaluation.Results),
				evaluation.StopRequested));
		});
	}

	private McpServiceResult<McpScreenshotCapture> CaptureScreenshot(
		long leaseId,
		McpStateIdentity expectedIdentity,
		McpAutomationStateContext? stateContext)
	{
		return ExecuteOwned(leaseId, stateContext, (api, identity) => {
			if(identity != expectedIdentity || !api.IsExecutionStopped()) {
				return McpServiceResult<McpScreenshotCapture>.Failure("state_changed", "Screenshot capture requires stopped execution.");
			}
			if(_emulator.EmulatorIdentity.Current != expectedIdentity) {
				return StateChanged<McpScreenshotCapture>();
			}
			McpServiceResult<McpScreenshotCapture> capture = api.CaptureScreenshot();
			if(!capture.IsSuccess) {
				return capture;
			}
			McpScreenshotCapture value = capture.Value!;
			if(_emulator.EmulatorIdentity.Current != expectedIdentity) {
				return StateChanged<McpScreenshotCapture>();
			}
			McpScreenshotMetadata metadata = value.Metadata;
			if(metadata.Width <= 0 || metadata.Height <= 0 || value.Png.Length == 0 || metadata.PngBytes != value.Png.Length) {
				return McpServiceResult<McpScreenshotCapture>.Failure("native_failure", "Native screenshot data is invalid.");
			}
			if(metadata.Width > McpAutomationLimits.MaxScreenshotDimension
				|| metadata.Height > McpAutomationLimits.MaxScreenshotDimension
				|| (long)metadata.Width * metadata.Height > McpAutomationLimits.MaxScreenshotPixels
				|| value.Png.Length > McpAutomationLimits.MaxPngBytes) {
				return McpServiceResult<McpScreenshotCapture>.Failure("native_failure", "Native screenshot data exceeds managed limits.");
			}
			return McpServiceResult<McpScreenshotCapture>.Success(value with {
				Metadata = metadata with {
					RomIdentity = identity.RomIdentity,
					MutableStateGeneration = identity.MutableStateGeneration
				}
			});
		});
	}

	private McpServiceResult<T> ExecuteOwned<T>(
		long leaseId,
		McpAutomationStateContext? stateContext,
		Func<IMcpEmulatorApi, McpStateIdentity, McpServiceResult<T>> operation)
	{
		return stateContext is null
			? _emulator.ExecuteOwned(leaseId, operation)
			: _emulator.ExecuteOwnedForTicket(leaseId, stateContext.Ticket, operation);
	}

	private TimeSpan Remaining(long started, int timeoutMs)
	{
		TimeSpan remaining = TimeSpan.FromMilliseconds(timeoutMs)
			- _clock.GetElapsedTime(started, _clock.GetTimestamp());
		return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
	}

	private static string MapFailureReason(string code) => code switch {
		"cancelled" => McpExperimentReason.Cancelled,
		"timeout" => McpExperimentReason.Timeout,
		"reset" => McpExperimentReason.Reset,
		"rom_transition" => McpExperimentReason.RomTransition,
		"state_changed" => McpExperimentReason.StateChanged,
		"server_stopping" => McpExperimentReason.RomTransition,
		_ => McpExperimentReason.NativeFailure
	};

	private static string MapStopReason(McpStopReason reason) => reason switch {
		McpStopReason.Breakpoint => McpExperimentReason.Breakpoint,
		McpStopReason.Timeout => McpExperimentReason.Timeout,
		McpStopReason.Cancelled => McpExperimentReason.Cancelled,
		McpStopReason.Reset => McpExperimentReason.Reset,
		McpStopReason.RomTransition or McpStopReason.ServerStopping => McpExperimentReason.RomTransition,
		McpStopReason.StateChanged => McpExperimentReason.StateChanged,
		_ => McpExperimentReason.NativeFailure
	};

	private static McpServiceResult<T> FailureFrom<T, TSource>(McpServiceResult<TSource> source) =>
		McpServiceResult<T>.Failure(source.Error!.Code, source.Error.Message);

	private static McpServiceResult<T> StateChanged<T>() =>
		McpServiceResult<T>.Failure("state_changed", "Emulator state changed during the experiment.");

	private sealed record PreparedExperiment(
		McpValidatedExperiment Experiment,
		CpuType CpuType,
		McpStateIdentity PreflightIdentity,
		IReadOnlySet<int> ControlledPorts,
		IReadOnlyList<McpExclusiveControllerState> InitialStates,
		IReadOnlyList<IReadOnlyList<McpExclusiveControllerState>> SegmentStates);

	private sealed record CheckpointCapture(McpCheckpointResult Result, bool StopRequested);
}
