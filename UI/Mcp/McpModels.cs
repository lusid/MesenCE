using System.Collections.Generic;

namespace Mesen.Mcp;

public sealed record EmulatorStatus(bool GameLoaded, string? System, string? RomName, string State, string MesenVersion, string McpVersion);
public sealed record MemorySpace(string Id, string DisplayName, int Size, bool CanRead, bool CanWrite);
public sealed record MemoryRead(string Space, uint Address, int Count, byte[] Data, string Hex);
public sealed record MemoryWrite(string Space, uint Address, int Count);
public sealed record CpuRegister(string Name, ulong Value, int Bits, string Hex);
public sealed record CpuRegisters(string System, string Cpu, string Architecture, IReadOnlyList<CpuRegister> Registers);
public sealed record McpServiceError(string Code, string Message);
public sealed record McpServiceResult<T>(T? Value, McpServiceError? Error)
{
	public bool IsSuccess => Error is null;
	public static McpServiceResult<T> Success(T value) => new(value, null);
	public static McpServiceResult<T> Failure(string code, string message) => new(default, new(code, message));
}
