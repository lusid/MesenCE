using Mesen.Debugger;
using Mesen.Interop;
using System;

namespace Mesen.Mcp;

internal sealed class MesenMcpEmulatorApi : IMcpEmulatorApi
{
	private readonly Func<InteropBufferResult> _createSaveState;
	private readonly Func<byte[], InteropApiResult> _loadSaveState;

	public MesenMcpEmulatorApi() : this(EmuApi.CreateSaveState, EmuApi.LoadSaveState) {}

	internal MesenMcpEmulatorApi(
		Func<InteropBufferResult> createSaveState,
		Func<byte[], InteropApiResult> loadSaveState)
	{
		_createSaveState = createSaveState;
		_loadSaveState = loadSaveState;
	}

	public bool IsRunning() => EmuApi.IsRunning();
	public ulong GetDebuggerRequestBlockState() => DebugApi.GetDebuggerRequestBlockState();
	public bool IsPaused() => EmuApi.IsPaused();
	public RomInfo GetRomInfo() => EmuApi.GetRomInfo();
	public int GetMemorySize(MemoryType type) => DebugApi.GetMemorySize(type);
	public byte[] GetMemoryValues(MemoryType type, uint start, uint endInclusive) => DebugApi.GetMemoryValues(type, start, endInclusive);
	public void SetMemoryValues(MemoryType type, uint start, byte[] data) => DebugApi.SetMemoryValues(type, start, data, data.Length);
	public NesCpuState GetNesCpuState() => DebugApi.GetCpuState<NesCpuState>(CpuType.Nes);
	public void Pause() => EmuApi.Pause();
	public void Resume() => EmuApi.Resume();
	public void ResumeDebugger() => DebugApi.ResumeExecution();
	public bool IsExecutionStopped() => DebugApi.IsExecutionStopped();
	public void Step(CpuType cpuType, int instructionCount, StepType type) => DebugApi.Step(cpuType, instructionCount, type);
	public DebuggerFeatures GetDebuggerFeatures(CpuType cpuType) => DebugApi.GetDebuggerFeatures(cpuType);
	public long EvaluateExpression(string expression, CpuType cpuType, out EvalResultType resultType, bool useCache) => DebugApi.EvaluateExpression(expression, cpuType, out resultType, useCache);
	public uint GetProgramCounter(CpuType cpuType, bool instructionProgramCounter) => DebugApi.GetProgramCounter(cpuType, instructionProgramCounter);
	public CodeLineData[] GetDisassemblyOutput(CpuType cpuType, uint address, uint rowCount) => DebugApi.GetDisassemblyOutput(cpuType, address, rowCount);
	public int GetDisassemblyRowAddress(CpuType cpuType, uint address, int rowOffset) => DebugApi.GetDisassemblyRowAddress(cpuType, address, rowOffset);
	public AddressInfo GetAbsoluteAddress(AddressInfo relativeAddress) => DebugApi.GetAbsoluteAddress(relativeAddress);
	public AddressInfo GetRelativeAddress(AddressInfo absoluteAddress, CpuType cpuType) => DebugApi.GetRelativeAddress(absoluteAddress, cpuType);
	public StackFrameInfo[] GetCallstack(CpuType cpuType) => DebugApi.GetCallstack(cpuType);
	public void SetTraceOptions(CpuType cpuType, InteropTraceLoggerOptions options) => DebugApi.SetTraceOptions(cpuType, options);
	public void ClearExecutionTrace() => DebugApi.ClearExecutionTrace();
	public uint GetExecutionTraceSize() => DebugApi.GetExecutionTraceSize();
	public TraceRow[] GetExecutionTrace(uint startOffset, uint maxRowCount) => DebugApi.GetExecutionTrace(startOffset, maxRowCount);

	public McpServiceResult<byte[]> CreateSaveState()
	{
		InteropBufferResult result = _createSaveState();
		return result.Result switch {
			InteropApiResult.Success => McpServiceResult<byte[]>.Success(result.Data),
			InteropApiResult.NoGameLoaded => McpServiceResult<byte[]>.Failure("no_game", "No game is currently loaded."),
			InteropApiResult.PayloadTooLarge => McpServiceResult<byte[]>.Failure("payload_too_large", "The save state exceeds the native size limit."),
			_ => McpServiceResult<byte[]>.Failure("interop_failure", "Native emulator interop failed.")
		};
	}

	public McpServiceResult<bool> LoadSaveState(byte[] data)
	{
		return _loadSaveState(data) switch {
			InteropApiResult.Success => McpServiceResult<bool>.Success(true),
			InteropApiResult.NoGameLoaded => McpServiceResult<bool>.Failure("no_game", "No game is currently loaded."),
			InteropApiResult.PayloadTooLarge => McpServiceResult<bool>.Failure("payload_too_large", "The save state exceeds the native size limit."),
			_ => McpServiceResult<bool>.Failure("interop_failure", "Native emulator interop failed.")
		};
	}
}
