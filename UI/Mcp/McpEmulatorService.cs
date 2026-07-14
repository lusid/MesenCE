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
	private readonly McpEmulatorIdentity _emulatorIdentity;
	private readonly McpExecutionCoordinator _executionCoordinator;
	private readonly IDebuggerLifetimeCoordinator _debuggerLifetime;
	private readonly ITraceLoggerCoordinator _traceCoordinator;
	private readonly object _traceOwner = new();
	private readonly IMcpBreakpointCollection _breakpointCollection;
	private readonly Dictionary<long, Breakpoint> _mcpBreakpoints = [];
	private readonly object _executionLock = new();
	private readonly Dictionary<CpuType, TaskCompletionSource<CapturedBreakEvent>> _breakWaiters = [];
	private BreakEvent? _latestBreakEvent;
	private long? _latestBreakStableBreakpointId;
	private long _latestBreakGeneration = -1;
	private TraceConfiguration? _traceConfiguration;
	private CpuType? _traceCpu;
	private CpuType? _traceReconciliationCpu;
	private int _traceReconciliationVersion;
	private bool _traceReconciliationPending;
	private int _breakpointReconciliationVersion;
	private bool _breakpointReconciliationPending;
	private long _nextBreakpointId;
	private IDisposable? _debuggerLease;
	private bool _serviceShutdownStarted;
	private bool _debuggerCleanupCompleted;
	private bool _breakpointCollectionDisposed;
	private bool _exclusiveInputCleanupCompleted;
	private bool _disposed;

	internal McpEmulatorService(
		IMcpEmulatorApi api,
		McpEmulatorGate? gate = null,
		IDebuggerLifetimeCoordinator? debuggerLifetime = null,
		IMcpBreakpointCollection? breakpointCollection = null,
		ITraceLoggerCoordinator? traceCoordinator = null,
		McpEmulatorIdentity? emulatorIdentity = null,
		McpExecutionCoordinator? executionCoordinator = null)
	{
		_api = api;
		_executionCoordinator = executionCoordinator ?? gate?.ExecutionCoordinator ?? new();
		if(gate != null && executionCoordinator != null && !ReferenceEquals(gate.ExecutionCoordinator, executionCoordinator)) {
			throw new ArgumentException("The gate and service must share one execution coordinator.", nameof(executionCoordinator));
		}
		_gate = gate ?? new McpEmulatorGate(api, _executionCoordinator);
		_emulatorIdentity = emulatorIdentity ?? new();
		_debuggerLifetime = debuggerLifetime ?? DebuggerLifetimeCoordinator.Shared;
		_breakpointCollection = breakpointCollection ?? new McpBreakpointCollection();
		_traceCoordinator = traceCoordinator ?? TraceLoggerCoordinator.Shared;
	}

	internal IMcpEmulatorApi Api => _api;
	internal McpEmulatorIdentity EmulatorIdentity => _emulatorIdentity;
	internal McpExecutionCoordinator ExecutionCoordinator => _executionCoordinator;

	public McpServiceResult<EmulatorStatus> GetStatus()
	{
		return Execute(() => {
			if(!_api.IsRunning()) {
				return McpServiceResult<EmulatorStatus>.Success(new(false, null, null, "stopped", MesenVersion, McpVersion));
			}

			bool quarantined = _executionCoordinator.IsQuarantined;
			bool paused = !quarantined && _api.IsPaused();
			RomInfo romInfo = _api.GetRomInfo();
			return McpServiceResult<EmulatorStatus>.Success(new(
				true,
				romInfo.ConsoleType.ToString(),
				romInfo.GetRomName(),
				quarantined ? EmulatorStatus.Unknown : paused ? "paused" : "running",
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

		return ExecuteWithDebuggerLease((_, prepareDebuggerLease) => {
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

			McpServiceResult<bool> lease = prepareDebuggerLease();
			if(!lease.IsSuccess) {
				return FailureFrom<McpBreakpoint>(lease);
			}
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
		return ExecuteWithDebuggerLease((_, prepareDebuggerLease) => {
			if(!_api.IsRunning()) {
				return McpServiceResult<BreakpointRemovalSummary>.Failure("no_game", "No game is currently loaded.");
			}

			McpServiceResult<bool> lease = prepareDebuggerLease();
			if(!lease.IsSuccess) {
				return FailureFrom<BreakpointRemovalSummary>(lease);
			}
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

		return ExecuteWithTicket(ticket => {
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

		return ExecuteWithTicket(ticket => {
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
			long? stableBreakpointId;
			long generation;
			lock(_executionLock) {
				if(!_api.IsExecutionStopped()
					|| !_latestBreakEvent.HasValue
					|| _latestBreakGeneration != _gate.Generation) {
					return McpServiceResult<BreakContext>.Failure("stale_context", "No current stopped break context is available.");
				}
				breakEvent = _latestBreakEvent.Value;
				stableBreakpointId = _latestBreakStableBreakpointId;
				generation = _latestBreakGeneration;
			}
			return BuildBreakContext(breakEvent, stableBreakpointId, generation, before, after, maxStackDepth);
		});
	}

	internal void ProcessNotification(NotificationEventArgs e)
	{
		BreakEvent? copiedBreak = null;
		if(e.NotificationType == ConsoleNotificationType.CodeBreak && e.Parameter != IntPtr.Zero) {
			copiedBreak = Marshal.PtrToStructure<BreakEvent>(e.Parameter);
		}

		switch(e.NotificationType) {
			case ConsoleNotificationType.CodeBreak:
				if(copiedBreak.HasValue) {
					RecordCodeBreak(copiedBreak.Value);
				}
				_executionCoordinator.NotifyLifecycleRecovery();
				break;
			case ConsoleNotificationType.GamePaused:
				_executionCoordinator.NotifyLifecycleRecovery();
				break;
			case ConsoleNotificationType.GameResumed:
			case ConsoleNotificationType.DebuggerResumed:
				InvalidateLatestBreak();
				break;
			case ConsoleNotificationType.StateLoaded:
			case ConsoleNotificationType.GameReset:
				_emulatorIdentity.NotifyMutableStateChanged();
				_executionCoordinator.NotifyLifecycleRecovery();
				InvalidateGenerationResources(_gate.NotifyEmulatorStateChanged);
				break;
			case ConsoleNotificationType.BeforeGameLoad:
			case ConsoleNotificationType.BeforeGameUnload:
			case ConsoleNotificationType.BeforeEmulationStop:
				InvalidateGenerationResources(_gate.BeginEmulatorTransition);
				break;
			case ConsoleNotificationType.GameLoaded:
			case ConsoleNotificationType.GameLoadFailed:
			case ConsoleNotificationType.EmulationStopped:
				_emulatorIdentity.NotifyRomChanged();
				_executionCoordinator.NotifyLifecycleRecovery();
				InvalidateGenerationResources(_gate.EndEmulatorTransition);
				break;
		}
	}

	internal McpServiceResult<TraceConfiguration> ConfigureExecutionTrace(
		string cpu,
		string action,
		bool indentCode,
		bool useLabels,
		string? condition,
		string? format)
	{
		if(action is not ("enable" or "configure" or "clear" or "disable")) {
			return McpServiceResult<TraceConfiguration>.Failure(
				"invalid_action",
				"Trace action must be enable, configure, clear, or disable."
			);
		}
		if(action is "enable" or "configure") {
			if(Encoding.UTF8.GetByteCount(condition ?? "") > McpDebuggerLimits.MaxConditionUtf8Bytes) {
				return McpServiceResult<TraceConfiguration>.Failure(
					"payload_too_large",
					$"Trace conditions cannot exceed {McpDebuggerLimits.MaxConditionUtf8Bytes} UTF-8 bytes."
				);
			}
			if(Encoding.UTF8.GetByteCount(format ?? "") > McpDebuggerLimits.MaxTraceFormatUtf8Bytes) {
				return McpServiceResult<TraceConfiguration>.Failure(
					"payload_too_large",
					$"Trace formats cannot exceed {McpDebuggerLimits.MaxTraceFormatUtf8Bytes} UTF-8 bytes."
				);
			}
		}
		if(!TryParseExactEnum(cpu, out CpuType cpuType)) {
			return McpServiceResult<TraceConfiguration>.Failure("unknown_cpu", "The selected CPU is not available.");
		}

		return ExecuteWithDebuggerLease((ticket, prepareDebuggerLease) => {
			if(!_api.IsRunning()) {
				return McpServiceResult<TraceConfiguration>.Failure("no_game", "No game is currently loaded.");
			}
			if(!_api.GetRomInfo().CpuTypes.Contains(cpuType)) {
				return McpServiceResult<TraceConfiguration>.Failure("unknown_cpu", "The selected CPU is not available.");
			}

			TraceConfiguration? owned;
			CpuType? ownedCpu;
			lock(_executionLock) {
				owned = _traceConfiguration;
				ownedCpu = _traceCpu;
			}
			if(action is "clear" or "disable") {
				if(owned is null || ownedCpu != cpuType) {
					return McpServiceResult<TraceConfiguration>.Failure("trace_not_owned", "The execution trace is not owned by MCP for this CPU.");
				}
				if(action == "clear") {
					if(!_traceCoordinator.TryExecute(_traceOwner, _api.ClearExecutionTrace)) {
						return TraceOwnershipConflict<TraceConfiguration>();
					}
					return McpServiceResult<TraceConfiguration>.Success(owned);
				}

				if(!_traceCoordinator.TryReleaseAndExecute(_traceOwner, () => {
					_api.SetTraceOptions(cpuType, DisabledTraceOptions());
					_api.ClearExecutionTrace();
				})) {
					return TraceOwnershipConflict<TraceConfiguration>();
				}
				TraceConfiguration disabled = owned with { Enabled = false, Generation = ticket.Generation };
				lock(_executionLock) {
					_traceConfiguration = null;
					_traceCpu = null;
				}
				return McpServiceResult<TraceConfiguration>.Success(disabled);
			}

			InteropTraceLoggerOptions options = new() {
				Enabled = true,
				IndentCode = indentCode,
				UseLabels = useLabels,
				Condition = EncodeTraceText(condition),
				Format = EncodeTraceText(format)
			};
			McpServiceResult<bool> lease = prepareDebuggerLease();
			if(!lease.IsSuccess) {
				return FailureFrom<TraceConfiguration>(lease);
			}
			if(!_traceCoordinator.TryAcquireAndExecute(_traceOwner, () => {
				_api.SetTraceOptions(cpuType, options);
			})) {
				return TraceOwnershipConflict<TraceConfiguration>();
			}
			TraceConfiguration configured = new(cpu, true, indentCode, useLabels, condition, format, ticket.Generation);
			lock(_executionLock) {
				if(_gate.Generation == ticket.Generation) {
					_traceConfiguration = configured;
					_traceCpu = cpuType;
				} else {
					_traceReconciliationCpu = cpuType;
					_traceReconciliationVersion++;
					_traceReconciliationPending = true;
				}
			}
			return McpServiceResult<TraceConfiguration>.Success(configured);
		});
	}

	internal McpServiceResult<ExecutionTraceResult> GetExecutionTrace(int maxRows)
	{
		if(maxRows > McpDebuggerLimits.MaxTraceRows) {
			return McpServiceResult<ExecutionTraceResult>.Failure(
				"payload_too_large",
				$"Trace row count cannot exceed {McpDebuggerLimits.MaxTraceRows}."
			);
		}
		if(maxRows < 1) {
			return McpServiceResult<ExecutionTraceResult>.Failure("invalid_range", "Trace row count must be at least one.");
		}

		return ExecuteWithTicket(ticket => {
			lock(_executionLock) {
				if(_traceConfiguration is null) {
					return McpServiceResult<ExecutionTraceResult>.Failure("trace_not_owned", "The execution trace is not owned by MCP.");
				}
			}

			uint available = _api.GetExecutionTraceSize();
			uint count = Math.Min(available, (uint)maxRows);
			uint startOffset = available - count;
			TraceRow[] nativeRows = count == 0 ? [] : _api.GetExecutionTrace(startOffset, count);
			int returnedCount = Math.Min(nativeRows.Length, (int)count);
			List<ExecutionTraceRow> rows = new(returnedCount);
			for(int i = 0; i < returnedCount; i++) {
				TraceRow row = nativeRows[i];
				rows.Add(new(
					row.Type.ToString(),
					row.ProgramCounter,
					row.GetByteCodeStr().Replace(" ", "", StringComparison.Ordinal),
					row.GetOutput()
				));
			}
			return McpServiceResult<ExecutionTraceResult>.Success(new(
				ticket.Generation,
				checked((int)available),
				rows,
				available > count
			));
		});
	}

	internal McpServiceResult<ExecutionState> Pause()
	{
		return ExecuteMutationWithTicket(McpExecutionMutation.Pause, ticket => {
			if(!_api.IsRunning()) {
				return McpServiceResult<ExecutionState>.Failure("no_game", "No game is currently loaded.");
			}
			_api.Pause();
			if(_api.IsExecutionStopped()) {
				_executionCoordinator.ConfirmStoppedAndClearQuarantine();
			}
			return McpServiceResult<ExecutionState>.Success(new("paused", ticket.Generation));
		});
	}

	internal McpServiceResult<ExecutionState> Resume()
	{
		return ExecuteMutationWithTicket(McpExecutionMutation.Resume, ticket => {
			if(!_api.IsRunning()) {
				return McpServiceResult<ExecutionState>.Failure("no_game", "No game is currently loaded.");
			}
			if(HasActiveBreakWaiter()) {
				return McpServiceResult<ExecutionState>.Failure("operation_in_progress", "A continue operation is still in progress.");
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

		return ExecuteMutationWithDebuggerLease(McpExecutionMutation.Step, (ticket, prepareDebuggerLease) => {
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
			if(HasActiveBreakWaiter()) {
				return McpServiceResult<ExecutionState>.Failure("operation_in_progress", "A continue operation is still in progress.");
			}

			McpServiceResult<bool> lease = prepareDebuggerLease();
			if(!lease.IsSuccess) {
				return FailureFrom<ExecutionState>(lease);
			}
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
		McpServiceResult<McpExecutionLease> acquisition = await _executionCoordinator
			.TryAcquireAsync(cancellationToken)
			.ConfigureAwait(false);
		if(!acquisition.IsSuccess) {
			return McpServiceResult<ContinueResult>.Failure(acquisition.Error!.Code, acquisition.Error.Message);
		}
		await using McpExecutionLease executionLease = acquisition.Value!;

		McpOperationTicket ticket = default;
		CpuType cpuType = default;
		TaskCompletionSource<CapturedBreakEvent>? waiter = null;
		try {
			McpServiceResult<ExecutionState> setup = ExecuteOwnedWithDebuggerLease(
				executionLease.LeaseId,
				(currentTicket, prepareDebuggerLease) => {
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
						if(_breakWaiters.Count > 0) {
							return McpServiceResult<ExecutionState>.Failure("operation_in_progress", "A continue operation is already active.");
						}
						waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
						_breakWaiters.Add(cpuType, waiter);
						_latestBreakEvent = null;
						_latestBreakStableBreakpointId = null;
						_latestBreakGeneration = -1;
					}

					McpServiceResult<bool> lease = prepareDebuggerLease();
					if(!lease.IsSuccess) {
						return FailureFrom<ExecutionState>(lease);
					}
					ticket = currentTicket;
					if(_api.IsExecutionStopped()) {
						_api.ResumeDebugger();
					} else {
						_api.Resume();
					}
					return McpServiceResult<ExecutionState>.Success(new("running", ticket.Generation));
				});

			if(!setup.IsSuccess) {
				return McpServiceResult<ContinueResult>.Failure(setup.Error!.Code, setup.Error.Message);
			}

			using CancellationTokenSource timeoutCancellation = new(timeoutMs);
			using CancellationTokenSource linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
				cancellationToken,
				timeoutCancellation.Token,
				_gate.ShutdownToken
			);
			try {
				CapturedBreakEvent captured = await waiter!.Task.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
				return ExecuteOwnedForTicket(executionLease.LeaseId, ticket, () => {
					if(!_api.IsExecutionStopped()) {
						return McpServiceResult<ContinueResult>.Failure("stale_context", "Execution resumed before break context could be captured.");
					}
					return CreateContinueResult(captured, ticket.Generation);
				});
			} catch(OperationCanceledException) {
				if(IsServiceStopping()) {
					return McpServiceResult<ContinueResult>.Failure("server_stopping", "The MCP server is shutting down.");
				}
				if(_gate.Generation != ticket.Generation) {
					return McpServiceResult<ContinueResult>.Failure("state_changed", "Emulator state changed during the operation.");
				}
				if(cancellationToken.IsCancellationRequested) {
					return McpServiceResult<ContinueResult>.Failure("cancelled", "The continue operation was cancelled.");
				}
				return McpServiceResult<ContinueResult>.Failure("timeout", "The continue operation timed out.");
			}
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

	private McpServiceResult<ContinueResult> CreateContinueResult(CapturedBreakEvent captured, long generation)
	{
		McpServiceResult<BreakContext> context = BuildBreakContext(
			captured.Event,
			captured.StableBreakpointId,
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
		long? stableBreakpointId,
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
		long? breakpointId = breakEvent.Source == BreakSource.Breakpoint ? stableBreakpointId : null;
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

	private void RemoveWaiter(CpuType cpuType, TaskCompletionSource<CapturedBreakEvent>? waiter)
	{
		if(waiter is null) {
			return;
		}
		lock(_executionLock) {
			if(_breakWaiters.TryGetValue(cpuType, out TaskCompletionSource<CapturedBreakEvent>? current)
				&& ReferenceEquals(current, waiter)) {
				_breakWaiters.Remove(cpuType);
			}
		}
	}

	private void InvalidateLatestBreak()
	{
		lock(_executionLock) {
			_latestBreakEvent = null;
			_latestBreakStableBreakpointId = null;
			_latestBreakGeneration = -1;
		}
	}

	private void RecordCodeBreak(BreakEvent copied)
	{
		long? stableBreakpointId = null;
		if(copied.Source == BreakSource.Breakpoint
			&& _breakpointCollection.TryGetStableId(copied.BreakpointId, out long stableId)) {
			stableBreakpointId = stableId;
		}
		CapturedBreakEvent captured = new(copied, stableBreakpointId);
		TaskCompletionSource<CapturedBreakEvent>? waiter;
		lock(_executionLock) {
			if(_serviceShutdownStarted) {
				return;
			}
			_latestBreakEvent = copied;
			_latestBreakStableBreakpointId = stableBreakpointId;
			_latestBreakGeneration = _gate.Generation;
			_breakWaiters.TryGetValue(copied.SourceCpu, out waiter);
		}
		waiter?.TrySetResult(captured);
	}

	private void InvalidateGenerationResources(Action changeState)
	{
		TaskCompletionSource<CapturedBreakEvent>[] waiters;
		lock(_executionLock) {
			changeState();
			if(_traceCpu.HasValue) {
				_traceReconciliationCpu = _traceCpu;
				_traceReconciliationVersion++;
				_traceReconciliationPending = true;
				_traceConfiguration = null;
				_traceCpu = null;
			}
			_breakpointReconciliationVersion++;
			_breakpointReconciliationPending = true;
			waiters = [.. _breakWaiters.Values];
			_breakWaiters.Clear();
			_latestBreakEvent = null;
			_latestBreakStableBreakpointId = null;
			_latestBreakGeneration = -1;
		}
		foreach(TaskCompletionSource<CapturedBreakEvent> waiter in waiters) {
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

	private static McpServiceResult<T> FailureFrom<T>(McpServiceResult<bool> result)
	{
		return McpServiceResult<T>.Failure(result.Error!.Code, result.Error.Message);
	}

	private static byte[] EncodeTraceText(string? value)
	{
		byte[] buffer = new byte[1000];
		Encoding.UTF8.GetBytes(value ?? "", buffer);
		return buffer;
	}

	private static InteropTraceLoggerOptions DisabledTraceOptions() => new() {
		Enabled = false,
		Condition = new byte[1000],
		Format = new byte[1000]
	};

	private void ReconcilePendingTrace()
	{
		CpuType cpuType;
		int version;
		lock(_executionLock) {
			if(!_traceReconciliationPending || !_traceReconciliationCpu.HasValue) {
				return;
			}
			cpuType = _traceReconciliationCpu.Value;
			version = _traceReconciliationVersion;
		}

		if(_traceCoordinator.IsOwner(_traceOwner)) {
			try {
				_traceCoordinator.TryReleaseAndExecute(_traceOwner, () => {
					_api.SetTraceOptions(cpuType, DisabledTraceOptions());
					_api.ClearExecutionTrace();
				});
			} finally {
				_traceCoordinator.Release(_traceOwner);
			}
		}
		lock(_executionLock) {
			if(_traceReconciliationVersion == version) {
				_traceReconciliationPending = false;
				_traceReconciliationCpu = null;
			}
		}
	}

	private void ReconcilePendingBreakpoints()
	{
		int version;
		lock(_executionLock) {
			if(!_breakpointReconciliationPending) {
				return;
			}
			version = _breakpointReconciliationVersion;
		}

		if(_debuggerLease is not null) {
			_breakpointCollection.Replace([]);
			_debuggerLease.Dispose();
			_debuggerLease = null;
		}

		if(_api.IsRunning()) {
			RomInfo romInfo = _api.GetRomInfo();
			List<long> incompatible = [];
			foreach((long id, Breakpoint breakpoint) in _mcpBreakpoints) {
				int memorySize = _api.GetMemorySize(breakpoint.MemoryType);
				if(!romInfo.CpuTypes.Contains(breakpoint.CpuType)
					|| memorySize <= 0
					|| (breakpoint.BreakOnExec && !breakpoint.MemoryType.SupportsExecBreakpoints())
					|| breakpoint.StartAddress >= (uint)memorySize
					|| breakpoint.EndAddress >= (uint)memorySize) {
					incompatible.Add(id);
				}
			}
			foreach(long id in incompatible) {
				_mcpBreakpoints.Remove(id);
			}
			if(_mcpBreakpoints.Count > 0) {
				EnsureDebuggerLease();
				_breakpointCollection.Replace(GetExternalBreakpoints());
			}
		}

		lock(_executionLock) {
			if(_breakpointReconciliationVersion == version) {
				_breakpointReconciliationPending = false;
			}
		}
	}

	private static CpuRegister Register(string name, ulong value, int bits)
	{
		int digits = (bits + 3) / 4;
		return new(name, value, bits, value.ToString($"X{digits}"));
	}

	internal void BeginServiceShutdown()
	{
		_executionCoordinator.BeginShutdown();
		TaskCompletionSource<CapturedBreakEvent>[] waiters;
		lock(_executionLock) {
			if(_serviceShutdownStarted) {
				return;
			}
			_serviceShutdownStarted = true;
			waiters = [.. _breakWaiters.Values];
			_breakWaiters.Clear();
			_latestBreakEvent = null;
			_latestBreakStableBreakpointId = null;
			_latestBreakGeneration = -1;
		}
		foreach(TaskCompletionSource<CapturedBreakEvent> waiter in waiters) {
			waiter.TrySetCanceled();
		}
	}

	internal void CleanupDebuggerResources()
	{
		_gate.ExecuteTerminalCleanup(() => {
			lock(_executionLock) {
				if(_debuggerCleanupCompleted) {
					return McpServiceResult<bool>.Success(true);
				}
			}

			try {
				ReconcilePendingTrace();
				CpuType? traceCpu;
				lock(_executionLock) {
					traceCpu = _traceCpu;
				}
				if(traceCpu.HasValue && _traceCoordinator.IsOwner(_traceOwner)) {
					_traceCoordinator.TryReleaseAndExecute(_traceOwner, () => {
						_api.SetTraceOptions(traceCpu.Value, DisabledTraceOptions());
						_api.ClearExecutionTrace();
					});
					lock(_executionLock) {
						_traceConfiguration = null;
						_traceCpu = null;
					}
				}
				if(_debuggerLease is not null || _mcpBreakpoints.Count > 0) {
					_breakpointCollection.Replace([]);
				}
				_mcpBreakpoints.Clear();
			} catch(Exception) {
				// Continue through session disposal and lease release after partial native cleanup.
			} finally {
				_traceCoordinator.Release(_traceOwner);
				try {
					if(!_breakpointCollectionDisposed) {
						_breakpointCollection.Dispose();
						_breakpointCollectionDisposed = true;
					}
				} finally {
					try {
						_debuggerLease?.Dispose();
					} finally {
						_debuggerLease = null;
						_mcpBreakpoints.Clear();
						lock(_executionLock) {
							_traceConfiguration = null;
							_traceCpu = null;
							_debuggerCleanupCompleted = true;
						}
					}
				}
			}
			return McpServiceResult<bool>.Success(true);
		});
	}

	internal void DrainOperations()
	{
		_gate.DrainOperations();
	}

	internal McpServiceResult<T> ExecuteAutomation<T>(
		Func<IMcpEmulatorApi, McpStateIdentity, McpServiceResult<T>> operation)
	{
		return Execute(() => operation(_api, _emulatorIdentity.Current));
	}

	internal McpServiceResult<T> ExecuteOwnedStateLoad<T>(
		long leaseId,
		Func<IMcpEmulatorApi, McpStateIdentity, McpServiceResult<T>> operation)
	{
		if(IsServiceStopping()) {
			return ServiceStopping<T>();
		}
		return _gate.ExecuteOwnedStateLoad(leaseId, _ => {
			if(IsServiceStopping()) {
				return ServiceStopping<T>();
			}
			ReconcilePendingResources();
			McpServiceResult<T> result = operation(_api, _emulatorIdentity.Current);
			ReconcilePendingResources();
			return result;
		});
	}

	internal void CleanupExclusiveInput()
	{
		_gate.ExecuteTerminalCleanup(() => {
			lock(_executionLock) {
				if(_exclusiveInputCleanupCompleted) {
					return McpServiceResult<bool>.Success(true);
				}
				_exclusiveInputCleanupCompleted = true;
			}
			_api.ClearExclusiveControllerOverrides();
			return McpServiceResult<bool>.Success(true);
		});
	}

	private McpServiceResult<T> Execute<T>(Func<McpServiceResult<T>> operation)
	{
		if(IsServiceStopping()) {
			return ServiceStopping<T>();
		}
		return _gate.Execute(() => {
			if(IsServiceStopping()) {
				return ServiceStopping<T>();
			}
			ReconcilePendingResources();
			return operation();
		});
	}

	private McpServiceResult<T> ExecuteWithTicket<T>(Func<McpOperationTicket, McpServiceResult<T>> operation)
	{
		if(IsServiceStopping()) {
			return ServiceStopping<T>();
		}
		return _gate.ExecuteWithTicket(ticket => {
			if(IsServiceStopping()) {
				return ServiceStopping<T>();
			}
			ReconcilePendingResources();
			return operation(ticket);
		});
	}

	private McpServiceResult<T> ExecuteMutationWithTicket<T>(
		McpExecutionMutation mutation,
		Func<McpOperationTicket, McpServiceResult<T>> operation)
	{
		if(IsServiceStopping()) {
			return ServiceStopping<T>();
		}
		return _gate.ExecuteMutationWithTicket(mutation, ticket => {
			if(IsServiceStopping()) {
				return ServiceStopping<T>();
			}
			ReconcilePendingResources();
			return operation(ticket);
		});
	}

	private McpServiceResult<T> ExecuteWithDebuggerLease<T>(
		Func<McpOperationTicket, Func<McpServiceResult<bool>>, McpServiceResult<T>> operation)
	{
		if(IsServiceStopping()) {
			return ServiceStopping<T>();
		}
		return _gate.ExecuteWithDebuggerLease(EnsureDebuggerLease, (ticket, prepareDebuggerLease) => {
			if(IsServiceStopping()) {
				return ServiceStopping<T>();
			}
			ReconcilePendingResources();
			return operation(ticket, prepareDebuggerLease);
		});
	}

	private McpServiceResult<T> ExecuteMutationWithDebuggerLease<T>(
		McpExecutionMutation mutation,
		Func<McpOperationTicket, Func<McpServiceResult<bool>>, McpServiceResult<T>> operation)
	{
		if(IsServiceStopping()) {
			return ServiceStopping<T>();
		}
		return _gate.ExecuteMutationWithDebuggerLease(mutation, EnsureDebuggerLease, (ticket, prepareDebuggerLease) => {
			if(IsServiceStopping()) {
				return ServiceStopping<T>();
			}
			ReconcilePendingResources();
			return operation(ticket, prepareDebuggerLease);
		});
	}

	private McpServiceResult<T> ExecuteOwnedWithDebuggerLease<T>(
		long leaseId,
		Func<McpOperationTicket, Func<McpServiceResult<bool>>, McpServiceResult<T>> operation)
	{
		if(IsServiceStopping()) {
			return ServiceStopping<T>();
		}
		return _gate.ExecuteOwnedWithDebuggerLease(leaseId, EnsureDebuggerLease, (ticket, prepareDebuggerLease) => {
			if(IsServiceStopping()) {
				return ServiceStopping<T>();
			}
			ReconcilePendingResources();
			return operation(ticket, prepareDebuggerLease);
		});
	}

	private McpServiceResult<T> ExecuteForTicket<T>(McpOperationTicket ticket, Func<McpServiceResult<T>> operation)
	{
		return _gate.ExecuteForTicket(ticket, () => {
			if(IsServiceStopping()) {
				return ServiceStopping<T>();
			}
			ReconcilePendingResources();
			return operation();
		});
	}

	private McpServiceResult<T> ExecuteOwnedForTicket<T>(
		long leaseId,
		McpOperationTicket ticket,
		Func<McpServiceResult<T>> operation)
	{
		return _gate.ExecuteOwnedForTicket(leaseId, ticket, () => {
			if(IsServiceStopping()) {
				return ServiceStopping<T>();
			}
			ReconcilePendingResources();
			return operation();
		});
	}

	private void ReconcilePendingResources()
	{
		ReconcilePendingTrace();
		ReconcilePendingBreakpoints();
	}

	private bool IsServiceStopping()
	{
		lock(_executionLock) {
			return _serviceShutdownStarted;
		}
	}

	private static McpServiceResult<T> ServiceStopping<T>() =>
		McpServiceResult<T>.Failure("server_stopping", "The MCP server is shutting down.");

	private bool HasActiveBreakWaiter()
	{
		lock(_executionLock) {
			return _breakWaiters.Count > 0;
		}
	}

	private static McpServiceResult<T> TraceOwnershipConflict<T>() =>
		McpServiceResult<T>.Failure("operation_in_progress", "The execution trace is in use by another debugger client.");

	private readonly record struct CapturedBreakEvent(BreakEvent Event, long? StableBreakpointId);

	public void Dispose()
	{
		lock(_executionLock) {
			if(_disposed) {
				return;
			}
			_disposed = true;
		}
		BeginServiceShutdown();
		CleanupDebuggerResources();
		_gate.BeginShutdown();
		_gate.DrainOperations();
	}
}
