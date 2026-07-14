using Mesen.Debugger;
using Mesen.Interop;
using System.Collections.Generic;

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
	void Pause();
	void Resume();
	void ResumeDebugger();
	bool IsExecutionStopped();
	void Step(CpuType cpuType, int instructionCount, StepType type);
	DebuggerFeatures GetDebuggerFeatures(CpuType cpuType);
	long EvaluateExpression(string expression, CpuType cpuType, out EvalResultType resultType, bool useCache);
	uint GetProgramCounter(CpuType cpuType, bool instructionProgramCounter);
	CodeLineData[] GetDisassemblyOutput(CpuType cpuType, uint address, uint rowCount);
	int GetDisassemblyRowAddress(CpuType cpuType, uint address, int rowOffset);
	AddressInfo GetAbsoluteAddress(AddressInfo relativeAddress);
	AddressInfo GetRelativeAddress(AddressInfo absoluteAddress, CpuType cpuType);
	StackFrameInfo[] GetCallstack(CpuType cpuType);
	void SetTraceOptions(CpuType cpuType, InteropTraceLoggerOptions options);
	void ClearExecutionTrace();
	uint GetExecutionTraceSize();
	TraceRow[] GetExecutionTrace(uint startOffset, uint maxRowCount);
	McpServiceResult<byte[]> CreateSaveState();
	McpServiceResult<bool> LoadSaveState(byte[] data);
	McpServiceResult<McpScreenshotCapture> CaptureScreenshot();
	IReadOnlyList<McpControllerTopology> GetControllerTopology();
	bool SetExclusiveControllerOverride(McpExclusiveControllerState state);
	void ClearExclusiveControllerOverrides();
}
