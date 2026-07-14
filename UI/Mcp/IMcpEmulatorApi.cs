using Mesen.Interop;

namespace Mesen.Mcp;

internal interface IMcpEmulatorApi
{
	bool IsRunning();
	ulong GetDebuggerRequestBlockState();
	bool IsPaused();
	RomInfo GetRomInfo();
	int GetMemorySize(MemoryType type);
	byte[] GetMemoryValues(MemoryType type, uint start, uint endInclusive);
	void SetMemoryValues(MemoryType type, uint start, byte[] data);
	NesCpuState GetNesCpuState();
}
