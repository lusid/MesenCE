using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Mesen.Mcp;

public sealed record ReadMemoryRequest(string Space, uint Address, int Count);
public sealed record WriteMemoryRequest(string Space, uint Address, byte[] Data);

internal sealed class McpTools
{
	private readonly McpEmulatorService _service;

	private McpTools(McpEmulatorService service)
	{
		_service = service;
	}

	internal static IReadOnlyList<McpServerTool> Create(McpEmulatorService service)
	{
		McpTools tools = new(service);
		JsonSerializerOptions serializerOptions = new(McpJsonUtilities.DefaultOptions);
		serializerOptions.TypeInfoResolverChain.Insert(0, McpToolJsonContext.Default);

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
			CreateTool(
				new Func<WriteMemoryRequest, CallToolResult>(tools.WriteMemory),
				"write_memory",
				"Writes live emulator memory without pausing emulation and modifies emulator state immediately.",
				serializerOptions,
				readOnly: false
			),
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
		return ToResult("get_emulator_status", _service.GetStatus(), McpToolJsonContext.Default.EmulatorStatus);
	}

	private CallToolResult ListMemorySpaces()
	{
		return ToResult("list_memory_spaces", _service.ListMemorySpaces(), McpToolJsonContext.Default.IReadOnlyListMemorySpace);
	}

	private CallToolResult ReadMemory(ReadMemoryRequest request)
	{
		return ToResult("read_memory", _service.ReadMemory(request.Space, request.Address, request.Count), McpToolJsonContext.Default.MemoryRead);
	}

	private CallToolResult WriteMemory(WriteMemoryRequest request)
	{
		return ToResult("write_memory", _service.WriteMemory(request.Space, request.Address, request.Data), McpToolJsonContext.Default.MemoryWrite);
	}

	private CallToolResult GetCpuRegisters()
	{
		return ToResult("get_cpu_registers", _service.GetCpuRegisters(), McpToolJsonContext.Default.CpuRegisters);
	}

	private static McpServerTool CreateTool(Delegate method, string name, string description, JsonSerializerOptions serializerOptions, bool readOnly)
	{
		return McpServerTool.Create(method, new McpServerToolCreateOptions {
			Name = name,
			Description = description,
			ReadOnly = readOnly,
			Destructive = !readOnly,
			Idempotent = true,
			OpenWorld = false,
			SerializerOptions = serializerOptions
		});
	}

	private static CallToolResult ToResult<T>(string toolName, McpServiceResult<T> result, JsonTypeInfo<T> typeInfo)
	{
		if(result.Error is not null) {
			McpServer.Log($"[MCP] Tool {toolName} failed: {result.Error.Code}: {result.Error.Message}");
			JsonObject error = new() {
				["code"] = result.Error.Code,
				["message"] = result.Error.Message
			};
			if(result.Error.BytesWritten is int bytesWritten) {
				error["bytesWritten"] = bytesWritten;
			}
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
[JsonSerializable(typeof(MemoryRead))]
[JsonSerializable(typeof(MemoryWrite))]
[JsonSerializable(typeof(CpuRegisters))]
[JsonSerializable(typeof(ReadMemoryRequest))]
[JsonSerializable(typeof(WriteMemoryRequest))]
internal sealed partial class McpToolJsonContext : JsonSerializerContext
{
}
