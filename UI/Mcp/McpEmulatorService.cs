using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Mesen.Interop;

[assembly: InternalsVisibleTo("UI.Tests")]

namespace Mesen.Mcp;

internal sealed class McpEmulatorService
{
	public const int MaxTransferSize = 65536;
	private const string McpVersion = "1.0";
	private static readonly string MesenVersion = typeof(McpEmulatorService).Assembly.GetName().Version?.ToString(3) ?? "unknown";

	private readonly IMcpEmulatorApi _api;
	private readonly SemaphoreSlim _emulatorSemaphore = new(1, 1);
	private long _emulatorGeneration;
	private int _transitionActive;
	private int _shutdownStarted;

	public McpEmulatorService(IMcpEmulatorApi api)
	{
		_api = api;
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

	private bool TryResolveMemorySpace(string id, out MemoryType type, out int size)
	{
		if(!Enum.TryParse(id, ignoreCase: false, out type) || type == MemoryType.None || type.ToString() != id) {
			size = 0;
			return false;
		}

		size = _api.GetMemorySize(type);
		return size > 0;
	}

	private static CpuRegister Register(string name, ulong value, int bits)
	{
		int digits = (bits + 3) / 4;
		return new(name, value, bits, value.ToString($"X{digits}"));
	}

	internal void NotifyEmulatorStateChanged()
	{
		Interlocked.Increment(ref _emulatorGeneration);
	}

	internal void BeginEmulatorTransition()
	{
		Interlocked.Exchange(ref _transitionActive, 1);
		Interlocked.Increment(ref _emulatorGeneration);
	}

	internal void EndEmulatorTransition()
	{
		Interlocked.Increment(ref _emulatorGeneration);
		Interlocked.Exchange(ref _transitionActive, 0);
	}

	internal void BeginShutdown()
	{
		Interlocked.Exchange(ref _shutdownStarted, 1);
	}

	internal void DrainOperations()
	{
		_emulatorSemaphore.Wait();
		_emulatorSemaphore.Release();
	}

	private McpServiceResult<T> Execute<T>(Func<McpServiceResult<T>> operation)
	{
		_emulatorSemaphore.Wait();
		try {
			if(Volatile.Read(ref _shutdownStarted) != 0) {
				return McpServiceResult<T>.Failure("server_stopping", "The MCP server is shutting down.");
			}

			long generation = Volatile.Read(ref _emulatorGeneration);
			if(!TryGetDebuggerRequestBlockState(out ulong debuggerBlockState)) {
				return InteropFailure<T>();
			}
			if(Volatile.Read(ref _transitionActive) != 0 || IsDebuggerRequestBlocked(debuggerBlockState)) {
				return McpServiceResult<T>.Failure("state_changed", "Emulator state changed during the operation.");
			}
			if(generation != Volatile.Read(ref _emulatorGeneration)) {
				return McpServiceResult<T>.Failure("state_changed", "Emulator state changed during the operation.");
			}

			McpServiceResult<T> result;
			try {
				result = operation();
			} catch(Exception) {
				result = InteropFailure<T>();
			}

			if(!TryGetDebuggerRequestBlockState(out ulong endingDebuggerBlockState)) {
				return InteropFailure<T>();
			}
			if(generation != Volatile.Read(ref _emulatorGeneration)
				|| Volatile.Read(ref _transitionActive) != 0
				|| debuggerBlockState != endingDebuggerBlockState
				|| IsDebuggerRequestBlocked(endingDebuggerBlockState)) {
				return McpServiceResult<T>.Failure("state_changed", "Emulator state changed during the operation.");
			}
			return result;
		} finally {
			_emulatorSemaphore.Release();
		}
	}

	private bool TryGetDebuggerRequestBlockState(out ulong state)
	{
		try {
			state = _api.GetDebuggerRequestBlockState();
			return true;
		} catch(Exception) {
			state = 0;
			return false;
		}
	}

	private static McpServiceResult<T> InteropFailure<T>()
	{
		return McpServiceResult<T>.Failure("interop_failure", "Native emulator interop failed.");
	}

	private static bool IsDebuggerRequestBlocked(ulong state) => (state & 1) != 0;
}
