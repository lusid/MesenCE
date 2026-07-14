using Mesen.Config;
using Mesen.Debugger;
using Mesen.Interop;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Mesen.Mcp;

internal sealed class NesMcpAutomationAdapter(IMcpEmulatorApi api) : IMcpAutomationAdapter
{
	private static readonly HashSet<ControllerType> SupportedDevices = [
		ControllerType.NesController,
		ControllerType.FamicomController,
		ControllerType.FamicomControllerP2
	];

	public StepType FrameStepType => StepType.PpuFrame;
	public string FrameSemantics => "next_frame_boundary";

	public McpAutomationCapabilities GetCapabilities(IMcpEmulatorApi api, McpStateIdentity identity)
	{
		IReadOnlyList<McpControllerTopology> topology = api.GetControllerTopology();
		bool deterministic = topology.Count > 0 && topology.All(IsSupported);
		List<string> limitations = deterministic
			? []
			: topology.Where(device => !IsSupported(device))
				.Select(device => $"Configured device {device.DeviceType} on port {device.Index} is not supported for deterministic NES automation.")
				.ToList();
		if(topology.Count == 0) {
			limitations.Add("No configured NES controller device is available for deterministic automation.");
		}
		List<McpControllerCapability> controllers = topology.Select(device => new McpControllerCapability(
			device.Index,
			device.DeviceType.ToString(),
			IsSupported(device),
			device.Controls.Select(control => new McpControllerControlCapability(
				control.Id,
				control.NativeId,
				control.IsNumeric ? "number" : "button")).ToArray())).ToList();

		return new(
			"Nes",
			identity.RomIdentity,
			identity.MutableStateGeneration,
			true,
			true,
			deterministic,
			deterministic ? FrameSemantics : null,
			controllers,
			CreateLimits(),
			limitations);
	}

	public McpServiceResult<CpuType> ResolveFrameCpu(string cpu)
	{
		return cpu == "Nes"
			? McpServiceResult<CpuType>.Success(CpuType.Nes)
			: McpServiceResult<CpuType>.Failure("invalid_request", "CPU must be exactly 'Nes' for NES frame automation.");
	}

	public McpServiceResult<IReadOnlyList<McpExclusiveControllerState>> ValidateInput(IReadOnlyList<McpControllerInput> controllers)
	{
		IReadOnlyList<McpControllerTopology> topology = api.GetControllerTopology();
		if(topology.Count == 0 || topology.Any(device => !IsSupported(device))) {
			return McpServiceResult<IReadOnlyList<McpExclusiveControllerState>>.Failure(
				"unsupported_capability",
				"Every configured NES input device must support complete exclusive input.");
		}

		Dictionary<int, McpControllerTopology> devices = topology.ToDictionary(device => device.Index);
		Dictionary<int, IReadOnlyList<string>> requested = new();
		foreach(McpControllerInput controller in controllers) {
			if(!devices.ContainsKey(controller.Port)) {
				return InvalidInput($"Controller port {controller.Port} is not configured.");
			}
			if(!requested.TryAdd(controller.Port, controller.Buttons)) {
				return InvalidInput($"Controller port {controller.Port} is specified more than once.");
			}
		}

		List<McpExclusiveControllerState> states = new(topology.Count);
		foreach(McpControllerTopology device in topology) {
			IReadOnlyList<string> buttons = requested.GetValueOrDefault(device.Index, []);
			Dictionary<string, McpControllerControl> controls = device.Controls
				.Where(control => !control.IsNumeric)
				.ToDictionary(control => control.Id, StringComparer.Ordinal);
			HashSet<string> seen = new(StringComparer.Ordinal);
			List<McpControllerValue> values = new(buttons.Count);
			foreach(string button in buttons) {
				if(!seen.Add(button)) {
					return InvalidInput($"Button '{button}' is specified more than once on port {device.Index}.");
				}
				if(!controls.TryGetValue(button, out McpControllerControl? control)) {
					return InvalidInput($"Button '{button}' is not supported on port {device.Index}.");
				}
				values.Add(new(control.NativeId, 1));
			}
			states.Add(new(device.Index, values));
		}

		return McpServiceResult<IReadOnlyList<McpExclusiveControllerState>>.Success(states);
	}

	private static bool IsSupported(McpControllerTopology device)
	{
		return SupportedDevices.Contains(device.DeviceType)
			&& device.Controls.Count > 0
			&& device.Controls.All(control => !control.IsNumeric && control.NativeId is >= 0 and <= byte.MaxValue)
			&& device.Controls.Select(control => control.Id).Distinct(StringComparer.Ordinal).Count() == device.Controls.Count
			&& device.Controls.Select(control => control.NativeId).Distinct().Count() == device.Controls.Count;
	}

	private static McpServiceResult<IReadOnlyList<McpExclusiveControllerState>> InvalidInput(string message)
	{
		return McpServiceResult<IReadOnlyList<McpExclusiveControllerState>>.Failure("invalid_request", message);
	}

	private static McpAutomationResourceLimits CreateLimits() => new(
		McpAutomationLimits.MaxSaveStates,
		McpAutomationLimits.MaxSaveStateBytes,
		McpAutomationLimits.MaxAggregateSaveStateBytes,
		McpAutomationLimits.MaxMemorySnapshots,
		McpAutomationLimits.MaxMemorySnapshotBytes,
		McpAutomationLimits.MaxAggregateMemorySnapshotBytes,
		McpAutomationLimits.MaxMemorySearches,
		McpAutomationLimits.MaxSearchRangeBytes,
		McpAutomationLimits.MaxSearchAllocationBytes,
		McpAutomationLimits.MaxAggregateSearchAllocationBytes,
		McpAutomationLimits.MaxSegments,
		McpAutomationLimits.MaxExperimentFrames,
		McpAutomationLimits.MaxObservations,
		McpAutomationLimits.MaxAssertions,
		McpAutomationLimits.MaxObservedBytes,
		McpAutomationLimits.MaxPngBytes,
		McpAutomationLimits.MaxScreenshotDimension,
		McpAutomationLimits.MaxScreenshotPixels,
		McpAutomationLimits.MaxResultPage,
		McpAutomationLimits.MaxRunSampleBytes,
		McpAutomationLimits.MinExperimentTimeoutMs,
		McpAutomationLimits.MaxExperimentTimeoutMs,
		(int)McpAutomationLimits.ResourceIdleExpiration.TotalMinutes);
}
