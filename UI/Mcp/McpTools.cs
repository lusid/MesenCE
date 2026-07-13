using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Mesen.Mcp;

public sealed record ReadMemoryRequest(string Space, uint Address, int Count);
public sealed record WriteMemoryRequest(string Space, uint Address, int[] Data);
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
		McpServerTool writeTool = CreateTool(
			new Func<WriteMemoryRequest, CallToolResult>(tools.WriteMemory),
			"write_memory",
			"Writes live emulator memory without pausing emulation and modifies emulator state immediately.",
			serializerOptions,
			readOnly: false
		);
		AddWriteSchemaLimits(writeTool);

		return [
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
			CreateTool(
				new Func<ReadMemoryRequest, CallToolResult>(tools.ReadMemory),
				"read_memory",
				"Reads live emulator memory without pausing emulation.",
				serializerOptions,
				readOnly: true
			),
			writeTool,
			CreateTool(
				new Func<CallToolResult>(tools.GetCpuRegisters),
				"get_cpu_registers",
				"Gets live CPU registers without pausing emulation.",
				serializerOptions,
				readOnly: true
			)
		];
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
internal sealed partial class McpToolJsonContext : JsonSerializerContext
{
}
