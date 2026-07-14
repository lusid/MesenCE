using Mesen.Debugger;
using Mesen.Interop;
using System.Collections.Generic;

namespace Mesen.Mcp;

internal interface IMcpAutomationAdapter
{
	McpAutomationCapabilities GetCapabilities(IMcpEmulatorApi api, McpStateIdentity identity);
	McpServiceResult<CpuType> ResolveFrameCpu(string cpu);
	McpServiceResult<IReadOnlyList<McpExclusiveControllerState>> ValidateInput(IReadOnlyList<McpControllerInput> controllers);
	StepType FrameStepType { get; }
	string FrameSemantics { get; }
}
