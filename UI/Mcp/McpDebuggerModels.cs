using System.Collections.Generic;

namespace Mesen.Mcp;

internal static class McpDebuggerLimits
{
	internal const int MaxMcpBreakpoints = 128;
	internal const int MaxDisassemblyRows = 256;
	internal const int MaxCallStackDepth = 128;
	internal const int MaxTraceRows = 1000;
	internal const int MaxConditionUtf8Bytes = 999;
	internal const int MaxTraceFormatUtf8Bytes = 999;
	internal const int MinContinueTimeoutMs = 1;
	internal const int MaxContinueTimeoutMs = 30000;
}

public sealed record McpBreakpoint(long Id, string Cpu, string Space, string Access, uint StartAddress, uint EndAddress, string? Condition, bool Enabled);
public sealed record BreakpointRemoval(long Id, bool Removed);
public sealed record BreakpointRemovalSummary(int RemovedCount);
public sealed record ExecutionState(string State, long Generation);
public sealed record ContinueResult(string Reason, BreakContext? Context, long Generation);
public sealed record McpMemoryOperation(string Space, uint Address, string Access, int Width, long Value);
public sealed record McpAddress(string Space, int Address);
public sealed record DisassemblyRow(uint CpuAddress, McpAddress? PhysicalAddress, string Bytes, string Text, string Comment, McpAddress? EffectiveAddress, uint? EffectiveValue, int? EffectiveValueWidth);
public sealed record CallStackFrame(uint Source, McpAddress SourcePhysical, uint Target, McpAddress TargetPhysical, uint Return, McpAddress ReturnPhysical, string Flags);
public sealed record CallStackResult(string Cpu, bool Live, long Generation, IReadOnlyList<CallStackFrame> Frames, bool Truncated);
public sealed record AddressMapping(string Cpu, McpAddress CpuRelative, McpAddress Physical, long Generation);
public sealed record BreakContext(string Reason, long? BreakpointId, string Cpu, McpMemoryOperation? Operation, CpuRegisters Registers, uint ProgramCounter, McpAddress? PhysicalProgramCounter, IReadOnlyList<DisassemblyRow> Disassembly, McpAddress? EffectiveAddress, uint? EffectiveValue, IReadOnlyList<CallStackFrame> CallStack, long Generation);
public sealed record TraceConfiguration(string Cpu, bool Enabled, bool IndentCode, bool UseLabels, string? Condition, string? Format, long Generation);
public sealed record ExecutionTraceRow(string Cpu, uint ProgramCounter, string Bytes, string Output);
public sealed record ExecutionTraceResult(long Generation, int AvailableRows, IReadOnlyList<ExecutionTraceRow> Rows, bool Truncated);
