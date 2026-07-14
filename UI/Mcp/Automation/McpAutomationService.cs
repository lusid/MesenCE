using System;
using System.Threading;
using System.Threading.Tasks;

namespace Mesen.Mcp;

internal sealed class McpAutomationService
{
	private readonly McpEmulatorService _emulator;
	private readonly McpAutomationAdapterRegistry _adapters;
	private readonly McpSaveStateStore _saveStates;

	internal McpAutomationService(
		McpEmulatorService emulator,
		McpAutomationAdapterRegistry adapters,
		McpSaveStateStore saveStates)
	{
		_emulator = emulator;
		_adapters = adapters;
		_saveStates = saveStates;
	}

	internal McpServiceResult<McpAutomationCapabilities> GetCapabilities()
	{
		return _emulator.ExecuteAutomationWithDebuggerLease((api, identity) => {
			if(!api.IsRunning()) {
				return NoGame<McpAutomationCapabilities>();
			}
			IMcpAutomationAdapter? adapter = _adapters.GetAdapter(api.GetRomInfo().ConsoleType);
			return adapter is null
				? McpServiceResult<McpAutomationCapabilities>.Failure(
					"unsupported_system", "Automation is not supported for the current system.")
				: McpServiceResult<McpAutomationCapabilities>.Success(adapter.GetCapabilities(api, identity));
		});
	}

	internal McpServiceResult<McpSaveStateMetadata> CreateSaveState()
	{
		return _emulator.ExecuteAutomation((api, identity) => {
			if(!api.IsRunning()) {
				return NoGame<McpSaveStateMetadata>();
			}
			McpServiceResult<byte[]> state = api.CreateSaveState();
			if(!state.IsSuccess) {
				return ForwardFailure<McpSaveStateMetadata, byte[]>(state);
			}
			return _saveStates.Create(state.Value!, identity, DateTimeOffset.UtcNow);
		});
	}

	internal async Task<McpServiceResult<McpSaveStateLoadResult>> LoadSaveStateAsync(
		string id,
		CancellationToken cancellationToken)
	{
		McpServiceResult<McpExecutionLease> acquisition = await _emulator.ExecutionCoordinator
			.TryAcquireAsync(cancellationToken)
			.ConfigureAwait(false);
		if(!acquisition.IsSuccess) {
			return ForwardFailure<McpSaveStateLoadResult, McpExecutionLease>(acquisition);
		}
		await using McpExecutionLease executionLease = acquisition.Value!;

		McpServiceResult<McpPinnedResource<McpSaveStateResource>> pinResult =
			_emulator.ExecuteAutomation((_, _) => _saveStates.Pin(id));
		if(!pinResult.IsSuccess) {
			return ForwardFailure<McpSaveStateLoadResult, McpPinnedResource<McpSaveStateResource>>(pinResult);
		}
		using McpPinnedResource<McpSaveStateResource> pin = pinResult.Value!;
		McpServiceResult<McpOwnedSaveStateLoad> load = LoadOwnedSaveState(
			executionLease.LeaseId, id, pin.Value, cancellationToken);
		return load.IsSuccess
			? McpServiceResult<McpSaveStateLoadResult>.Success(load.Value!.Result)
			: ForwardFailure<McpSaveStateLoadResult, McpOwnedSaveStateLoad>(load);
	}

	internal McpServiceResult<McpOwnedSaveStateLoad> LoadOwnedSaveState(
		long leaseId,
		string id,
		McpSaveStateResource state,
		CancellationToken cancellationToken)
	{
		McpServiceResult<McpStateLoadPostflight<OwnedLoadIdentity>> load =
			_emulator.ExecuteOwnedStateLoad(leaseId, (api, identity) => {
			if(!api.IsRunning()) {
				return NoGame<OwnedLoadIdentity>();
			}
			if(state.Identity.RomIdentity != identity.RomIdentity) {
				return McpServiceResult<OwnedLoadIdentity>.Failure(
					"stale_resource", $"Resource '{id}' is no longer compatible with the active ROM or memory topology.");
			}
			if(cancellationToken.IsCancellationRequested) {
				return McpServiceResult<OwnedLoadIdentity>.Failure("cancelled", "The operation was cancelled.");
			}

			McpServiceResult<bool> nativeLoad = api.LoadSaveState(state.Data);
			if(!nativeLoad.IsSuccess) {
				return ForwardFailure<OwnedLoadIdentity, bool>(nativeLoad);
			}
			McpStateIdentity current = _emulator.EmulatorIdentity.Current;
			if(current.RomIdentity != identity.RomIdentity ||
				current.MutableStateGeneration != identity.MutableStateGeneration + 1 ||
				current.StateLoadedSequence != identity.StateLoadedSequence + 1) {
				return McpServiceResult<OwnedLoadIdentity>.Failure(
					"state_changed", "Emulator state changed unexpectedly during the operation.");
			}
			return McpServiceResult<OwnedLoadIdentity>.Success(new(
				identity, current, api.IsPaused() ? "paused" : "running"));
		});
		if(!load.IsSuccess) {
			return ForwardFailure<McpOwnedSaveStateLoad, McpStateLoadPostflight<OwnedLoadIdentity>>(load);
		}
		McpStateLoadPostflight<OwnedLoadIdentity> postflight = load.Value!;
		OwnedLoadIdentity value = postflight.Value;
		return McpServiceResult<McpOwnedSaveStateLoad>.Success(new(
			new(id, value.Current.RomIdentity, value.Previous.MutableStateGeneration,
				value.Current.MutableStateGeneration, value.State),
			new(value.Current, postflight.Ticket)));
	}

	internal McpServiceResult<McpDeleteResourceResult> DeleteSaveState(string id) =>
		_emulator.ExecuteAutomation((_, _) => _saveStates.Delete(id));

	internal McpServiceResult<McpScreenshotCapture> CaptureScreenshot()
	{
		return _emulator.ExecuteAutomation((api, identity) => {
			if(!api.IsRunning()) {
				return NoGame<McpScreenshotCapture>();
			}
			McpServiceResult<McpScreenshotCapture> capture = api.CaptureScreenshot();
			if(!capture.IsSuccess) {
				return capture;
			}
			McpScreenshotCapture value = capture.Value!;
			McpScreenshotMetadata metadata = value.Metadata;
			if(metadata.Width > McpAutomationLimits.MaxScreenshotDimension ||
				metadata.Height > McpAutomationLimits.MaxScreenshotDimension ||
				(long)metadata.Width * metadata.Height > McpAutomationLimits.MaxScreenshotPixels ||
				value.Png.Length > McpAutomationLimits.MaxPngBytes) {
				return McpServiceResult<McpScreenshotCapture>.Failure(
					"payload_too_large", "The screenshot exceeds the managed size limit.");
			}
			if(metadata.Width <= 0 || metadata.Height <= 0 || value.Png.Length == 0 || metadata.PngBytes != value.Png.Length) {
				return McpServiceResult<McpScreenshotCapture>.Failure("interop_failure", "Native emulator interop failed.");
			}
			return McpServiceResult<McpScreenshotCapture>.Success(value with {
				Metadata = metadata with {
					RomIdentity = identity.RomIdentity,
					MutableStateGeneration = identity.MutableStateGeneration
				}
			});
		});
	}

	internal void InvalidateRomResources() => _saveStates.InvalidateRom(_emulator.EmulatorIdentity.Current.RomIdentity);

	private static McpServiceResult<T> NoGame<T>() =>
		McpServiceResult<T>.Failure("no_game", "No game is currently loaded.");

	private static McpServiceResult<T> ForwardFailure<T, TSource>(McpServiceResult<TSource> source) =>
		McpServiceResult<T>.Failure(source.Error!.Code, source.Error.Message);

	private sealed record OwnedLoadIdentity(
		McpStateIdentity Previous,
		McpStateIdentity Current,
		string State);
}
