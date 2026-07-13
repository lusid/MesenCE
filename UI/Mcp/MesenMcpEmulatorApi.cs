using Mesen.Interop;

namespace Mesen.Mcp;

internal sealed class MesenMcpEmulatorApi : IMcpEmulatorApi
{
	public bool IsRunning() => EmuApi.IsRunning();
	public bool IsPaused() => EmuApi.IsPaused();
	public RomInfo GetRomInfo() => EmuApi.GetRomInfo();
	public int GetMemorySize(MemoryType type) => DebugApi.GetMemorySize(type);
	public byte[] GetMemoryValues(MemoryType type, uint start, uint endInclusive) => DebugApi.GetMemoryValues(type, start, endInclusive);
	public void SetMemoryValues(MemoryType type, uint start, byte[] data) => DebugApi.SetMemoryValues(type, start, data, data.Length);
	public NesCpuState GetNesCpuState() => DebugApi.GetCpuState<NesCpuState>(CpuType.Nes);
}
