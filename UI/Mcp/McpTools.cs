using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Mesen.Mcp;

public sealed record ReadMemoryRequest(string Space, uint Address, int Count);
public sealed record WriteMemoryRequest(string Space, uint Address, int[] Data);
public sealed record SetBreakpointRequest(string Cpu, string Space, string Access, uint StartAddress, uint? EndAddress, string? Condition);
public sealed record RemoveBreakpointRequest(long Id);
public sealed record StepRequest(string Cpu, string StepType);
public sealed record ContinueUntilBreakRequest(string Cpu, int TimeoutMs);
public sealed record BreakContextRequest(int Before, int After, int MaxStackDepth);
public sealed record DisassembleRequest(string Cpu, string? Space, uint? Address, int Before, int After);
public sealed record MapAddressRequest(string Cpu, string Space, uint Address);
public sealed record CallStackRequest(string Cpu, int MaxDepth);
public sealed record ConfigureExecutionTraceRequest(string Cpu, string Action, bool IndentCode, bool UseLabels, string? Condition, string? Format);
public sealed record GetExecutionTraceRequest(int MaxRows);
public sealed record McpMemoryRead(string Space, uint Address, int Count, int[] Data, string Hex);

internal sealed class McpTools
{
	private readonly McpEmulatorService _service;
	private readonly Action<string> _log;

	private McpTools(McpEmulatorService service, Action<string> log)
	{
		_service = service;
		_log = log;
	}

	internal static IReadOnlyList<McpServerTool> Create(McpEmulatorService service, Action<string>? log = null)
	{
		McpTools tools = new(service, log ?? McpServer.Log);
		JsonSerializerOptions serializerOptions = new(McpJsonUtilities.DefaultOptions);
		serializerOptions.TypeInfoResolverChain.Insert(0, McpToolJsonContext.Default);
		McpServerTool readTool = CreateTool(
			new Func<ReadMemoryRequest, CallToolResult>(tools.ReadMemory),
			"read_memory",
			"Reads live emulator memory without pausing emulation.",
			serializerOptions,
			readOnly: true
		);
		AddReadSchemaLimits(readTool);
		McpServerTool writeTool = CreateTool(
			new Func<WriteMemoryRequest, CallToolResult>(tools.WriteMemory),
			"write_memory",
			"Writes live emulator memory without pausing emulation and modifies emulator state immediately.",
			serializerOptions,
			readOnly: false
		);
		AddWriteSchemaLimits(writeTool);

		IReadOnlyList<McpServerTool> result = [
			CreateTool(
				new Func<CallToolResult>(tools.GetEmulatorStatus),
				"get_emulator_status",
				"Gets live emulator status without pausing emulation.",
				serializerOptions,
				readOnly: true
			),
			CreateTool(
				new Func<CallToolResult>(tools.ListMemorySpaces),
				"list_memory_spaces",
				"Lists live emulator memory spaces without pausing emulation.",
				serializerOptions,
				readOnly: true
			),
			readTool,
			writeTool,
			CreateTool(
				new Func<CallToolResult>(tools.GetCpuRegisters),
				"get_cpu_registers",
				"Gets live CPU registers without pausing emulation.",
				serializerOptions,
				readOnly: true
			),
			CreateTool(
				new Func<SetBreakpointRequest, CallToolResult>(tools.SetBreakpoint),
				"set_breakpoint",
				$"Creates an MCP-owned breakpoint against live emulator state without pausing. At most {McpDebuggerLimits.MaxMcpBreakpoints} MCP breakpoints are owned; conditions are limited to {McpDebuggerLimits.MaxConditionUtf8Bytes} UTF-8 bytes. Changes are immediate.",
				serializerOptions,
				readOnly: false
			),
			CreateTool(
				new Func<CallToolResult>(tools.ListBreakpoints),
				"list_breakpoints",
				$"Lists up to {McpDebuggerLimits.MaxMcpBreakpoints} MCP-owned breakpoints from live emulator state without pausing.",
				serializerOptions,
				readOnly: true
			),
			CreateTool(
				new Func<RemoveBreakpointRequest, CallToolResult>(tools.RemoveBreakpoint),
				"remove_breakpoint",
				"Removes one MCP-owned breakpoint from live emulator state without pausing. Changes are immediate.",
				serializerOptions,
				readOnly: false
			),
			CreateTool(
				new Func<CallToolResult>(tools.RemoveAllBreakpoints),
				"remove_all_breakpoints",
				$"Removes all, and only, the up to {McpDebuggerLimits.MaxMcpBreakpoints} MCP-owned breakpoints from live emulator state without pausing. Changes are immediate.",
				serializerOptions,
				readOnly: false
			),
			CreateTool(
				new Func<CallToolResult>(tools.Pause),
				"pause",
				"Pauses live emulator execution immediately; this changes execution state.",
				serializerOptions,
				readOnly: false
			),
			CreateTool(
				new Func<CallToolResult>(tools.Resume),
				"resume",
				"Resumes paused or debugger-stopped emulator execution immediately; this invalidates stopped break context.",
				serializerOptions,
				readOnly: false
			),
			CreateTool(
				new Func<StepRequest, CallToolResult>(tools.Step),
				"step",
				"Starts one supported step on debugger-stopped execution; it is unavailable while execution is live and invalidates stopped break context.",
				serializerOptions,
				readOnly: false
			),
			CreateTool(
				new Func<ContinueUntilBreakRequest, CancellationToken, Task<CallToolResult>>(tools.ContinueUntilBreak),
				"continue_until_break",
				$"Resumes paused or debugger-stopped execution and waits for the next break for {McpDebuggerLimits.MinContinueTimeoutMs} through {McpDebuggerLimits.MaxContinueTimeoutMs} milliseconds; cancellation stops only the wait.",
				serializerOptions,
				readOnly: false
			),
			CreateTool(
				new Func<BreakContextRequest, CallToolResult>(tools.GetBreakContext),
				"get_break_context",
				$"Gets the current debugger-stopped break context; unavailable for live or stale execution. Disassembly is limited to {McpDebuggerLimits.MaxDisassemblyRows} total rows and stack depth to {McpDebuggerLimits.MaxCallStackDepth}.",
				serializerOptions,
				readOnly: true
			),
			CreateTool(
				new Func<DisassembleRequest, CallToolResult>(tools.Disassemble),
				"disassemble",
				$"Disassembles live or debugger-stopped emulator state without changing execution, with at most {McpDebuggerLimits.MaxDisassemblyRows} total rows.",
				serializerOptions,
				readOnly: true
			),
			CreateTool(
				new Func<MapAddressRequest, CallToolResult>(tools.MapAddress),
				"map_address",
				"Maps one address using current live or debugger-stopped emulator state without changing execution.",
				serializerOptions,
				readOnly: true
			),
			CreateTool(
				new Func<CallStackRequest, CallToolResult>(tools.GetCallStack),
				"get_call_stack",
				$"Gets a live or debugger-stopped call stack without changing execution, limited to {McpDebuggerLimits.MaxCallStackDepth} frames.",
				serializerOptions,
				readOnly: true
			),
			CreateTool(
				new Func<ConfigureExecutionTraceRequest, CallToolResult>(tools.ConfigureExecutionTrace),
				"configure_execution_trace",
				$"Enables, configures, clears, or disables the MCP-owned execution trace against live or stopped state. Condition and format are each limited to {McpDebuggerLimits.MaxConditionUtf8Bytes} UTF-8 bytes; changes are immediate.",
				serializerOptions,
				readOnly: false
			),
			CreateTool(
				new Func<GetExecutionTraceRequest, CallToolResult>(tools.GetExecutionTrace),
				"get_execution_trace",
				$"Gets up to {McpDebuggerLimits.MaxTraceRows} rows from the MCP-owned execution trace while execution is live or stopped without changing state.",
				serializerOptions,
				readOnly: true
			)
		];
		ApplyDebuggerSchemaLimits(result);
		return result;
	}

	private CallToolResult GetEmulatorStatus()
	{
		return Execute("get_emulator_status", () => ToResult(_service.GetStatus(), McpToolJsonContext.Default.EmulatorStatus));
	}

	private CallToolResult ListMemorySpaces()
	{
		return Execute("list_memory_spaces", () => ToResult(_service.ListMemorySpaces(), McpToolJsonContext.Default.IReadOnlyListMemorySpace));
	}

	private CallToolResult ReadMemory(ReadMemoryRequest request)
	{
		return Execute("read_memory", () => {
			McpServiceResult<MemoryRead> result = _service.ReadMemory(request.Space, request.Address, request.Count);
			McpServiceResult<McpMemoryRead> protocolResult = result.Error is not null
				? new(null, result.Error)
				: McpServiceResult<McpMemoryRead>.Success(new(
					result.Value!.Space,
					result.Value.Address,
					result.Value.Count,
					Array.ConvertAll(result.Value.Data, value => (int)value),
					result.Value.Hex
				));
			return ToResult(protocolResult, McpToolJsonContext.Default.McpMemoryRead);
		});
	}

	private CallToolResult WriteMemory(WriteMemoryRequest request)
	{
		return Execute("write_memory", () => {
			if(request.Data is null) {
				return InvalidByteValue();
			}

			if(request.Data.Length > McpEmulatorService.MaxTransferSize) {
				return ToResult(
					McpServiceResult<MemoryWrite>.Failure("payload_too_large", $"Write data cannot exceed {McpEmulatorService.MaxTransferSize} bytes."),
					McpToolJsonContext.Default.MemoryWrite
				);
			}

			foreach(int value in request.Data) {
				if(value is < 0 or > byte.MaxValue) {
					return InvalidByteValue();
				}
			}

			byte[] data = Array.ConvertAll(request.Data, value => (byte)value);
			return ToResult(_service.WriteMemory(request.Space, request.Address, data), McpToolJsonContext.Default.MemoryWrite);
		});
	}

	private static CallToolResult InvalidByteValue()
	{
		return ToResult(
			McpServiceResult<MemoryWrite>.Failure("invalid_byte_value", "Write data must contain only integer values from 0 through 255."),
			McpToolJsonContext.Default.MemoryWrite
		);
	}

	private CallToolResult GetCpuRegisters()
	{
		return Execute("get_cpu_registers", () => ToResult(_service.GetCpuRegisters(), McpToolJsonContext.Default.CpuRegisters));
	}

	private CallToolResult SetBreakpoint(SetBreakpointRequest request)
	{
		return Execute("set_breakpoint", () => ToResult(
			_service.SetBreakpoint(request.Cpu, request.Space, request.Access, request.StartAddress, request.EndAddress, request.Condition),
			McpToolJsonContext.Default.McpBreakpoint
		));
	}

	private CallToolResult ListBreakpoints()
	{
		return Execute("list_breakpoints", () => ToResult(
			_service.ListBreakpoints(),
			McpToolJsonContext.Default.IReadOnlyListMcpBreakpoint
		));
	}

	private CallToolResult RemoveBreakpoint(RemoveBreakpointRequest request)
	{
		return Execute("remove_breakpoint", () => ToResult(
			_service.RemoveBreakpoint(request.Id),
			McpToolJsonContext.Default.BreakpointRemoval
		));
	}

	private CallToolResult RemoveAllBreakpoints()
	{
		return Execute("remove_all_breakpoints", () => ToResult(
			_service.RemoveAllBreakpoints(),
			McpToolJsonContext.Default.BreakpointRemovalSummary
		));
	}

	private CallToolResult Pause()
	{
		return Execute("pause", () => ToResult(_service.Pause(), McpToolJsonContext.Default.ExecutionState));
	}

	private CallToolResult Resume()
	{
		return Execute("resume", () => ToResult(_service.Resume(), McpToolJsonContext.Default.ExecutionState));
	}

	private CallToolResult Step(StepRequest request)
	{
		return Execute("step", () => ToResult(
			_service.Step(request.Cpu, request.StepType),
			McpToolJsonContext.Default.ExecutionState
		));
	}

	private async Task<CallToolResult> ContinueUntilBreak(
		ContinueUntilBreakRequest request,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync("continue_until_break", async () =>
			ToResult(
				await _service.ContinueUntilBreakAsync(request.Cpu, request.TimeoutMs, cancellationToken).ConfigureAwait(false),
				McpToolJsonContext.Default.ContinueResult
			)
		).ConfigureAwait(false);
	}

	private CallToolResult GetBreakContext(BreakContextRequest request)
	{
		return Execute("get_break_context", () => ToResult(
			_service.GetBreakContext(request.Before, request.After, request.MaxStackDepth),
			McpToolJsonContext.Default.BreakContext
		));
	}

	private CallToolResult Disassemble(DisassembleRequest request)
	{
		return Execute("disassemble", () => ToResult(
			_service.Disassemble(request.Cpu, request.Space, request.Address, request.Before, request.After),
			McpToolJsonContext.Default.IReadOnlyListDisassemblyRow
		));
	}

	private CallToolResult MapAddress(MapAddressRequest request)
	{
		return Execute("map_address", () => ToResult(
			_service.MapAddress(request.Cpu, request.Space, request.Address),
			McpToolJsonContext.Default.AddressMapping
		));
	}

	private CallToolResult GetCallStack(CallStackRequest request)
	{
		return Execute("get_call_stack", () => ToResult(
			_service.GetCallStack(request.Cpu, request.MaxDepth),
			McpToolJsonContext.Default.CallStackResult
		));
	}

	private CallToolResult ConfigureExecutionTrace(ConfigureExecutionTraceRequest request)
	{
		return Execute("configure_execution_trace", () => ToResult(
			_service.ConfigureExecutionTrace(
				request.Cpu,
				request.Action,
				request.IndentCode,
				request.UseLabels,
				request.Condition,
				request.Format
			),
			McpToolJsonContext.Default.TraceConfiguration
		));
	}

	private CallToolResult GetExecutionTrace(GetExecutionTraceRequest request)
	{
		return Execute("get_execution_trace", () => ToResult(
			_service.GetExecutionTrace(request.MaxRows),
			McpToolJsonContext.Default.ExecutionTraceResult
		));
	}

	private static McpServerTool CreateTool(Delegate method, string name, string description, JsonSerializerOptions serializerOptions, bool readOnly)
	{
		return McpServerTool.Create(method, new McpServerToolCreateOptions {
			Name = name,
			Description = description,
			ReadOnly = readOnly,
			Destructive = !readOnly,
			Idempotent = readOnly,
			OpenWorld = false,
			SerializerOptions = serializerOptions
		});
	}

	private static void AddWriteSchemaLimits(McpServerTool tool)
	{
		JsonObject schema = JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText())!.AsObject();
		JsonObject dataSchema = schema["properties"]!["request"]!["properties"]!["data"]!.AsObject();
		dataSchema["maxItems"] = McpEmulatorService.MaxTransferSize;
		JsonObject itemSchema = dataSchema["items"]!.AsObject();
		itemSchema["minimum"] = 0;
		itemSchema["maximum"] = byte.MaxValue;
		using JsonDocument document = JsonDocument.Parse(schema.ToJsonString());
		tool.ProtocolTool.InputSchema = document.RootElement.Clone();
	}

	private static void AddReadSchemaLimits(McpServerTool tool)
	{
		JsonObject schema = JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText())!.AsObject();
		JsonObject countSchema = schema["properties"]!["request"]!["properties"]!["count"]!.AsObject();
		countSchema["minimum"] = 1;
		countSchema["maximum"] = McpEmulatorService.MaxTransferSize;
		using JsonDocument document = JsonDocument.Parse(schema.ToJsonString());
		tool.ProtocolTool.InputSchema = document.RootElement.Clone();
	}

	private static void ApplyDebuggerSchemaLimits(IReadOnlyList<McpServerTool> tools)
	{
		foreach(McpServerTool tool in tools) {
			JsonObject schema = JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText())!.AsObject();
			JsonObject? properties = schema["properties"]?["request"]?["properties"]?.AsObject();
			if(properties is null) {
				continue;
			}

			bool changed = true;
			switch(tool.ProtocolTool.Name) {
				case "set_breakpoint":
					properties["access"]!["enum"] = new JsonArray("execute", "read", "write");
					properties["condition"]!["maxLength"] = McpDebuggerLimits.MaxConditionUtf8Bytes;
					break;
				case "continue_until_break":
					properties["timeoutMs"]!["minimum"] = McpDebuggerLimits.MinContinueTimeoutMs;
					properties["timeoutMs"]!["maximum"] = McpDebuggerLimits.MaxContinueTimeoutMs;
					break;
				case "get_break_context":
					properties["before"]!["maximum"] = McpDebuggerLimits.MaxDisassemblyRows - 1;
					properties["after"]!["maximum"] = McpDebuggerLimits.MaxDisassemblyRows - 1;
					properties["maxStackDepth"]!["maximum"] = McpDebuggerLimits.MaxCallStackDepth;
					break;
				case "disassemble":
					properties["before"]!["maximum"] = McpDebuggerLimits.MaxDisassemblyRows - 1;
					properties["after"]!["maximum"] = McpDebuggerLimits.MaxDisassemblyRows - 1;
					break;
				case "get_call_stack":
					properties["maxDepth"]!["maximum"] = McpDebuggerLimits.MaxCallStackDepth;
					break;
				case "configure_execution_trace":
					properties["condition"]!["maxLength"] = McpDebuggerLimits.MaxConditionUtf8Bytes;
					properties["format"]!["maxLength"] = McpDebuggerLimits.MaxTraceFormatUtf8Bytes;
					break;
				case "get_execution_trace":
					properties["maxRows"]!["maximum"] = McpDebuggerLimits.MaxTraceRows;
					break;
				default:
					changed = false;
					break;
			}

			if(changed) {
				using JsonDocument document = JsonDocument.Parse(schema.ToJsonString());
				tool.ProtocolTool.InputSchema = document.RootElement.Clone();
			}
		}
	}

	private CallToolResult Execute(string toolName, Func<CallToolResult> operation)
	{
		long started = Stopwatch.GetTimestamp();
		try {
			CallToolResult result = operation();
			long elapsedMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
			if(result.IsError == true) {
				string code = result.StructuredContent?.GetProperty("code").GetString() ?? "unknown_error";
				_log($"[MCP] Tool {toolName} failed with {code} in {elapsedMilliseconds} ms.");
			} else {
				_log($"[MCP] Tool {toolName} succeeded in {elapsedMilliseconds} ms.");
			}
			return result;
		} catch(Exception) {
			long elapsedMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
			_log($"[MCP] Tool {toolName} failed with unhandled_error in {elapsedMilliseconds} ms.");
			throw;
		}
	}

	private async Task<CallToolResult> ExecuteAsync(string toolName, Func<Task<CallToolResult>> operation)
	{
		long started = Stopwatch.GetTimestamp();
		try {
			CallToolResult result = await operation().ConfigureAwait(false);
			long elapsedMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
			if(result.IsError == true) {
				string code = result.StructuredContent?.GetProperty("code").GetString() ?? "unknown_error";
				_log($"[MCP] Tool {toolName} failed with {code} in {elapsedMilliseconds} ms.");
			} else {
				_log($"[MCP] Tool {toolName} succeeded in {elapsedMilliseconds} ms.");
			}
			return result;
		} catch(Exception) {
			long elapsedMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
			_log($"[MCP] Tool {toolName} failed with unhandled_error in {elapsedMilliseconds} ms.");
			throw;
		}
	}

	private static CallToolResult ToResult<T>(McpServiceResult<T> result, JsonTypeInfo<T> typeInfo)
	{
		if(result.Error is not null) {
			JsonObject error = new() {
				["code"] = result.Error.Code,
				["message"] = result.Error.Message
			};
			return CreateResult(error, isError: true);
		}

		JsonNode value = JsonSerializer.SerializeToNode(result.Value!, typeInfo)
			?? throw new InvalidOperationException("The MCP service returned an empty success value.");
		return CreateResult(value, isError: false);
	}

	private static CallToolResult CreateResult(JsonNode payload, bool isError)
	{
		string json = payload.ToJsonString();
		using JsonDocument document = JsonDocument.Parse(json);
		return new CallToolResult {
			Content = [new TextContentBlock { Text = json }],
			StructuredContent = document.RootElement.Clone(),
			IsError = isError
		};
	}
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(EmulatorStatus))]
[JsonSerializable(typeof(IReadOnlyList<MemorySpace>))]
[JsonSerializable(typeof(McpMemoryRead))]
[JsonSerializable(typeof(MemoryWrite))]
[JsonSerializable(typeof(CpuRegisters))]
[JsonSerializable(typeof(ReadMemoryRequest))]
[JsonSerializable(typeof(WriteMemoryRequest))]
[JsonSerializable(typeof(SetBreakpointRequest))]
[JsonSerializable(typeof(RemoveBreakpointRequest))]
[JsonSerializable(typeof(StepRequest))]
[JsonSerializable(typeof(ContinueUntilBreakRequest))]
[JsonSerializable(typeof(BreakContextRequest))]
[JsonSerializable(typeof(DisassembleRequest))]
[JsonSerializable(typeof(MapAddressRequest))]
[JsonSerializable(typeof(CallStackRequest))]
[JsonSerializable(typeof(ConfigureExecutionTraceRequest))]
[JsonSerializable(typeof(GetExecutionTraceRequest))]
[JsonSerializable(typeof(McpBreakpoint))]
[JsonSerializable(typeof(IReadOnlyList<McpBreakpoint>))]
[JsonSerializable(typeof(BreakpointRemoval))]
[JsonSerializable(typeof(BreakpointRemovalSummary))]
[JsonSerializable(typeof(ExecutionState))]
[JsonSerializable(typeof(ContinueResult))]
[JsonSerializable(typeof(BreakContext))]
[JsonSerializable(typeof(McpMemoryOperation))]
[JsonSerializable(typeof(McpAddress))]
[JsonSerializable(typeof(DisassemblyRow))]
[JsonSerializable(typeof(IReadOnlyList<DisassemblyRow>))]
[JsonSerializable(typeof(CallStackFrame))]
[JsonSerializable(typeof(IReadOnlyList<CallStackFrame>))]
[JsonSerializable(typeof(CallStackResult))]
[JsonSerializable(typeof(AddressMapping))]
[JsonSerializable(typeof(TraceConfiguration))]
[JsonSerializable(typeof(ExecutionTraceRow))]
[JsonSerializable(typeof(IReadOnlyList<ExecutionTraceRow>))]
[JsonSerializable(typeof(ExecutionTraceResult))]
[JsonSerializable(typeof(LoadSaveStateRequest))]
[JsonSerializable(typeof(DeleteSaveStateRequest))]
[JsonSerializable(typeof(McpAutomationResourceLimits))]
[JsonSerializable(typeof(McpControllerControlCapability))]
[JsonSerializable(typeof(IReadOnlyList<McpControllerControlCapability>))]
[JsonSerializable(typeof(McpControllerCapability))]
[JsonSerializable(typeof(IReadOnlyList<McpControllerCapability>))]
[JsonSerializable(typeof(McpAutomationCapabilities))]
[JsonSerializable(typeof(McpSaveStateMetadata))]
[JsonSerializable(typeof(McpSaveStateLoadResult))]
[JsonSerializable(typeof(McpDeleteResourceResult))]
[JsonSerializable(typeof(McpScreenshotMetadata))]
[JsonSerializable(typeof(RunExperimentRequest))]
[JsonSerializable(typeof(McpInputSegment))]
[JsonSerializable(typeof(IReadOnlyList<McpInputSegment>))]
[JsonSerializable(typeof(McpControllerInput))]
[JsonSerializable(typeof(IReadOnlyList<McpControllerInput>))]
[JsonSerializable(typeof(McpMemoryObservationRequest))]
[JsonSerializable(typeof(IReadOnlyList<McpMemoryObservationRequest>))]
[JsonSerializable(typeof(McpDecodeRequest))]
[JsonSerializable(typeof(McpAssertionRequest))]
[JsonSerializable(typeof(IReadOnlyList<McpAssertionRequest>))]
[JsonSerializable(typeof(McpObservationResult))]
[JsonSerializable(typeof(IReadOnlyList<McpObservationResult>))]
[JsonSerializable(typeof(McpAssertionResult))]
[JsonSerializable(typeof(IReadOnlyList<McpAssertionResult>))]
[JsonSerializable(typeof(McpCheckpointResult))]
[JsonSerializable(typeof(IReadOnlyList<McpCheckpointResult>))]
[JsonSerializable(typeof(McpAssertionSummary))]
[JsonSerializable(typeof(McpExperimentInterruption))]
[JsonSerializable(typeof(McpExperimentCleanup))]
[JsonSerializable(typeof(RunExperimentResult))]
[JsonSerializable(typeof(CreateMemorySnapshotRequest))]
[JsonSerializable(typeof(CompareMemorySnapshotsRequest))]
[JsonSerializable(typeof(DeleteMemorySnapshotRequest))]
[JsonSerializable(typeof(StartMemorySearchRequest))]
[JsonSerializable(typeof(RefineMemorySearchRequest))]
[JsonSerializable(typeof(GetMemorySearchResultsRequest))]
[JsonSerializable(typeof(UndoMemorySearchRequest))]
[JsonSerializable(typeof(DeleteMemorySearchRequest))]
[JsonSerializable(typeof(McpMemorySnapshotMetadata))]
[JsonSerializable(typeof(CreateMemorySnapshotResult))]
[JsonSerializable(typeof(McpChangedMemoryRun))]
[JsonSerializable(typeof(IReadOnlyList<McpChangedMemoryRun>))]
[JsonSerializable(typeof(CompareMemorySnapshotsResult))]
[JsonSerializable(typeof(StartMemorySearchResult))]
[JsonSerializable(typeof(RefineMemorySearchResult))]
[JsonSerializable(typeof(McpMemorySearchCandidate))]
[JsonSerializable(typeof(IReadOnlyList<McpMemorySearchCandidate>))]
[JsonSerializable(typeof(GetMemorySearchResultsResult))]
[JsonSerializable(typeof(UndoMemorySearchResult))]
[JsonSerializable(typeof(IReadOnlyList<int>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
internal sealed partial class McpToolJsonContext : JsonSerializerContext
{
}
