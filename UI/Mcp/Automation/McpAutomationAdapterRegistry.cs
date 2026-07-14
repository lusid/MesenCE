using Mesen.Interop;

namespace Mesen.Mcp;

internal sealed class McpAutomationAdapterRegistry(IMcpEmulatorApi api)
{
	internal IMcpAutomationAdapter? GetAdapter(ConsoleType consoleType)
	{
		return consoleType == ConsoleType.Nes ? new NesMcpAutomationAdapter(api) : null;
	}
}
