using Mesen.Debugger;
using Mesen.Interop;
using Mesen.Mcp;

namespace UI.Tests.Mcp;

internal sealed class FakeMcpEmulatorApi : IMcpEmulatorApi
{
	internal delegate long EvaluateExpressionDelegate(string expression, CpuType cpuType, out EvalResultType resultType, bool useCache);
	private readonly object _debuggerRequestBlockStateLock = new();
	private ulong _debuggerRequestBlockState;
	private int _debuggerRequestBlockCount;
	private int _debuggerRequestBlockStateCalls;

	public bool Running { get; init; }
	public bool Paused { get; init; }
	public bool ExecutionStopped { get; init; } = true;
	public RomInfo RomInfo { get; init; } = new() { ConsoleType = ConsoleType.Nes, RomPath = "game.nes" };
	public Dictionary<MemoryType, int> MemorySizes { get; } = [];
	public int DefaultMemorySize { get; init; }
	public byte[] ReadData { get; set; } = [0];
	public NesCpuState NesCpuState { get; init; }
	public uint ProgramCounter { get; init; } = 0x8000;
	public CodeLineData[] DisassemblyOutput { get; init; } = [new(CpuType.Nes) { Address = 0x8000 }];
	public StackFrameInfo[] Callstack { get; init; } = [new()];
	public TraceRow[] ExecutionTrace { get; init; } = [];
	public int ThrowOnDebuggerRequestBlockStateCall { get; set; }

	public Func<bool>? IsRunningHandler { get; set; }
	public Func<ulong>? GetDebuggerRequestBlockStateHandler { get; set; }
	public Func<bool>? IsPausedHandler { get; set; }
	public Func<RomInfo>? GetRomInfoHandler { get; set; }
	public Func<MemoryType, int>? GetMemorySizeHandler { get; set; }
	public Func<MemoryType, uint, uint, byte[]>? GetMemoryValuesHandler { get; set; }
	public Action<MemoryType, uint, byte[]>? SetMemoryValuesHandler { get; set; }
	public Func<NesCpuState>? GetNesCpuStateHandler { get; set; }
	public Action? PauseHandler { get; set; }
	public Action? ResumeHandler { get; set; }
	public Action? ResumeDebuggerHandler { get; set; }
	public Func<bool>? IsExecutionStoppedHandler { get; set; }
	public Action<CpuType, int, StepType>? StepHandler { get; set; }
	public Func<CpuType, DebuggerFeatures>? GetDebuggerFeaturesHandler { get; set; }
	public EvaluateExpressionDelegate? EvaluateExpressionHandler { get; set; }
	public Func<CpuType, bool, uint>? GetProgramCounterHandler { get; set; }
	public Func<CpuType, uint, uint, CodeLineData[]>? GetDisassemblyOutputHandler { get; set; }
	public Func<CpuType, uint, int, int>? GetDisassemblyRowAddressHandler { get; set; }
	public Func<AddressInfo, AddressInfo>? GetAbsoluteAddressHandler { get; set; }
	public Func<AddressInfo, CpuType, AddressInfo>? GetRelativeAddressHandler { get; set; }
	public Func<CpuType, StackFrameInfo[]>? GetCallstackHandler { get; set; }
	public Action<CpuType, InteropTraceLoggerOptions>? SetTraceOptionsHandler { get; set; }
	public Action? ClearExecutionTraceHandler { get; set; }
	public Func<uint>? GetExecutionTraceSizeHandler { get; set; }
	public Func<uint, uint, TraceRow[]>? GetExecutionTraceHandler { get; set; }
	public Func<McpServiceResult<byte[]>>? CreateSaveStateHandler { get; set; }
	public Func<byte[], McpServiceResult<bool>>? LoadSaveStateHandler { get; set; }
	public Func<McpServiceResult<McpScreenshotCapture>>? CaptureScreenshotHandler { get; set; }

	public Action? OnRead { get; set; }
	public int IsRunningCalls { get; private set; }
	public int DebuggerRequestBlockStateCalls => _debuggerRequestBlockStateCalls;
	public int IsPausedCalls { get; private set; }
	public int GetRomInfoCalls { get; private set; }
	public int GetMemorySizeCalls { get; private set; }
	public int GetMemoryValuesCalls { get; private set; }
	public int SetMemoryValuesCalls { get; private set; }
	public int GetNesCpuStateCalls { get; private set; }
	public int PauseCalls { get; private set; }
	public int ResumeCalls { get; private set; }
	public int ResumeDebuggerCalls { get; private set; }
	public int IsExecutionStoppedCalls { get; private set; }
	public int StepCalls { get; private set; }
	public int GetDebuggerFeaturesCalls { get; private set; }
	public int EvaluateExpressionCalls { get; private set; }
	public int GetProgramCounterCalls { get; private set; }
	public int GetDisassemblyOutputCalls { get; private set; }
	public int GetDisassemblyRowAddressCalls { get; private set; }
	public int GetAbsoluteAddressCalls { get; private set; }
	public int GetRelativeAddressCalls { get; private set; }
	public int GetCallstackCalls { get; private set; }
	public int SetTraceOptionsCalls { get; private set; }
	public int ClearExecutionTraceCalls { get; private set; }
	public int GetExecutionTraceSizeCalls { get; private set; }
	public int GetExecutionTraceCalls { get; private set; }
	public int CreateSaveStateCalls { get; private set; }
	public int LoadSaveStateCalls { get; private set; }
	public int CaptureScreenshotCalls { get; private set; }
	public uint LastReadStart { get; private set; }
	public uint LastReadEndInclusive { get; private set; }
	public uint LastWriteStart { get; private set; }
	public byte[]? LastWriteData { get; private set; }

	public static FakeMcpEmulatorApi RunningNes() => new() {
		Running = true,
		RomInfo = new RomInfo {
			ConsoleType = ConsoleType.Nes,
			RomPath = "game.nes",
			CpuTypes = [CpuType.Nes]
		}
	};

	public bool IsRunning()
	{
		IsRunningCalls++;
		return IsRunningHandler?.Invoke() ?? Running;
	}

	public ulong GetDebuggerRequestBlockState()
	{
		int call = Interlocked.Increment(ref _debuggerRequestBlockStateCalls);
		if(call == ThrowOnDebuggerRequestBlockStateCall) {
			throw new InvalidOperationException("native snapshot details");
		}
		if(GetDebuggerRequestBlockStateHandler != null) {
			return GetDebuggerRequestBlockStateHandler();
		}
		lock(_debuggerRequestBlockStateLock) {
			return _debuggerRequestBlockState;
		}
	}

	public void SetDebuggerRequestBlocked(bool blocked)
	{
		lock(_debuggerRequestBlockStateLock) {
			if(blocked) {
				_debuggerRequestBlockCount++;
			} else {
				if(_debuggerRequestBlockCount == 0) {
					throw new InvalidOperationException("Debugger requests are not blocked.");
				}
				_debuggerRequestBlockCount--;
			}
			_debuggerRequestBlockState = (((_debuggerRequestBlockState >> 33) + 1) << 33)
				| ((ulong)_debuggerRequestBlockCount << 1)
				| (_debuggerRequestBlockCount > 0 ? 1UL : 0);
		}
	}

	public bool IsPaused()
	{
		IsPausedCalls++;
		return IsPausedHandler?.Invoke() ?? Paused;
	}

	public RomInfo GetRomInfo()
	{
		GetRomInfoCalls++;
		return GetRomInfoHandler?.Invoke() ?? RomInfo;
	}

	public int GetMemorySize(MemoryType type)
	{
		GetMemorySizeCalls++;
		return GetMemorySizeHandler?.Invoke(type) ?? MemorySizes.GetValueOrDefault(type, DefaultMemorySize);
	}

	public byte[] GetMemoryValues(MemoryType type, uint start, uint endInclusive)
	{
		GetMemoryValuesCalls++;
		LastReadStart = start;
		LastReadEndInclusive = endInclusive;
		OnRead?.Invoke();
		return GetMemoryValuesHandler?.Invoke(type, start, endInclusive) ?? ReadData;
	}

	public void SetMemoryValues(MemoryType type, uint start, byte[] data)
	{
		SetMemoryValuesCalls++;
		LastWriteStart = start;
		LastWriteData = data;
		SetMemoryValuesHandler?.Invoke(type, start, data);
	}

	public NesCpuState GetNesCpuState()
	{
		GetNesCpuStateCalls++;
		return GetNesCpuStateHandler?.Invoke() ?? NesCpuState;
	}

	public void Pause()
	{
		PauseCalls++;
		PauseHandler?.Invoke();
	}

	public void Resume()
	{
		ResumeCalls++;
		ResumeHandler?.Invoke();
	}

	public void ResumeDebugger()
	{
		ResumeDebuggerCalls++;
		ResumeDebuggerHandler?.Invoke();
	}

	public bool IsExecutionStopped()
	{
		IsExecutionStoppedCalls++;
		return IsExecutionStoppedHandler?.Invoke() ?? ExecutionStopped;
	}

	public void Step(CpuType cpuType, int instructionCount, StepType type)
	{
		StepCalls++;
		StepHandler?.Invoke(cpuType, instructionCount, type);
	}

	public DebuggerFeatures GetDebuggerFeatures(CpuType cpuType)
	{
		GetDebuggerFeaturesCalls++;
		return GetDebuggerFeaturesHandler?.Invoke(cpuType) ?? default;
	}

	public long EvaluateExpression(string expression, CpuType cpuType, out EvalResultType resultType, bool useCache)
	{
		EvaluateExpressionCalls++;
		if(EvaluateExpressionHandler != null) {
			return EvaluateExpressionHandler(expression, cpuType, out resultType, useCache);
		}
		resultType = EvalResultType.Numeric;
		return 0;
	}

	public uint GetProgramCounter(CpuType cpuType, bool instructionProgramCounter)
	{
		GetProgramCounterCalls++;
		return GetProgramCounterHandler?.Invoke(cpuType, instructionProgramCounter) ?? ProgramCounter;
	}

	public CodeLineData[] GetDisassemblyOutput(CpuType cpuType, uint address, uint rowCount)
	{
		GetDisassemblyOutputCalls++;
		return GetDisassemblyOutputHandler?.Invoke(cpuType, address, rowCount) ?? DisassemblyOutput;
	}

	public int GetDisassemblyRowAddress(CpuType cpuType, uint address, int rowOffset)
	{
		GetDisassemblyRowAddressCalls++;
		return GetDisassemblyRowAddressHandler?.Invoke(cpuType, address, rowOffset) ?? (int)address + rowOffset;
	}

	public AddressInfo GetAbsoluteAddress(AddressInfo relativeAddress)
	{
		GetAbsoluteAddressCalls++;
		return GetAbsoluteAddressHandler?.Invoke(relativeAddress) ?? relativeAddress;
	}

	public AddressInfo GetRelativeAddress(AddressInfo absoluteAddress, CpuType cpuType)
	{
		GetRelativeAddressCalls++;
		return GetRelativeAddressHandler?.Invoke(absoluteAddress, cpuType) ?? absoluteAddress;
	}

	public StackFrameInfo[] GetCallstack(CpuType cpuType)
	{
		GetCallstackCalls++;
		return GetCallstackHandler?.Invoke(cpuType) ?? Callstack;
	}

	public void SetTraceOptions(CpuType cpuType, InteropTraceLoggerOptions options)
	{
		SetTraceOptionsCalls++;
		SetTraceOptionsHandler?.Invoke(cpuType, options);
	}

	public void ClearExecutionTrace()
	{
		ClearExecutionTraceCalls++;
		ClearExecutionTraceHandler?.Invoke();
	}

	public uint GetExecutionTraceSize()
	{
		GetExecutionTraceSizeCalls++;
		return GetExecutionTraceSizeHandler?.Invoke() ?? (uint)ExecutionTrace.Length;
	}

	public TraceRow[] GetExecutionTrace(uint startOffset, uint maxRowCount)
	{
		GetExecutionTraceCalls++;
		return GetExecutionTraceHandler?.Invoke(startOffset, maxRowCount) ?? ExecutionTrace;
	}

	public McpServiceResult<byte[]> CreateSaveState()
	{
		CreateSaveStateCalls++;
		return CreateSaveStateHandler?.Invoke() ?? McpServiceResult<byte[]>.Success([]);
	}

	public McpServiceResult<bool> LoadSaveState(byte[] data)
	{
		LoadSaveStateCalls++;
		return LoadSaveStateHandler?.Invoke(data) ?? McpServiceResult<bool>.Success(true);
	}

	public McpServiceResult<McpScreenshotCapture> CaptureScreenshot()
	{
		CaptureScreenshotCalls++;
		return CaptureScreenshotHandler?.Invoke()
			?? McpServiceResult<McpScreenshotCapture>.Failure("no_frame", "No decoded video frame is available.");
	}
}
