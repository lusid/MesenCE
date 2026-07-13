using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
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

			NesCpuState state = _api.GetNesCpuState();
			return McpServiceResult<CpuRegisters>.Success(new(
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
			));
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
		_gate.NotifyEmulatorStateChanged();
	}

	internal void BeginEmulatorTransition()
	{
		_gate.BeginEmulatorTransition();
	}

	internal void EndEmulatorTransition()
	{
		_gate.EndEmulatorTransition();
	}

	internal void BeginShutdown()
	{
		_gate.BeginShutdown();
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
