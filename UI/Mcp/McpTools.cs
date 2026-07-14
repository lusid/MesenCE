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
	private const int ResourceIdLength = 32;
	private const string ResourceIdPattern = "^[0-9a-f]{32}$";
	private readonly McpEmulatorService _service;
	private readonly McpAutomationService _automation;
	private readonly McpExperimentService _experiments;
	private readonly McpMemorySnapshotService _snapshots;
	private readonly McpMemorySearchService _searches;
	private readonly Action<string> _log;

	private McpTools(
		McpEmulatorService service,
		McpAutomationService automation,
		McpExperimentService experiments,
		McpMemorySnapshotService snapshots,
		McpMemorySearchService searches,
		Action<string> log)
	{
		_service = service;
		_automation = automation;
		_experiments = experiments;
		_snapshots = snapshots;
		_searches = searches;
		_log = log;
	}

	internal static IReadOnlyList<McpServerTool> Create(
		McpEmulatorService service,
		McpAutomationService automation,
		McpExperimentService experiments,
		McpMemorySnapshotService snapshots,
		McpMemorySearchService searches,
		Action<string>? log = null)
	{
		McpTools tools = new(service, automation, experiments, snapshots, searches, log ?? McpServer.Log);
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
			),
			CreateTool(
				new Func<CallToolResult>(tools.GetAutomationCapabilities),
				"get_automation_capabilities",
				"Gets automation capabilities and bounded resource limits for the active system.",
				serializerOptions,
				readOnly: true
			),
			CreateTool(
				new Func<CallToolResult>(tools.CreateSaveState),
				"create_save_state",
				"Creates an MCP-owned in-memory save state.",
				serializerOptions,
				readOnly: false
			),
			CreateTool(
				new Func<LoadSaveStateRequest, CancellationToken, Task<CallToolResult>>(tools.LoadSaveState),
				"load_save_state",
				"Loads an MCP-owned save state and changes emulator execution state.",
				serializerOptions,
				readOnly: false
			),
			CreateTool(
				new Func<DeleteSaveStateRequest, CallToolResult>(tools.DeleteSaveState),
				"delete_save_state",
				"Deletes an MCP-owned save state.",
				serializerOptions,
				readOnly: false
			),
			CreateTool(
				new Func<CallToolResult>(tools.CaptureScreenshot),
				"capture_screenshot",
				"Captures the current decoded frame as bounded PNG image content.",
				serializerOptions,
				readOnly: true
			),
			CreateTool(
				new Func<RunExperimentRequest, CancellationToken, Task<CallToolResult>>(tools.RunExperiment),
				"run_experiment",
				"Runs one bounded deterministic input, observation, and assertion experiment.",
				serializerOptions,
				readOnly: false
			),
			CreateTool(
				new Func<CreateMemorySnapshotRequest, CallToolResult>(tools.CreateMemorySnapshot),
				"create_memory_snapshot",
				"Creates an MCP-owned memory snapshot from stopped execution.",
				serializerOptions,
				readOnly: false
			),
			CreateTool(
				new Func<CompareMemorySnapshotsRequest, CallToolResult>(tools.CompareMemorySnapshots),
				"compare_memory_snapshots",
				"Compares two compatible MCP-owned memory snapshots without changing resources.",
				serializerOptions,
				readOnly: true
			),
			CreateTool(
				new Func<DeleteMemorySnapshotRequest, CallToolResult>(tools.DeleteMemorySnapshot),
				"delete_memory_snapshot",
				"Deletes an MCP-owned memory snapshot.",
				serializerOptions,
				readOnly: false
			),
			CreateTool(
				new Func<StartMemorySearchRequest, CallToolResult>(tools.StartMemorySearch),
				"start_memory_search",
				"Creates a bounded MCP-owned memory search from stopped execution.",
				serializerOptions,
				readOnly: false
			),
			CreateTool(
				new Func<RefineMemorySearchRequest, CallToolResult>(tools.RefineMemorySearch),
				"refine_memory_search",
				"Refines an MCP-owned memory search against current stopped memory.",
				serializerOptions,
				readOnly: false
			),
			CreateTool(
				new Func<GetMemorySearchResultsRequest, CallToolResult>(tools.GetMemorySearchResults),
				"get_memory_search_results",
				"Gets one bounded page of MCP-owned memory search candidates.",
				serializerOptions,
				readOnly: true
			),
			CreateTool(
				new Func<UndoMemorySearchRequest, CallToolResult>(tools.UndoMemorySearch),
				"undo_memory_search",
				"Undoes the latest refinement of an MCP-owned memory search.",
				serializerOptions,
				readOnly: false
			),
			CreateTool(
				new Func<DeleteMemorySearchRequest, CallToolResult>(tools.DeleteMemorySearch),
				"delete_memory_search",
				"Deletes an MCP-owned memory search.",
				serializerOptions,
				readOnly: false
			)
		];
		ApplyDebuggerSchemaLimits(result);
		ApplyAutomationSchemaLimits(result);
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

	private CallToolResult GetAutomationCapabilities() => Execute(
		"get_automation_capabilities",
		() => ToResult(_automation.GetCapabilities(), McpToolJsonContext.Default.McpAutomationCapabilities));

	private CallToolResult CreateSaveState() => Execute(
		"create_save_state",
		() => ToResult(_automation.CreateSaveState(), McpToolJsonContext.Default.McpSaveStateMetadata));

	private async Task<CallToolResult> LoadSaveState(
		LoadSaveStateRequest request,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync("load_save_state", async () => ToResult(
			await _automation.LoadSaveStateAsync(request.Id, cancellationToken).ConfigureAwait(false),
			McpToolJsonContext.Default.McpSaveStateLoadResult)).ConfigureAwait(false);
	}

	private CallToolResult DeleteSaveState(DeleteSaveStateRequest request) => Execute(
		"delete_save_state",
		() => ToResult(_automation.DeleteSaveState(request.Id), McpToolJsonContext.Default.McpDeleteResourceResult));

	private CallToolResult CaptureScreenshot()
	{
		return Execute("capture_screenshot", () => {
			McpServiceResult<McpScreenshotCapture> result = _automation.CaptureScreenshot();
			if(!result.IsSuccess) {
				return ToErrorResult(result.Error!);
			}
			McpScreenshotCapture capture = result.Value!;
			return CreateImageResult(capture.Metadata, McpToolJsonContext.Default.McpScreenshotMetadata, capture.Png);
		});
	}

	private async Task<CallToolResult> RunExperiment(
		RunExperimentRequest request,
		CancellationToken cancellationToken)
	{
		return await ExecuteAsync("run_experiment", async () => {
			McpServiceResult<McpExperimentCapture> result = await _experiments
				.RunAsync(request, cancellationToken).ConfigureAwait(false);
			if(!result.IsSuccess) {
				return ToErrorResult(result.Error!);
			}
			McpExperimentCapture capture = result.Value!;
			return capture.Png is null
				? ToResult(McpServiceResult<RunExperimentResult>.Success(capture.Result), McpToolJsonContext.Default.RunExperimentResult)
				: CreateImageResult(capture.Result, McpToolJsonContext.Default.RunExperimentResult, capture.Png);
		}).ConfigureAwait(false);
	}

	private CallToolResult CreateMemorySnapshot(CreateMemorySnapshotRequest request) => Execute(
		"create_memory_snapshot",
		() => ToResult(
			_snapshots.CreateMemorySnapshot(request.Space, request.Address, request.Count),
			McpToolJsonContext.Default.CreateMemorySnapshotResult));

	private CallToolResult CompareMemorySnapshots(CompareMemorySnapshotsRequest request) => Execute(
		"compare_memory_snapshots",
		() => ToResult(
			_snapshots.CompareMemorySnapshots(
				request.FirstId, request.SecondId, request.Offset, request.Limit, request.SampleBytes),
			McpToolJsonContext.Default.CompareMemorySnapshotsResult));

	private CallToolResult DeleteMemorySnapshot(DeleteMemorySnapshotRequest request) => Execute(
		"delete_memory_snapshot",
		() => ToResult(
			_snapshots.DeleteMemorySnapshot(request.Id),
			McpToolJsonContext.Default.McpDeleteResourceResult));

	private CallToolResult StartMemorySearch(StartMemorySearchRequest request) => Execute(
		"start_memory_search",
		() => ToResult(
			_searches.StartMemorySearch(
				request.Space, request.Address, request.Count, request.Width, request.Signed,
				request.ByteOrder, request.Stride, request.InitialValue),
			McpToolJsonContext.Default.StartMemorySearchResult));

	private CallToolResult RefineMemorySearch(RefineMemorySearchRequest request) => Execute(
		"refine_memory_search",
		() => ToResult(
			_searches.RefineMemorySearch(request.Id, request.Comparison, request.Value, request.Delta),
			McpToolJsonContext.Default.RefineMemorySearchResult));

	private CallToolResult GetMemorySearchResults(GetMemorySearchResultsRequest request) => Execute(
		"get_memory_search_results",
		() => ToResult(
			_searches.GetMemorySearchResults(request.Id, request.Offset, request.Limit),
			McpToolJsonContext.Default.GetMemorySearchResultsResult));

	private CallToolResult UndoMemorySearch(UndoMemorySearchRequest request) => Execute(
		"undo_memory_search",
		() => ToResult(
			_searches.UndoMemorySearch(request.Id),
			McpToolJsonContext.Default.UndoMemorySearchResult));

	private CallToolResult DeleteMemorySearch(DeleteMemorySearchRequest request) => Execute(
		"delete_memory_search",
		() => ToResult(
			_searches.DeleteMemorySearch(request.Id),
			McpToolJsonContext.Default.McpDeleteResourceResult));

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

	private static void ApplyAutomationSchemaLimits(IReadOnlyList<McpServerTool> tools)
	{
		foreach(McpServerTool tool in tools) {
			JsonObject schema = JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText())!.AsObject();
			JsonObject? properties = schema["properties"]?["request"]?["properties"]?.AsObject();
			if(properties is null) {
				continue;
			}

			bool changed = true;
			switch(tool.ProtocolTool.Name) {
				case "load_save_state":
				case "delete_save_state":
				case "delete_memory_snapshot":
				case "undo_memory_search":
				case "delete_memory_search":
					SetResourceIdSchema(properties["id"]!);
					break;
				case "run_experiment": {
					properties["cpu"]!["enum"] = new JsonArray("Nes");
					SetResourceIdSchema(properties["saveStateId"]!);
					SetRange(properties["timeoutMs"]!, McpAutomationLimits.MinExperimentTimeoutMs, McpAutomationLimits.MaxExperimentTimeoutMs);
					JsonObject segments = properties["segments"]!.AsObject();
					segments["minItems"] = 1;
					segments["maxItems"] = McpAutomationLimits.MaxSegments;
					JsonObject segmentProperties = segments["items"]!["properties"]!.AsObject();
					SetRange(segmentProperties["frames"]!, 1, McpAutomationLimits.MaxExperimentFrames);
					JsonObject controllers = segmentProperties["controllers"]!.AsObject();
					controllers["maxItems"] = McpAutomationLimits.MaxControllersPerSegment;
					JsonObject controllerProperties = controllers["items"]!["properties"]!.AsObject();
					SetRange(
						controllerProperties["port"]!,
						McpAutomationLimits.MinPhysicalControllerPort,
						McpAutomationLimits.MaxPhysicalControllerPort);
					controllerProperties["buttons"]!["maxItems"] = byte.MaxValue + 1;

					JsonObject observations = properties["observations"]!.AsObject();
					observations["maxItems"] = McpAutomationLimits.MaxObservations;
					JsonObject observationProperties = observations["items"]!["properties"]!.AsObject();
					SetRange(observationProperties["count"]!, 1, McpAutomationLimits.MaxObservedBytes);
					JsonObject decodeProperties = observationProperties["decode"]!["properties"]!.AsObject();
					decodeProperties["width"]!["enum"] = new JsonArray(1, 2, 4);
					decodeProperties["byteOrder"]!["enum"] = new JsonArray("little", "big");

					JsonObject assertions = properties["assertions"]!.AsObject();
					assertions["maxItems"] = McpAutomationLimits.MaxAssertions;
					JsonObject assertionProperties = assertions["items"]!["properties"]!.AsObject();
					assertionProperties["operator"]!["enum"] = new JsonArray(
						"equal", "not_equal", "range", "masked_equal", "relative_equal",
						"relative_not_equal", "increased", "decreased", "changed", "unchanged");
					JsonObject expectedBytes = assertionProperties["expectedBytes"]!.AsObject();
					expectedBytes["maxItems"] = McpAutomationLimits.MaxObservedBytes;
					SetRange(expectedBytes["items"]!, byte.MinValue, byte.MaxValue);
					break;
				}
				case "create_memory_snapshot":
					SetRange(properties["count"]!, 1, McpAutomationLimits.MaxMemorySnapshotBytes);
					break;
				case "compare_memory_snapshots":
					SetResourceIdSchema(properties["firstId"]!);
					SetResourceIdSchema(properties["secondId"]!);
					SetPaging(properties);
					SetRange(properties["sampleBytes"]!, 0, McpAutomationLimits.MaxRunSampleBytes);
					break;
				case "start_memory_search":
					SetRange(properties["count"]!, 1, McpAutomationLimits.MaxSearchRangeBytes);
					properties["width"]!["enum"] = new JsonArray(1, 2, 4);
					properties["byteOrder"]!["enum"] = new JsonArray("little", "big");
					SetRange(properties["stride"]!, 1, 4);
					break;
				case "refine_memory_search":
					SetResourceIdSchema(properties["id"]!);
					properties["comparison"]!["enum"] = new JsonArray(
						"exact", "not_equal", "increased", "decreased", "changed", "unchanged",
						"increased_by", "decreased_by");
					properties["delta"]!["minimum"] = 1;
					break;
				case "get_memory_search_results":
					SetResourceIdSchema(properties["id"]!);
					SetPaging(properties);
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

	private static void SetPaging(JsonObject properties)
	{
		properties["offset"]!["minimum"] = 0;
		SetRange(properties["limit"]!, 1, McpAutomationLimits.MaxResultPage);
	}

	private static void SetResourceIdSchema(JsonNode schema)
	{
		JsonObject stringSchema = GetSchemaBranch(schema.AsObject(), "string");
		stringSchema["minLength"] = ResourceIdLength;
		stringSchema["maxLength"] = ResourceIdLength;
		stringSchema["pattern"] = ResourceIdPattern;
	}

	private static JsonObject GetSchemaBranch(JsonObject schema, string type)
	{
		if(HasSchemaType(schema, type)) {
			return schema;
		}
		if(schema["anyOf"] is JsonArray alternatives) {
			foreach(JsonNode? alternative in alternatives) {
				if(alternative is JsonObject candidate && HasSchemaType(candidate, type)) {
					return candidate;
				}
			}
		}
		throw new InvalidOperationException($"Generated schema does not contain a {type} branch.");
	}

	private static bool HasSchemaType(JsonObject schema, string type)
	{
		if(schema["type"] is JsonValue value) {
			return value.TryGetValue(out string? schemaType) && schemaType == type;
		}
		if(schema["type"] is JsonArray types) {
			foreach(JsonNode? item in types) {
				if(item is JsonValue candidate && candidate.TryGetValue(out string? schemaType) && schemaType == type) {
					return true;
				}
			}
		}
		return false;
	}

	private static void SetRange(JsonNode schema, int minimum, int maximum)
	{
		schema["minimum"] = minimum;
		schema["maximum"] = maximum;
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
			return ToErrorResult(result.Error);
		}

		JsonNode value = JsonSerializer.SerializeToNode(result.Value!, typeInfo)
			?? throw new InvalidOperationException("The MCP service returned an empty success value.");
		return CreateResult(value, isError: false);
	}

	private static CallToolResult ToErrorResult(McpServiceError error)
	{
		return CreateResult(new JsonObject {
			["code"] = error.Code,
			["message"] = error.Message
		}, isError: true);
	}

	private static CallToolResult CreateImageResult<T>(T metadata, JsonTypeInfo<T> typeInfo, byte[] png)
	{
		JsonNode payload = JsonSerializer.SerializeToNode(metadata, typeInfo)
			?? throw new InvalidOperationException("The MCP service returned empty image metadata.");
		string json = payload.ToJsonString();
		using JsonDocument document = JsonDocument.Parse(json);
		return new CallToolResult {
			Content = [
				new TextContentBlock { Text = json },
				ImageContentBlock.FromBytes(png, "image/png")
			],
			StructuredContent = document.RootElement.Clone(),
			IsError = false
		};
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
