using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mesen.Debugger;
using Mesen.Debugger.Utilities;
using Mesen.Interop;

[assembly: InternalsVisibleTo("UI.Tests")]

namespace Mesen.Mcp;

internal sealed class McpEmulatorService : IDisposable
{
	public const int MaxTransferSize = 65536;
	private const string McpVersion = "1.0";
	private static readonly string MesenVersion = typeof(McpEmulatorService).Assembly.GetName().Version?.ToString(3) ?? "unknown";

	private readonly IMcpEmulatorApi _api;
	private readonly McpEmulatorGate _gate;
	private readonly IDebuggerLifetimeCoordinator _debuggerLifetime;
	private readonly IMcpBreakpointCollection _breakpointCollection;
	private readonly Dictionary<long, Breakpoint> _mcpBreakpoints = [];
	private readonly object _executionLock = new();
	private readonly Dictionary<CpuType, TaskCompletionSource<BreakEvent>> _breakWaiters = [];
	private BreakEvent? _latestBreakEvent;
	private long _latestBreakGeneration = -1;
	private long _nextBreakpointId;
	private IDisposable? _debuggerLease;
	private bool _disposed;

	internal McpEmulatorService(
		IMcpEmulatorApi api,
		McpEmulatorGate? gate = null,
		IDebuggerLifetimeCoordinator? debuggerLifetime = null,
		IMcpBreakpointCollection? breakpointCollection = null)
	{
		_api = api;
		_gate = gate ?? new McpEmulatorGate(api);
		_debuggerLifetime = debuggerLifetime ?? DebuggerLifetimeCoordinator.Shared;
		_breakpointCollection = breakpointCollection ?? new McpBreakpointCollection();
	}

	public McpServiceResult<EmulatorStatus> GetStatus()
	{
		return Execute(() => {
			if(!_api.IsRunning()) {
				return McpServiceResult<EmulatorStatus>.Success(new(false, null, null, "stopped", MesenVersion, McpVersion));
			}

			bool paused = _api.IsPaused();
			RomInfo romInfo = _api.GetRomInfo();
			return McpServiceResult<EmulatorStatus>.Success(new(
				true,
				romInfo.ConsoleType.ToString(),
				romInfo.GetRomName(),
				paused ? "paused" : "running",
				MesenVersion,
				McpVersion
			));
		});
	}

	public McpServiceResult<IReadOnlyList<MemorySpace>> ListMemorySpaces()
	{
		return Execute(() => {
			if(!_api.IsRunning()) {
				return McpServiceResult<IReadOnlyList<MemorySpace>>.Failure("no_game", "No game is currently loaded.");
			}

			List<MemorySpace> spaces = new();
			foreach(MemoryType type in Enum.GetValues<MemoryType>()) {
				if(type == MemoryType.None) {
					continue;
				}

				int size = _api.GetMemorySize(type);
				if(size > 0) {
					spaces.Add(new(type.ToString(), type.GetShortName(), size, true, McpMemoryCapabilities.CanWrite(type)));
				}
			}

			return McpServiceResult<IReadOnlyList<MemorySpace>>.Success(spaces);
		});
	}

	public McpServiceResult<CpuRegisters> GetCpuRegisters()
	{
		return Execute(() => {
			if(!_api.IsRunning()) {
				return McpServiceResult<CpuRegisters>.Failure("no_game", "No game is currently loaded.");
			}

			RomInfo romInfo = _api.GetRomInfo();
			if(!romInfo.CpuTypes.Contains(CpuType.Nes)) {
				return McpServiceResult<CpuRegisters>.Failure("registers_not_supported", "CPU registers are not supported for the current system.");
			}

			return McpServiceResult<CpuRegisters>.Success(BuildNesRegisters(romInfo));
		});
	}

	public McpServiceResult<MemoryRead> ReadMemory(string space, uint address, int count)
	{
		if(count > MaxTransferSize) {
			return McpServiceResult<MemoryRead>.Failure("payload_too_large", $"Read count cannot exceed {MaxTransferSize} bytes.");
		}

		return Execute(() => {
			if(!_api.IsRunning()) {
				return McpServiceResult<MemoryRead>.Failure("no_game", "No game is currently loaded.");
			}

			if(!TryResolveMemorySpace(space, out MemoryType type, out int size)) {
				return McpServiceResult<MemoryRead>.Failure("unknown_memory_space", "The selected memory space is not available.");
			}

			if(count <= 0 || address >= size || (uint)count > (uint)size - address) {
				return McpServiceResult<MemoryRead>.Failure("invalid_range", "The requested range is outside the selected memory space.");
			}

			uint endInclusive = address + (uint)count - 1;
			byte[] data = _api.GetMemoryValues(type, address, endInclusive);
			return McpServiceResult<MemoryRead>.Success(new(space, address, count, data, Convert.ToHexString(data)));
		});
	}

	public McpServiceResult<MemoryWrite> WriteMemory(string space, uint address, byte[] data)
	{
		return Execute(() => {
			if(!_api.IsRunning()) {
				return McpServiceResult<MemoryWrite>.Failure("no_game", "No game is currently loaded.");
			}

			if(!TryResolveMemorySpace(space, out MemoryType type, out int size)) {
				return McpServiceResult<MemoryWrite>.Failure("unknown_memory_space", "The selected memory space is not available.");
			}

			if(!McpMemoryCapabilities.CanWrite(type)) {
				return McpServiceResult<MemoryWrite>.Failure("memory_space_read_only", "The selected memory space does not support writes.");
			}

			int count = data.Length;
			if(count <= 0 || count > MaxTransferSize || address >= size || (uint)count > (uint)size - address) {
				return McpServiceResult<MemoryWrite>.Failure("invalid_range", "The requested range is outside the selected memory space.");
			}

			_api.SetMemoryValues(type, address, data);
			return McpServiceResult<MemoryWrite>.Success(new(space, address, count));
		});
	}

	internal McpServiceResult<McpBreakpoint> SetBreakpoint(
		string cpu,
		string space,
		string access,
		uint startAddress,
		uint? endAddress,
		string? condition)
	{
		if(Encoding.UTF8.GetByteCount(condition ?? "") > McpDebuggerLimits.MaxConditionUtf8Bytes) {
			return McpServiceResult<McpBreakpoint>.Failure(
				"payload_too_large",
				$"Breakpoint conditions cannot exceed {McpDebuggerLimits.MaxConditionUtf8Bytes} UTF-8 bytes."
			);
		}

		if(!TryParseExactEnum(cpu, out CpuType cpuType)) {
			return McpServiceResult<McpBreakpoint>.Failure("unknown_cpu", "The selected CPU is not available.");
		}

		if(access is not ("execute" or "read" or "write")) {
			return McpServiceResult<McpBreakpoint>.Failure("invalid_access", "Breakpoint access must be execute, read, or write.");
		}

		return Execute(() => {
			if(_mcpBreakpoints.Count >= McpDebuggerLimits.MaxMcpBreakpoints) {
				return McpServiceResult<McpBreakpoint>.Failure(
					"payload_too_large",
					$"MCP breakpoint count cannot exceed {McpDebuggerLimits.MaxMcpBreakpoints}."
				);
			}

			if(!_api.IsRunning()) {
				return McpServiceResult<McpBreakpoint>.Failure("no_game", "No game is currently loaded.");
			}

			RomInfo romInfo = _api.GetRomInfo();
			if(!romInfo.CpuTypes.Contains(cpuType)) {
				return McpServiceResult<McpBreakpoint>.Failure("unknown_cpu", "The selected CPU is not available.");
			}

			if(!TryResolveMemorySpace(space, out MemoryType memoryType, out int memorySize)) {
				return McpServiceResult<McpBreakpoint>.Failure("unknown_memory_space", "The selected memory space is not available.");
			}

			if(access == "execute" && !memoryType.SupportsExecBreakpoints()) {
				return McpServiceResult<McpBreakpoint>.Failure("invalid_access", "The selected memory space does not support execute breakpoints.");
			}

			uint normalizedEnd = endAddress ?? startAddress;
			if(normalizedEnd < startAddress || startAddress >= (uint)memorySize || normalizedEnd >= (uint)memorySize) {
				return McpServiceResult<McpBreakpoint>.Failure("invalid_range", "The breakpoint range is outside the selected memory space.");
			}

			EnsureDebuggerLease();
			if(!string.IsNullOrEmpty(condition)) {
				_api.EvaluateExpression(condition, cpuType, out EvalResultType resultType, useCache: false);
				if(resultType is not (EvalResultType.Numeric or EvalResultType.Boolean)) {
					return McpServiceResult<McpBreakpoint>.Failure("invalid_expression", "The breakpoint condition is not a valid expression.");
				}
			}

			long id = ++_nextBreakpointId;
			Breakpoint breakpoint = new() {
				CpuType = cpuType,
				MemoryType = memoryType,
				StartAddress = startAddress,
				EndAddress = normalizedEnd,
				BreakOnExec = access == "execute",
				BreakOnRead = access == "read",
				BreakOnWrite = access == "write",
				Condition = condition?.Trim() ?? "",
				Enabled = true
			};
			List<BreakpointManager.ExternalBreakpoint> replacements = GetExternalBreakpoints();
			replacements.Add(new(id, breakpoint));
			_breakpointCollection.Replace(replacements);
			_mcpBreakpoints.Add(id, breakpoint);

			return McpServiceResult<McpBreakpoint>.Success(ToMcpBreakpoint(id, breakpoint));
		});
	}

	internal McpServiceResult<IReadOnlyList<McpBreakpoint>> ListBreakpoints()
	{
		return Execute(() => {
			if(!_api.IsRunning()) {
				return McpServiceResult<IReadOnlyList<McpBreakpoint>>.Failure("no_game", "No game is currently loaded.");
			}

			EnsureDebuggerLease();
			List<McpBreakpoint> breakpoints = new(_mcpBreakpoints.Count);
			foreach((long id, Breakpoint breakpoint) in _mcpBreakpoints) {
				breakpoints.Add(ToMcpBreakpoint(id, breakpoint));
			}
			return McpServiceResult<IReadOnlyList<McpBreakpoint>>.Success(breakpoints);
		});
	}

	internal McpServiceResult<BreakpointRemoval> RemoveBreakpoint(long id)
	{
		return Execute(() => {
			if(!_api.IsRunning()) {
				return McpServiceResult<BreakpointRemoval>.Failure("no_game", "No game is currently loaded.");
			}

			EnsureDebuggerLease();
			if(!_mcpBreakpoints.ContainsKey(id)) {
				return McpServiceResult<BreakpointRemoval>.Failure("breakpoint_not_owned", "The breakpoint is not owned by MCP.");
			}

			List<BreakpointManager.ExternalBreakpoint> replacements = GetExternalBreakpoints(id);
			_breakpointCollection.Replace(replacements);
			_mcpBreakpoints.Remove(id);
			return McpServiceResult<BreakpointRemoval>.Success(new(id, true));
		});
	}

	internal McpServiceResult<BreakpointRemovalSummary> RemoveAllBreakpoints()
	{
		return Execute(() => {
			if(!_api.IsRunning()) {
				return McpServiceResult<BreakpointRemovalSummary>.Failure("no_game", "No game is currently loaded.");
			}

			EnsureDebuggerLease();
			int removedCount = _mcpBreakpoints.Count;
			if(removedCount > 0) {
				_breakpointCollection.Replace([]);
				_mcpBreakpoints.Clear();
			}
			return McpServiceResult<BreakpointRemovalSummary>.Success(new(removedCount));
		});
	}

	internal McpServiceResult<IReadOnlyList<DisassemblyRow>> Disassemble(
		string cpu,
		string? space,
		uint? address,
		int before,
		int after)
	{
		if(before < 0 || after < 0 || before > McpDebuggerLimits.MaxDisassemblyRows - after - 1) {
			return McpServiceResult<IReadOnlyList<DisassemblyRow>>.Failure("invalid_range", "The disassembly window is outside the supported range.");
		}
		if(!TryParseExactEnum(cpu, out CpuType cpuType)) {
			return McpServiceResult<IReadOnlyList<DisassemblyRow>>.Failure("unknown_cpu", "The selected CPU is not available.");
		}

		return Execute(() => {
			if(!_api.IsRunning()) {
				return McpServiceResult<IReadOnlyList<DisassemblyRow>>.Failure("no_game", "No game is currently loaded.");
			}
			if(!_api.GetRomInfo().CpuTypes.Contains(cpuType)) {
				return McpServiceResult<IReadOnlyList<DisassemblyRow>>.Failure("unknown_cpu", "The selected CPU is not available.");
			}

			uint centerAddress;
			if(space is null) {
				centerAddress = address ?? _api.GetProgramCounter(cpuType, true);
			} else {
				if(!address.HasValue || !TryResolveMemorySpace(space, out MemoryType memoryType, out int memorySize)) {
					return McpServiceResult<IReadOnlyList<DisassemblyRow>>.Failure("invalid_address", "The disassembly address is not available.");
				}
				if(address.Value >= (uint)memorySize) {
					return McpServiceResult<IReadOnlyList<DisassemblyRow>>.Failure("invalid_address", "The disassembly address is not available.");
				}
				if(memoryType == cpuType.ToMemoryType()) {
					centerAddress = address.Value;
				} else {
					AddressInfo relative = _api.GetRelativeAddress(new AddressInfo { Address = (int)address.Value, Type = memoryType }, cpuType);
					if(relative.Address < 0) {
						return McpServiceResult<IReadOnlyList<DisassemblyRow>>.Failure("invalid_address", "The disassembly address is not mapped to the selected CPU.");
					}
					centerAddress = (uint)relative.Address;
				}
			}

			return McpServiceResult<IReadOnlyList<DisassemblyRow>>.Success(BuildDisassembly(cpuType, centerAddress, before, after));
		});
	}

	internal McpServiceResult<AddressMapping> MapAddress(string cpu, string space, uint address)
	{
		if(!TryParseExactEnum(cpu, out CpuType cpuType)) {
			return McpServiceResult<AddressMapping>.Failure("unknown_cpu", "The selected CPU is not available.");
		}

		return _gate.ExecuteWithTicket(ticket => {
			if(!_api.IsRunning()) {
				return McpServiceResult<AddressMapping>.Failure("no_game", "No game is currently loaded.");
			}
			if(!_api.GetRomInfo().CpuTypes.Contains(cpuType)) {
				return McpServiceResult<AddressMapping>.Failure("unknown_cpu", "The selected CPU is not available.");
			}
			if(!TryResolveMemorySpace(space, out MemoryType memoryType, out int memorySize) || address >= (uint)memorySize) {
				return McpServiceResult<AddressMapping>.Failure("invalid_address", "The selected address is not available.");
			}

			AddressInfo relative;
			AddressInfo physical;
			if(memoryType == cpuType.ToMemoryType()) {
				relative = new AddressInfo { Address = (int)address, Type = memoryType };
				physical = _api.GetAbsoluteAddress(relative);
			} else {
				physical = new AddressInfo { Address = (int)address, Type = memoryType };
				relative = _api.GetRelativeAddress(physical, cpuType);
			}
			if(relative.Address < 0 || physical.Address < 0) {
				return McpServiceResult<AddressMapping>.Failure("invalid_address", "The selected address is not mapped.");
			}

			return McpServiceResult<AddressMapping>.Success(new(
				cpu,
				ToMcpAddress(relative),
				ToMcpAddress(physical),
				ticket.Generation
			));
		});
	}

	internal McpServiceResult<CallStackResult> GetCallStack(string cpu, int maxDepth)
	{
		if(maxDepth is < 1 or > McpDebuggerLimits.MaxCallStackDepth) {
			return McpServiceResult<CallStackResult>.Failure("invalid_range", "The call stack depth is outside the supported range.");
		}
		if(!TryParseExactEnum(cpu, out CpuType cpuType)) {
			return McpServiceResult<CallStackResult>.Failure("unknown_cpu", "The selected CPU is not available.");
		}

		return _gate.ExecuteWithTicket(ticket => {
			if(!_api.IsRunning()) {
				return McpServiceResult<CallStackResult>.Failure("no_game", "No game is currently loaded.");
			}
			if(!_api.GetRomInfo().CpuTypes.Contains(cpuType)) {
				return McpServiceResult<CallStackResult>.Failure("unknown_cpu", "The selected CPU is not available.");
			}

			return McpServiceResult<CallStackResult>.Success(BuildCallStack(cpuType, maxDepth, ticket.Generation));
		});
	}

	internal McpServiceResult<BreakContext> GetBreakContext(int before, int after, int maxStackDepth)
	{
		if(before < 0 || after < 0 || before > McpDebuggerLimits.MaxDisassemblyRows - after - 1) {
			return McpServiceResult<BreakContext>.Failure("invalid_range", "The disassembly window is outside the supported range.");
		}
		if(maxStackDepth is < 1 or > McpDebuggerLimits.MaxCallStackDepth) {
			return McpServiceResult<BreakContext>.Failure("invalid_range", "The call stack depth is outside the supported range.");
		}

		return Execute(() => {
			BreakEvent breakEvent;
			long generation;
			lock(_executionLock) {
				if(!_api.IsExecutionStopped()
					|| !_latestBreakEvent.HasValue
					|| _latestBreakGeneration != _gate.Generation) {
					return McpServiceResult<BreakContext>.Failure("stale_context", "No current stopped break context is available.");
				}
				breakEvent = _latestBreakEvent.Value;
				generation = _latestBreakGeneration;
			}
			return BuildBreakContext(breakEvent, generation, before, after, maxStackDepth);
		});
	}

	internal void ProcessNotification(NotificationEventArgs e)
	{
		if(e.NotificationType != ConsoleNotificationType.CodeBreak || e.Parameter == IntPtr.Zero) {
			return;
		}

		BreakEvent copied = Marshal.PtrToStructure<BreakEvent>(e.Parameter);
		TaskCompletionSource<BreakEvent>? waiter;
		lock(_executionLock) {
			if(_gate.ShutdownToken.IsCancellationRequested) {
				return;
			}
			_latestBreakEvent = copied;
			_latestBreakGeneration = _gate.Generation;
			_breakWaiters.Remove(copied.SourceCpu, out waiter);
		}
		waiter?.TrySetResult(copied);
	}

	internal McpServiceResult<ExecutionState> Pause()
	{
		return _gate.ExecuteWithTicket(ticket => {
			if(!_api.IsRunning()) {
				return McpServiceResult<ExecutionState>.Failure("no_game", "No game is currently loaded.");
			}
			_api.Pause();
			return McpServiceResult<ExecutionState>.Success(new("paused", ticket.Generation));
		});
	}

	internal McpServiceResult<ExecutionState> Resume()
	{
		return _gate.ExecuteWithTicket(ticket => {
			if(!_api.IsRunning()) {
				return McpServiceResult<ExecutionState>.Failure("no_game", "No game is currently loaded.");
			}

			InvalidateLatestBreak();
			if(_api.IsExecutionStopped()) {
				_api.ResumeDebugger();
			} else {
				_api.Resume();
			}
			return McpServiceResult<ExecutionState>.Success(new("running", ticket.Generation));
		});
	}

	internal McpServiceResult<ExecutionState> Step(string cpu, string stepType)
	{
		if(!TryParseExactEnum(cpu, out CpuType cpuType)) {
			return McpServiceResult<ExecutionState>.Failure("unknown_cpu", "The selected CPU is not available.");
		}
		if(!TryParseStepType(stepType, out StepType nativeStepType)) {
			return McpServiceResult<ExecutionState>.Failure("invalid_step_type", "The selected step type is not supported.");
		}

		return _gate.ExecuteWithTicket(ticket => {
			if(!_api.IsRunning()) {
				return McpServiceResult<ExecutionState>.Failure("no_game", "No game is currently loaded.");
			}
			RomInfo romInfo = _api.GetRomInfo();
			if(!romInfo.CpuTypes.Contains(cpuType)) {
				return McpServiceResult<ExecutionState>.Failure("unknown_cpu", "The selected CPU is not available.");
			}
			if(!_api.IsExecutionStopped()) {
				return McpServiceResult<ExecutionState>.Failure("debugger_unavailable", "Execution must be stopped before stepping.");
			}
			if(!IsStepSupported(cpuType, nativeStepType, romInfo)) {
				return McpServiceResult<ExecutionState>.Failure("invalid_step_type", "The selected step type is not supported.");
			}

			EnsureDebuggerLease();
			InvalidateLatestBreak();
			_api.Step(cpuType, 1, nativeStepType);
			return McpServiceResult<ExecutionState>.Success(new("running", ticket.Generation));
		});
	}

	internal async Task<McpServiceResult<ContinueResult>> ContinueUntilBreakAsync(
		string cpu,
		int timeoutMs,
		CancellationToken cancellationToken)
	{
		McpOperationTicket ticket = default;
		CpuType cpuType = default;
		TaskCompletionSource<BreakEvent>? waiter = null;
		McpServiceResult<ExecutionState> setup = _gate.ExecuteWithTicket(currentTicket => {
			if(timeoutMs is < McpDebuggerLimits.MinContinueTimeoutMs or > McpDebuggerLimits.MaxContinueTimeoutMs) {
				return McpServiceResult<ExecutionState>.Failure("invalid_timeout", "Continue timeout is outside the supported range.");
			}
			if(!TryParseExactEnum(cpu, out cpuType)) {
				return McpServiceResult<ExecutionState>.Failure("unknown_cpu", "The selected CPU is not available.");
			}
			if(!_api.IsRunning()) {
				return McpServiceResult<ExecutionState>.Failure("no_game", "No game is currently loaded.");
			}
			if(!_api.GetRomInfo().CpuTypes.Contains(cpuType)) {
				return McpServiceResult<ExecutionState>.Failure("unknown_cpu", "The selected CPU is not available.");
			}
			if(cancellationToken.IsCancellationRequested) {
				return McpServiceResult<ExecutionState>.Failure("cancelled", "The continue operation was cancelled.");
			}

			lock(_executionLock) {
				if(_gate.ShutdownToken.IsCancellationRequested) {
					return McpServiceResult<ExecutionState>.Failure("server_stopping", "The MCP server is shutting down.");
				}
				if(_gate.Generation != currentTicket.Generation) {
					return McpServiceResult<ExecutionState>.Failure("state_changed", "Emulator state changed during the operation.");
				}
				if(_breakWaiters.ContainsKey(cpuType)) {
					return McpServiceResult<ExecutionState>.Failure("operation_in_progress", "A continue operation is already active for this CPU.");
				}
				waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
				_breakWaiters.Add(cpuType, waiter);
				_latestBreakEvent = null;
				_latestBreakGeneration = -1;
			}

			ticket = currentTicket;
			EnsureDebuggerLease();
			if(_api.IsExecutionStopped()) {
				_api.ResumeDebugger();
			} else {
				_api.Resume();
			}
			return McpServiceResult<ExecutionState>.Success(new("running", ticket.Generation));
		});

		if(!setup.IsSuccess) {
			RemoveWaiter(cpuType, waiter);
			return McpServiceResult<ContinueResult>.Failure(setup.Error!.Code, setup.Error.Message);
		}

		using CancellationTokenSource timeoutCancellation = new(timeoutMs);
		using CancellationTokenSource linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
			cancellationToken,
			timeoutCancellation.Token,
			_gate.ShutdownToken
		);
		try {
			BreakEvent breakEvent = await waiter!.Task.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
			return _gate.ExecuteForTicket(ticket, () => CreateContinueResult(breakEvent, ticket.Generation));
		} catch(OperationCanceledException) {
			if(_gate.ShutdownToken.IsCancellationRequested) {
				return McpServiceResult<ContinueResult>.Failure("server_stopping", "The MCP server is shutting down.");
			}
			if(_gate.Generation != ticket.Generation) {
				return McpServiceResult<ContinueResult>.Failure("state_changed", "Emulator state changed during the operation.");
			}
			if(cancellationToken.IsCancellationRequested) {
				return McpServiceResult<ContinueResult>.Failure("cancelled", "The continue operation was cancelled.");
			}
			return McpServiceResult<ContinueResult>.Failure("timeout", "The continue operation timed out.");
		} finally {
			RemoveWaiter(cpuType, waiter);
		}
	}

	private bool TryResolveMemorySpace(string id, out MemoryType type, out int size)
	{
		if(!Enum.TryParse(id, ignoreCase: false, out type) || type == MemoryType.None || type.ToString() != id) {
			size = 0;
			return false;
		}

		size = _api.GetMemorySize(type);
		return size > 0;
	}

	private static bool TryParseExactEnum<T>(string value, out T result) where T : struct, Enum
	{
		return Enum.TryParse(value, ignoreCase: false, out result) && result.ToString() == value;
	}

	private static bool TryParseStepType(string value, out StepType stepType)
	{
		stepType = value switch {
			"instruction" => StepType.Step,
			"over" => StepType.StepOver,
			"out" => StepType.StepOut,
			"cpu_cycle" => StepType.CpuCycleStep,
			"ppu_scanline" => StepType.PpuScanline,
			"ppu_frame" => StepType.PpuFrame,
			"back" => StepType.StepBack,
			_ => default
		};
		return value is "instruction" or "over" or "out" or "cpu_cycle" or "ppu_scanline" or "ppu_frame" or "back";
	}

	private bool IsStepSupported(CpuType cpuType, StepType stepType, RomInfo romInfo)
	{
		DebuggerFeatures features = _api.GetDebuggerFeatures(cpuType);
		return stepType switch {
			StepType.Step => true,
			StepType.StepOver => features.StepOver,
			StepType.StepOut => features.StepOut,
			StepType.CpuCycleStep => features.CpuCycleStep,
			StepType.StepBack => features.StepBack,
			StepType.PpuScanline or StepType.PpuFrame => cpuType == romInfo.ConsoleType.GetMainCpuType(),
			_ => false
		};
	}

	private McpServiceResult<ContinueResult> CreateContinueResult(BreakEvent breakEvent, long generation)
	{
		McpServiceResult<BreakContext> context = BuildBreakContext(
			breakEvent,
			generation,
			8,
			8,
			McpDebuggerLimits.MaxCallStackDepth
		);
		if(!context.IsSuccess) {
			return McpServiceResult<ContinueResult>.Failure(context.Error!.Code, context.Error.Message);
		}
		return McpServiceResult<ContinueResult>.Success(new(context.Value!.Reason, context.Value, generation));
	}

	private McpServiceResult<BreakContext> BuildBreakContext(
		BreakEvent breakEvent,
		long generation,
		int before,
		int after,
		int maxStackDepth)
	{
		string reason = breakEvent.Source switch {
			BreakSource.Breakpoint => "breakpoint",
			BreakSource.Pause => "pause",
			_ => breakEvent.Source.ToString().ToLowerInvariant()
		};
		long? breakpointId = null;
		if(breakEvent.Source == BreakSource.Breakpoint
			&& _breakpointCollection.TryGetStableId(breakEvent.BreakpointId, out long stableId)) {
			breakpointId = stableId;
		}
		McpMemoryOperation operation = new(
			breakEvent.Operation.MemType.ToString(),
			breakEvent.Operation.Address,
			breakEvent.Operation.Type.ToString().ToLowerInvariant(),
			1,
			breakEvent.Operation.Value
		);
		if(breakEvent.SourceCpu != CpuType.Nes) {
			return McpServiceResult<BreakContext>.Failure("unsupported_cpu", "Register context is not supported for the selected CPU.");
		}

		RomInfo romInfo = _api.GetRomInfo();
		uint programCounter = _api.GetProgramCounter(breakEvent.SourceCpu, true);
		AddressInfo physicalProgramCounter = _api.GetAbsoluteAddress(new AddressInfo {
			Address = (int)programCounter,
			Type = breakEvent.SourceCpu.ToMemoryType()
		});
		IReadOnlyList<DisassemblyRow> disassembly = BuildDisassembly(breakEvent.SourceCpu, programCounter, before, after);
		DisassemblyRow? currentRow = null;
		foreach(DisassemblyRow row in disassembly) {
			if(row.CpuAddress == programCounter) {
				currentRow = row;
				break;
			}
		}
		CallStackResult callStack = BuildCallStack(breakEvent.SourceCpu, maxStackDepth, generation);
		BreakContext context = new(
			reason,
			breakpointId,
			breakEvent.SourceCpu.ToString(),
			operation,
			BuildNesRegisters(romInfo),
			programCounter,
			physicalProgramCounter.Address >= 0 ? ToMcpAddress(physicalProgramCounter) : null,
			disassembly,
			currentRow?.EffectiveAddress,
			currentRow?.EffectiveValue,
			callStack.Frames,
			generation
		);
		return McpServiceResult<BreakContext>.Success(context);
	}

	private IReadOnlyList<DisassemblyRow> BuildDisassembly(CpuType cpuType, uint centerAddress, int before, int after)
	{
		int start = _api.GetDisassemblyRowAddress(cpuType, centerAddress, -before);
		if(start < 0) {
			start = (int)centerAddress;
		}
		int requestedCount = before + after + 1;
		CodeLineData[] nativeRows = _api.GetDisassemblyOutput(cpuType, (uint)start, (uint)requestedCount);
		int count = Math.Min(nativeRows.Length, requestedCount);
		List<DisassemblyRow> rows = new(count);
		for(int i = 0; i < count; i++) {
			CodeLineData row = nativeRows[i];
			int byteCount = Math.Min(row.OpSize, row.ByteCode.Length);
			McpAddress? effectiveAddress = row.ShowEffectiveAddress && row.EffectiveAddress >= 0
				? new McpAddress(row.EffectiveAddressType.ToString(), (int)row.EffectiveAddress)
				: null;
			rows.Add(new(
				(uint)row.Address,
				row.AbsoluteAddress.Address >= 0 ? ToMcpAddress(row.AbsoluteAddress) : null,
				Convert.ToHexString(row.ByteCode.AsSpan(0, byteCount)),
				row.Text,
				row.Comment,
				effectiveAddress,
				effectiveAddress is null ? null : row.Value,
				effectiveAddress is null ? null : row.ValueSize
			));
		}
		return rows;
	}

	private CallStackResult BuildCallStack(CpuType cpuType, int maxDepth, long generation)
	{
		bool live = !_api.IsExecutionStopped();
		StackFrameInfo[] nativeFrames = _api.GetCallstack(cpuType);
		int count = Math.Min(nativeFrames.Length, maxDepth);
		List<CallStackFrame> frames = new(count);
		for(int i = 0; i < count; i++) {
			StackFrameInfo frame = nativeFrames[i];
			frames.Add(new(
				frame.Source,
				ToMcpAddress(frame.AbsSource),
				frame.Target,
				ToMcpAddress(frame.AbsTarget),
				frame.Return,
				ToMcpAddress(frame.AbsReturn),
				frame.Flags.ToString()
			));
		}
		return new(cpuType.ToString(), live, generation, frames, nativeFrames.Length > maxDepth);
	}

	private CpuRegisters BuildNesRegisters(RomInfo romInfo)
	{
		NesCpuState state = _api.GetNesCpuState();
		return new(
			romInfo.ConsoleType.ToString(),
			CpuType.Nes.ToString(),
			"6502",
			[
				Register("A", state.A, 8),
				Register("X", state.X, 8),
				Register("Y", state.Y, 8),
				Register("SP", state.SP, 8),
				Register("PC", state.PC, 16),
				Register("PS", state.PS, 8),
				Register("IRQFlag", state.IRQFlag, 8),
				Register("NMIFlag", state.NMIFlag ? 1UL : 0UL, 1),
				Register("CycleCount", state.CycleCount, 64)
			]
		);
	}

	private static McpAddress ToMcpAddress(AddressInfo address) => new(address.Type.ToString(), address.Address);

	private void RemoveWaiter(CpuType cpuType, TaskCompletionSource<BreakEvent>? waiter)
	{
		if(waiter is null) {
			return;
		}
		lock(_executionLock) {
			if(_breakWaiters.TryGetValue(cpuType, out TaskCompletionSource<BreakEvent>? current)
				&& ReferenceEquals(current, waiter)) {
				_breakWaiters.Remove(cpuType);
			}
		}
	}

	private void InvalidateLatestBreak()
	{
		lock(_executionLock) {
			_latestBreakEvent = null;
			_latestBreakGeneration = -1;
		}
	}

	private void InvalidateExecutionState(Action changeState)
	{
		TaskCompletionSource<BreakEvent>[] waiters;
		lock(_executionLock) {
			changeState();
			waiters = [.. _breakWaiters.Values];
			_breakWaiters.Clear();
			_latestBreakEvent = null;
			_latestBreakGeneration = -1;
		}
		foreach(TaskCompletionSource<BreakEvent> waiter in waiters) {
			waiter.TrySetCanceled();
		}
	}

	private List<BreakpointManager.ExternalBreakpoint> GetExternalBreakpoints(long? excludedId = null)
	{
		List<BreakpointManager.ExternalBreakpoint> breakpoints = new(_mcpBreakpoints.Count);
		foreach((long id, Breakpoint breakpoint) in _mcpBreakpoints) {
			if(id != excludedId) {
				breakpoints.Add(new(id, breakpoint));
			}
		}
		return breakpoints;
	}

	private static McpBreakpoint ToMcpBreakpoint(long id, Breakpoint breakpoint)
	{
		string access = breakpoint.BreakOnExec ? "execute" : breakpoint.BreakOnRead ? "read" : "write";
		return new(
			id,
			breakpoint.CpuType.ToString(),
			breakpoint.MemoryType.ToString(),
			access,
			breakpoint.StartAddress,
			breakpoint.EndAddress,
			breakpoint.Condition,
			breakpoint.Enabled
		);
	}

	private void EnsureDebuggerLease()
	{
		_debuggerLease ??= _debuggerLifetime.Acquire();
	}

	private static CpuRegister Register(string name, ulong value, int bits)
	{
		int digits = (bits + 3) / 4;
		return new(name, value, bits, value.ToString($"X{digits}"));
	}

	internal void NotifyEmulatorStateChanged()
	{
		InvalidateExecutionState(_gate.NotifyEmulatorStateChanged);
	}

	internal void BeginEmulatorTransition()
	{
		InvalidateExecutionState(_gate.BeginEmulatorTransition);
	}

	internal void EndEmulatorTransition()
	{
		InvalidateExecutionState(_gate.EndEmulatorTransition);
	}

	internal void BeginShutdown()
	{
		InvalidateExecutionState(_gate.BeginShutdown);
	}

	internal void DrainOperations()
	{
		_gate.DrainOperations();
	}

	private McpServiceResult<T> Execute<T>(Func<McpServiceResult<T>> operation)
	{
		return _gate.Execute(operation);
	}

	public void Dispose()
	{
		if(_disposed) {
			return;
		}
		_disposed = true;
		try {
			_breakpointCollection.Dispose();
		} finally {
			_debuggerLease?.Dispose();
		}
	}
}
