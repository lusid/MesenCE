using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using Mesen.Config;
using Mesen.Debugger;
using Mesen.Debugger.Utilities;
using Mesen.Interop;
using Mesen.Mcp;
using Mesen.Windows;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace UI.Tests.Mcp;

public sealed class McpServerTests
{
	[Fact]
	public void AutomationLifecycle_OwnsInvalidatesAndDisposesResourcesAndInputOnce()
	{
		FakeMcpEmulatorApi api = CreateRunningApi();
		McpServer server = new(api);
		McpStateIdentity identity = server.EmulatorIdentity.Current;
		McpSaveStateMetadata state = server.SaveStates.Create([1], identity, DateTimeOffset.UnixEpoch).Value!;
		McpMemorySnapshotMetadata snapshot = server.MemorySnapshots.Create(
			"Nes", nameof(MemoryType.NesWorkRam), 0, [1], identity, DateTimeOffset.UnixEpoch).Value!;
		string search = server.MemorySearches.Create(new(
			"Nes", nameof(MemoryType.NesWorkRam), 0, 1, 1, false, "little", 1, identity,
			new([1], [1], [0], 0, 0))).Value!;

		server.ProcessNotification(new NotificationEventArgs { NotificationType = ConsoleNotificationType.StateLoaded });
		Assert.True(server.MemorySnapshots.Pin(snapshot.Id).IsSuccess);
		Assert.True(server.MemorySearches.Pin(search).IsSuccess);
		server.ProcessNotification(new NotificationEventArgs { NotificationType = ConsoleNotificationType.GameLoaded });
		Assert.Equal("stale_resource", server.SaveStates.Pin(state.Id).Error?.Code);
		Assert.Equal("stale_resource", server.MemorySnapshots.Pin(snapshot.Id).Error?.Code);
		Assert.Equal("stale_resource", server.MemorySearches.Pin(search).Error?.Code);

		server.Stop(TimeSpan.FromSeconds(1));
		server.CompleteCoreRelease();
		server.Dispose();

		Assert.Equal(0, api.ClearExclusiveControllerOverridesCalls);
		Assert.Throws<ObjectDisposedException>(() => server.SaveStates.Pin("missing"));
		Assert.Throws<ObjectDisposedException>(() => server.MemorySnapshots.Pin("missing"));
		Assert.Throws<ObjectDisposedException>(() => server.MemorySearches.Pin("missing"));
	}

	[Fact]
	public async Task MainWindowNotification_ForwardsCodeBreakSynchronously()
	{
		McpEmulatorService service = new(
			FakeMcpEmulatorApi.RunningNes(),
			debuggerLifetime: new DebuggerLifetimeCoordinator(() => { }, () => { })
		);
		using McpServer server = new(service);
		MainWindowMcpLifecycle lifecycle = new(() => server, (_, _) => Task.CompletedTask, _ => { }, _ => { });
		await lifecycle.StartAsync(true, 7342);
		Task<McpServiceResult<ContinueResult>> wait = service.ContinueUntilBreakAsync(nameof(CpuType.Nes), 5000, CancellationToken.None);
		BreakEvent breakEvent = new() { Source = BreakSource.Pause, SourceCpu = CpuType.Nes, BreakpointId = -1 };
		IntPtr pointer = System.Runtime.InteropServices.Marshal.AllocHGlobal(System.Runtime.InteropServices.Marshal.SizeOf<BreakEvent>());
		try {
			System.Runtime.InteropServices.Marshal.StructureToPtr(breakEvent, pointer, false);
			lifecycle.ProcessNotification(new NotificationEventArgs {
				NotificationType = ConsoleNotificationType.CodeBreak,
				Parameter = pointer
			});
			System.Runtime.InteropServices.Marshal.StructureToPtr(new BreakEvent { Source = BreakSource.Nmi }, pointer, false);
		} finally {
			System.Runtime.InteropServices.Marshal.FreeHGlobal(pointer);
		}

		Assert.Equal("pause", (await wait.WaitAsync(TimeSpan.FromSeconds(2))).Value!.Reason);
	}

	[Fact]
	public async Task MainWindowLifecycle_AppliesConfigBeforeStartingAndStopsBeforeCoreRelease()
	{
		List<string> events = [];
		int createCalls = 0;
		using McpServer server = new(new McpEmulatorService(new FakeMcpEmulatorApi()));
		MainWindowMcpLifecycle lifecycle = new(
			() => {
				createCalls++;
				return server;
			},
			(_, _) => {
				events.Add("start");
				return Task.CompletedTask;
			},
			_ => events.Add("dispose"),
			events.Add
		);

		await lifecycle.ApplyConfigAndStartAsync(() => events.Add("disabled-config"), false, 7342);
		Assert.Equal(0, createCalls);
		await lifecycle.ApplyConfigAndStartAsync(() => events.Add("config"), true, 7342);
		await lifecycle.StartAsync(true, 7342);
		Assert.Equal(1, createCalls);

		lifecycle.StopBeforeCoreRelease(
			() => events.Add("core-stop"),
			() => events.Add("listener-dispose"),
			() => events.Add("core-release")
		);

		Assert.Equal([
			"disabled-config", "config", "start", "listener-dispose", "core-stop", "core-release", "dispose"
		], events);
	}

	[Fact]
	public async Task MainWindowLifecycle_LogsAndSwallowsBindFailure()
	{
		List<string> logs = [];
		int disposeCalls = 0;
		using McpServer server = new(new McpEmulatorService(new FakeMcpEmulatorApi()));
		MainWindowMcpLifecycle lifecycle = new(
			() => server,
			(_, _) => Task.FromException(new IOException("Address already in use")),
			_ => disposeCalls++,
			logs.Add
		);

		await lifecycle.StartAsync(true, 7342);
		lifecycle.StopBeforeCoreRelease(() => { }, () => { }, () => { });

		Assert.Equal(1, disposeCalls);
		Assert.Equal(["[MCP] Unable to start server on 127.0.0.1:7342: Address already in use"], logs);
	}

	[Fact]
	public async Task MainWindowLifecycle_CloseDuringStartupOwnsDisposalAndSuppressesFailureLog()
	{
		TaskCompletionSource startup = new(TaskCreationOptions.RunContinuationsAsynchronously);
		List<string> events = [];
		using McpServer server = new(new McpEmulatorService(new FakeMcpEmulatorApi()));
		MainWindowMcpLifecycle lifecycle = new(
			() => server,
			(_, _) => startup.Task,
			_ => events.Add("dispose"),
			message => events.Add($"log:{message}")
		);

		Task start = lifecycle.StartAsync(true, 7342);
		lifecycle.StopBeforeCoreRelease(() => events.Add("core-stop"), () => { }, () => events.Add("core-release"));
		startup.SetException(new OperationCanceledException("closing"));
		await start;
		lifecycle.StopBeforeCoreRelease(() => { }, () => { }, () => { });

		Assert.Equal(["core-stop", "core-release", "dispose"], events);
	}

	[Fact]
	public async Task MainWindowLifecycle_DetachesServerBeforeListenerAndCoreRelease()
	{
		FakeMcpEmulatorApi api = CreateRunningApi();
		McpEmulatorService service = new(api);
		using McpServer server = new(service);
		McpStateIdentity identity = server.EmulatorIdentity.Current;
		Assert.True(server.SaveStates.Create([1], identity, DateTimeOffset.UnixEpoch).IsSuccess);
		MainWindowMcpLifecycle lifecycle = new(() => server, (_, _) => Task.CompletedTask, _ => { }, _ => { });
		await lifecycle.StartAsync(true, 7342);

		List<string> events = [];
		lifecycle.StopBeforeCoreRelease(
			() => {
				events.Add("core-stop");
				Assert.Equal("server_stopping", service.GetStatus().Error?.Code);
				Assert.Throws<ObjectDisposedException>(() => server.SaveStates.Pin("missing"));
			},
			() => events.Add("listener-dispose"),
			() => events.Add("core-release"));

		Assert.Equal(["listener-dispose", "core-stop", "core-release"], events);
		Assert.Equal(0, api.ClearExclusiveControllerOverridesCalls);
	}

	[Fact]
	public async Task Discovery_ExposesExactlyThirtyThreeToolsWithBoundedSchemasOnLoopbackMcpRoute()
	{
		using McpServer server = new(new McpEmulatorService(new FakeMcpEmulatorApi()));
		await server.StartAsync(0);

		Assert.Equal(IPAddress.Loopback, IPAddress.Parse(server.Endpoint.Host));
		using HttpClient httpClient = new();
		Assert.Equal(HttpStatusCode.NotFound, (await httpClient.GetAsync(new Uri(server.Endpoint, "/unrelated"))).StatusCode);

		await using McpClient client = await CreateClientAsync(server.Endpoint);
		IList<McpClientTool> tools = await client.ListToolsAsync();

		Assert.Equal(
			[
				"capture_screenshot", "compare_memory_snapshots", "configure_execution_trace", "continue_until_break",
				"create_memory_snapshot", "create_save_state", "delete_memory_search", "delete_memory_snapshot",
				"delete_save_state", "disassemble", "get_automation_capabilities", "get_break_context",
				"get_call_stack", "get_cpu_registers", "get_emulator_status", "get_execution_trace",
				"get_memory_search_results", "list_breakpoints", "list_memory_spaces", "load_save_state",
				"map_address", "pause", "read_memory", "refine_memory_search", "remove_all_breakpoints",
				"remove_breakpoint", "resume", "run_experiment", "set_breakpoint", "start_memory_search",
				"step", "undo_memory_search", "write_memory"
			],
			[.. tools.Select(tool => tool.Name).Order()]
		);
		Assert.Contains("modifies emulator state immediately", tools.Single(tool => tool.Name == "write_memory").Description);
		JsonElement writeSchema = tools.Single(tool => tool.Name == "write_memory").JsonSchema;
		JsonElement readSchema = tools.Single(tool => tool.Name == "read_memory").JsonSchema;
		JsonElement countSchema = readSchema.GetProperty("properties").GetProperty("request").GetProperty("properties").GetProperty("count");
		Assert.Equal(1, countSchema.GetProperty("minimum").GetInt32());
		Assert.Equal(McpEmulatorService.MaxTransferSize, countSchema.GetProperty("maximum").GetInt32());
		JsonElement dataSchema = writeSchema.GetProperty("properties").GetProperty("request").GetProperty("properties").GetProperty("data");
		Assert.Equal("array", dataSchema.GetProperty("type").GetString());
		Assert.Equal(McpEmulatorService.MaxTransferSize, dataSchema.GetProperty("maxItems").GetInt32());
		Assert.Equal("integer", dataSchema.GetProperty("items").GetProperty("type").GetString());
		Assert.Equal(0, dataSchema.GetProperty("items").GetProperty("minimum").GetInt32());
		Assert.Equal(byte.MaxValue, dataSchema.GetProperty("items").GetProperty("maximum").GetInt32());

		JsonElement breakpointProperties = RequestProperties(tools, "set_breakpoint");
		Assert.Equal("integer", breakpointProperties.GetProperty("startAddress").GetProperty("type").GetString());
		Assert.Contains("integer", breakpointProperties.GetProperty("endAddress").GetProperty("type").EnumerateArray().Select(value => value.GetString()));
		Assert.Equal(
			["execute", "read", "write"],
			breakpointProperties.GetProperty("access").GetProperty("enum").EnumerateArray().Select(value => value.GetString())
		);
		Assert.Equal(McpDebuggerLimits.MaxConditionUtf8Bytes, breakpointProperties.GetProperty("condition").GetProperty("maxLength").GetInt32());

		JsonElement continueProperties = RequestProperties(tools, "continue_until_break");
		JsonElement timeoutSchema = continueProperties.GetProperty("timeoutMs");
		Assert.Equal(McpDebuggerLimits.MinContinueTimeoutMs, timeoutSchema.GetProperty("minimum").GetInt32());
		Assert.Equal(McpDebuggerLimits.MaxContinueTimeoutMs, timeoutSchema.GetProperty("maximum").GetInt32());

		foreach(string toolName in new[] { "get_break_context", "disassemble" }) {
			JsonElement properties = RequestProperties(tools, toolName);
			Assert.Equal(McpDebuggerLimits.MaxDisassemblyRows - 1, properties.GetProperty("before").GetProperty("maximum").GetInt32());
			Assert.Equal(McpDebuggerLimits.MaxDisassemblyRows - 1, properties.GetProperty("after").GetProperty("maximum").GetInt32());
		}
		Assert.Equal(
			McpDebuggerLimits.MaxCallStackDepth,
			RequestProperties(tools, "get_break_context").GetProperty("maxStackDepth").GetProperty("maximum").GetInt32()
		);
		Assert.Equal(
			McpDebuggerLimits.MaxCallStackDepth,
			RequestProperties(tools, "get_call_stack").GetProperty("maxDepth").GetProperty("maximum").GetInt32()
		);

		JsonElement traceProperties = RequestProperties(tools, "configure_execution_trace");
		Assert.Equal(McpDebuggerLimits.MaxConditionUtf8Bytes, traceProperties.GetProperty("condition").GetProperty("maxLength").GetInt32());
		Assert.Equal(McpDebuggerLimits.MaxTraceFormatUtf8Bytes, traceProperties.GetProperty("format").GetProperty("maxLength").GetInt32());
		Assert.Equal(
			McpDebuggerLimits.MaxTraceRows,
			RequestProperties(tools, "get_execution_trace").GetProperty("maxRows").GetProperty("maximum").GetInt32()
		);

		AssertSchemaBound(tools, "create_memory_snapshot", "count", "minimum", 1);
		AssertSchemaBound(tools, "create_memory_snapshot", "count", "maximum", McpAutomationLimits.MaxMemorySnapshotBytes);
		AssertPagingSchema(tools, "compare_memory_snapshots");
		AssertSchemaBound(tools, "compare_memory_snapshots", "sampleBytes", "minimum", 0);
		AssertSchemaBound(tools, "compare_memory_snapshots", "sampleBytes", "maximum", McpAutomationLimits.MaxRunSampleBytes);
		AssertSchemaBound(tools, "start_memory_search", "count", "minimum", 1);
		AssertSchemaBound(tools, "start_memory_search", "count", "maximum", McpAutomationLimits.MaxSearchRangeBytes);
		AssertSchemaBound(tools, "start_memory_search", "stride", "minimum", 1);
		AssertSchemaBound(tools, "start_memory_search", "stride", "maximum", 4);
		AssertSchemaEnum(tools, "start_memory_search", "width", 1, 2, 4);
		AssertSchemaEnum(tools, "start_memory_search", "byteOrder", "little", "big");
		AssertSchemaEnum(tools, "refine_memory_search", "comparison",
			"exact", "not_equal", "increased", "decreased", "changed", "unchanged", "increased_by", "decreased_by");
		AssertSchemaBound(tools, "refine_memory_search", "delta", "minimum", 1);
		AssertPagingSchema(tools, "get_memory_search_results");
		AssertResourceIdSchema(RequestProperties(tools, "load_save_state").GetProperty("id"));
		AssertResourceIdSchema(RequestProperties(tools, "delete_save_state").GetProperty("id"));
		AssertResourceIdSchema(RequestProperties(tools, "compare_memory_snapshots").GetProperty("firstId"));
		AssertResourceIdSchema(RequestProperties(tools, "compare_memory_snapshots").GetProperty("secondId"));
		AssertResourceIdSchema(RequestProperties(tools, "delete_memory_snapshot").GetProperty("id"));
		AssertResourceIdSchema(RequestProperties(tools, "refine_memory_search").GetProperty("id"));
		AssertResourceIdSchema(RequestProperties(tools, "get_memory_search_results").GetProperty("id"));
		AssertResourceIdSchema(RequestProperties(tools, "undo_memory_search").GetProperty("id"));
		AssertResourceIdSchema(RequestProperties(tools, "delete_memory_search").GetProperty("id"));

		JsonElement experiment = RequestProperties(tools, "run_experiment");
		AssertResourceIdSchema(experiment.GetProperty("saveStateId"), nullable: true);
		Assert.Equal(["Nes"], experiment.GetProperty("cpu").GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
		Assert.Equal(McpAutomationLimits.MinExperimentTimeoutMs, experiment.GetProperty("timeoutMs").GetProperty("minimum").GetInt32());
		Assert.Equal(McpAutomationLimits.MaxExperimentTimeoutMs, experiment.GetProperty("timeoutMs").GetProperty("maximum").GetInt32());
		JsonElement segments = experiment.GetProperty("segments");
		Assert.Equal(1, segments.GetProperty("minItems").GetInt32());
		Assert.Equal(McpAutomationLimits.MaxSegments, segments.GetProperty("maxItems").GetInt32());
		Assert.Equal(1, segments.GetProperty("items").GetProperty("properties").GetProperty("frames").GetProperty("minimum").GetInt32());
		Assert.Equal(McpAutomationLimits.MaxExperimentFrames, segments.GetProperty("items").GetProperty("properties").GetProperty("frames").GetProperty("maximum").GetInt32());
		JsonElement controllers = segments.GetProperty("items").GetProperty("properties").GetProperty("controllers");
		Assert.Equal(McpAutomationLimits.MaxControllersPerSegment, controllers.GetProperty("maxItems").GetInt32());
		JsonElement controller = controllers.GetProperty("items").GetProperty("properties");
		Assert.Equal(McpAutomationLimits.MinPhysicalControllerPort, controller.GetProperty("port").GetProperty("minimum").GetInt32());
		Assert.Equal(McpAutomationLimits.MaxPhysicalControllerPort, controller.GetProperty("port").GetProperty("maximum").GetInt32());
		Assert.Equal(byte.MaxValue + 1, controller.GetProperty("buttons").GetProperty("maxItems").GetInt32());
		JsonElement observations = experiment.GetProperty("observations");
		Assert.Equal(McpAutomationLimits.MaxObservations, observations.GetProperty("maxItems").GetInt32());
		Assert.Equal(1, observations.GetProperty("items").GetProperty("properties").GetProperty("count").GetProperty("minimum").GetInt32());
		Assert.Equal(McpAutomationLimits.MaxObservedBytes, observations.GetProperty("items").GetProperty("properties").GetProperty("count").GetProperty("maximum").GetInt32());
		JsonElement decode = observations.GetProperty("items").GetProperty("properties").GetProperty("decode");
		Assert.Equal([1, 2, 4], decode.GetProperty("properties").GetProperty("width").GetProperty("enum").EnumerateArray().Select(value => value.GetInt32()));
		Assert.Equal(["little", "big"], decode.GetProperty("properties").GetProperty("byteOrder").GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
		JsonElement assertions = experiment.GetProperty("assertions");
		Assert.Equal(McpAutomationLimits.MaxAssertions, assertions.GetProperty("maxItems").GetInt32());
		JsonElement expectedBytes = assertions.GetProperty("items").GetProperty("properties").GetProperty("expectedBytes");
		Assert.Equal(McpAutomationLimits.MaxObservedBytes, expectedBytes.GetProperty("maxItems").GetInt32());
		Assert.Equal(0, expectedBytes.GetProperty("items").GetProperty("minimum").GetInt32());
		Assert.Equal(byte.MaxValue, expectedBytes.GetProperty("items").GetProperty("maximum").GetInt32());
		Assert.Equal(
			["equal", "not_equal", "range", "masked_equal", "relative_equal", "relative_not_equal", "increased", "decreased", "changed", "unchanged"],
			assertions.GetProperty("items").GetProperty("properties").GetProperty("operator").GetProperty("enum")
				.EnumerateArray().Select(value => value.GetString())
		);

		HashSet<string> readOnlyTools = [
			"capture_screenshot", "compare_memory_snapshots", "disassemble", "get_automation_capabilities",
			"get_break_context", "get_call_stack", "get_cpu_registers", "get_emulator_status", "get_execution_trace",
			"get_memory_search_results", "list_breakpoints", "list_memory_spaces", "map_address", "read_memory"
		];
		Assert.All(tools, tool => {
			bool readOnly = readOnlyTools.Contains(tool.Name);
			Assert.Equal(readOnly, tool.ProtocolTool.Annotations!.ReadOnlyHint);
			Assert.Equal(!readOnly, tool.ProtocolTool.Annotations.DestructiveHint);
			Assert.Equal(readOnly, tool.ProtocolTool.Annotations.IdempotentHint);
			Assert.False(tool.ProtocolTool.Annotations.OpenWorldHint);
		});
	}

	[Fact]
	public async Task ReadMemory_RejectsOversizeCountBeforeRangeValidationOrNativeAccess()
	{
		FakeMcpEmulatorApi api = CreateRunningApi();
		using McpServer server = new(new McpEmulatorService(api));
		await server.StartAsync(0);
		await using McpClient client = await CreateClientAsync(server.Endpoint);

		CallToolResult result = await client.CallToolAsync("read_memory", Request(new {
			space = "invalid",
			address = uint.MaxValue,
			count = McpEmulatorService.MaxTransferSize + 1
		}));

		Assert.True(result.IsError);
		Assert.Equal("payload_too_large", result.StructuredContent!.Value.GetProperty("code").GetString());
		Assert.Equal(0, api.DebuggerRequestBlockStateCalls);
		Assert.Equal(0, api.IsRunningCalls);
		Assert.Equal(0, api.GetMemoryValuesCalls);
	}

	[Fact]
	public async Task Calls_AllFiveToolsAndReturnsStructuredServiceErrors()
	{
		FakeMcpEmulatorApi api = CreateRunningApi();
		using McpServer server = new(new McpEmulatorService(api));
		await server.StartAsync(0);
		await using McpClient client = await CreateClientAsync(server.Endpoint);

		CallToolResult status = await client.CallToolAsync("get_emulator_status");
		CallToolResult spaces = await client.CallToolAsync("list_memory_spaces");
		CallToolResult read = await client.CallToolAsync("read_memory", Request(new {
			space = nameof(MemoryType.NesWorkRam),
			address = 1U,
			count = 2
		}));
		CallToolResult write = await client.CallToolAsync("write_memory", Request(new {
			space = nameof(MemoryType.NesWorkRam),
			address = 2U,
			data = new[] { 0x44, 0x55 }
		}));
		CallToolResult registers = await client.CallToolAsync("get_cpu_registers");
		CallToolResult invalid = await client.CallToolAsync("read_memory", Request(new {
			space = nameof(MemoryType.NesWorkRam),
			address = 15U,
			count = 2
		}));

		Assert.False(status.IsError);
		Assert.True(status.StructuredContent!.Value.GetProperty("gameLoaded").GetBoolean());
		Assert.Equal("2.0", status.StructuredContent.Value.GetProperty("mcpVersion").GetString());
		Assert.False(spaces.IsError);
		Assert.Equal(nameof(MemoryType.NesWorkRam), spaces.StructuredContent!.Value[0].GetProperty("id").GetString());
		Assert.False(read.IsError);
		Assert.Equal("A1B2", read.StructuredContent!.Value.GetProperty("hex").GetString());
		Assert.Equal([0xA1, 0xB2], read.StructuredContent.Value.GetProperty("data").EnumerateArray().Select(value => value.GetInt32()));
		Assert.False(write.IsError);
		Assert.Equal(2, write.StructuredContent!.Value.GetProperty("count").GetInt32());
		Assert.False(registers.IsError);
		Assert.Equal("6502", registers.StructuredContent!.Value.GetProperty("architecture").GetString());
		Assert.True(invalid.IsError);
		JsonElement error = invalid.StructuredContent!.Value;
		Assert.Equal("invalid_range", error.GetProperty("code").GetString());
		Assert.Equal("The requested range is outside the selected memory space.", error.GetProperty("message").GetString());
		Assert.False(error.TryGetProperty("bytesWritten", out _));
		Assert.Equal(1, api.GetMemoryValuesCalls);
		Assert.Equal(1, api.SetMemoryValuesCalls);
	}

	[Fact]
	public async Task ScreenshotProtocol_ReturnsMetadataTextStructuredContentAndRawImageOnly()
	{
		byte[] png = [0x89, 0x50, 0x4E, 0x47];
		FakeMcpEmulatorApi api = CreateRunningApi();
		api.CaptureScreenshotHandler = () => McpServiceResult<McpScreenshotCapture>.Success(
			new(new(2, 1, 7, png.Length, 0, 0), png));
		using McpServer server = new(new McpEmulatorService(api));
		await server.StartAsync(0);
		await using McpClient client = await CreateClientAsync(server.Endpoint);

		CallToolResult result = await client.CallToolAsync("capture_screenshot");

		Assert.False(result.IsError);
		Assert.Equal(2, result.Content.Count);
		Assert.IsType<TextContentBlock>(result.Content[0]);
		ImageContentBlock image = Assert.IsType<ImageContentBlock>(result.Content[1]);
		Assert.Equal("image/png", image.MimeType);
		Assert.Equal(png, image.DecodedData.ToArray());
		JsonElement metadata = result.StructuredContent!.Value;
		Assert.Equal(2, metadata.GetProperty("width").GetInt32());
		Assert.Equal(png.Length, metadata.GetProperty("pngBytes").GetInt32());
		Assert.DoesNotContain(Convert.ToBase64String(png), metadata.GetRawText());
	}

	[Fact]
	public async Task AutomationProtocol_ReturnsStructuredPreflightErrorsAndSuccessfulRuntimePartials()
	{
		FakeMcpEmulatorApi api = CreateRunningApi();
		api.ControllerTopology = [new(0, 0, ControllerType.NesController, [new("A", 0, false)])];
		using McpServer server = new(new McpEmulatorService(api));
		await server.StartAsync(0);
		await using McpClient client = await CreateClientAsync(server.Endpoint);

		CallToolResult preflight = await client.CallToolAsync("run_experiment", Request(new {
			cpu = "nes",
			saveStateId = (string?)null,
			segments = new[] { new { frames = 1, controllers = Array.Empty<object>(), checkpoint = (string?)null } },
			timeoutMs = 1000,
			observations = Array.Empty<object>(),
			assertions = Array.Empty<object>(),
			captureFinalScreenshot = false,
			failFast = false
		}));
		CallToolResult partial = await client.CallToolAsync("run_experiment", Request(new {
			cpu = "Nes",
			saveStateId = (string?)null,
			segments = new[] { new { frames = 1, controllers = Array.Empty<object>(), checkpoint = (string?)null } },
			timeoutMs = McpAutomationLimits.MinExperimentTimeoutMs,
			observations = Array.Empty<object>(),
			assertions = Array.Empty<object>(),
			captureFinalScreenshot = false,
			failFast = false
		}));

		AssertErrorCode(preflight, "invalid_request");
		Assert.False(partial.IsError);
		Assert.Equal(McpExperimentStatus.Failed, partial.StructuredContent!.Value.GetProperty("status").GetString());
		Assert.Equal(McpExperimentReason.Timeout, partial.StructuredContent.Value.GetProperty("reason").GetString());
		Assert.Single(partial.Content);
		Assert.IsType<TextContentBlock>(partial.Content[0]);
	}

	[Fact]
	public async Task RunExperimentProtocol_AppendsOnlyItsOwnedSuccessfulFinalPng()
	{
		byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D];
		FakeMcpEmulatorApi api = CreateRunningApi();
		api.ControllerTopology = [new(0, 0, ControllerType.NesController, [new("A", 0, false)])];
		api.CaptureScreenshotHandler = () => McpServiceResult<McpScreenshotCapture>.Success(
			new(new(2, 1, 9, png.Length, 0, 0), png));
		using McpEmulatorService service = new(
			api,
			debuggerLifetime: new DebuggerLifetimeCoordinator(() => { }, () => { }),
			breakpointCollection: new ProtocolBreakpointCollection());
		using McpServer server = new(service);
		api.StepHandler = (_, _, _) => SendBreak(service, new BreakEvent {
			Source = BreakSource.PpuStep,
			SourceCpu = CpuType.Nes
		});
		await server.StartAsync(0);
		await using McpClient client = await CreateClientAsync(server.Endpoint);

		CallToolResult result = await client.CallToolAsync("run_experiment", Request(new {
			cpu = "Nes",
			saveStateId = (string?)null,
			segments = new[] { new { frames = 1, controllers = Array.Empty<object>(), checkpoint = (string?)null } },
			timeoutMs = 1000,
			observations = Array.Empty<object>(),
			assertions = Array.Empty<object>(),
			captureFinalScreenshot = true,
			failFast = false
		}));

		Assert.False(result.IsError);
		Assert.Equal(McpExperimentStatus.Completed, result.StructuredContent!.Value.GetProperty("status").GetString());
		Assert.Equal(2, result.Content.Count);
		ImageContentBlock image = Assert.IsType<ImageContentBlock>(result.Content[1]);
		Assert.Equal("image/png", image.MimeType);
		Assert.Equal(png, image.DecodedData.ToArray());
		Assert.DoesNotContain(Convert.ToBase64String(png), result.StructuredContent.Value.GetRawText());
	}

	[Fact]
	public async Task BreakpointProtocol_SetListAndRemoveReturnsStructuredResults()
	{
		FakeMcpEmulatorApi api = CreateRunningApi();
		api.MemorySizes[MemoryType.NesMemory] = 0x10000;
		using McpEmulatorService service = new(
			api,
			debuggerLifetime: new DebuggerLifetimeCoordinator(() => { }, () => { }),
			breakpointCollection: new ProtocolBreakpointCollection()
		);
		using McpServer server = new(service);
		await server.StartAsync(0);
		await using McpClient client = await CreateClientAsync(server.Endpoint);

		CallToolResult set = await client.CallToolAsync("set_breakpoint", Request(new {
			cpu = nameof(CpuType.Nes),
			space = nameof(MemoryType.NesMemory),
			access = "execute",
			startAddress = 0x8000U,
			endAddress = (uint?)null,
			condition = (string?)null
		}));
		long id = set.StructuredContent!.Value.GetProperty("id").GetInt64();
		CallToolResult list = await client.CallToolAsync("list_breakpoints");
		CallToolResult remove = await client.CallToolAsync("remove_breakpoint", Request(new { id }));

		Assert.False(set.IsError);
		Assert.Equal(1, id);
		Assert.False(list.IsError);
		Assert.Equal(id, Assert.Single(list.StructuredContent!.Value.EnumerateArray()).GetProperty("id").GetInt64());
		Assert.False(remove.IsError);
		Assert.True(remove.StructuredContent!.Value.GetProperty("removed").GetBoolean());
		Assert.Empty((await client.CallToolAsync("list_breakpoints")).StructuredContent!.Value.EnumerateArray());
	}

	[Fact]
	public async Task DebuggerProtocol_InvalidCallsReturnExactStructuredErrorCodes()
	{
		FakeMcpEmulatorApi api = CreateRunningApi();
		using McpServer server = new(new McpEmulatorService(
			api,
			debuggerLifetime: new DebuggerLifetimeCoordinator(() => { }, () => { })
		));
		await server.StartAsync(0);
		await using McpClient client = await CreateClientAsync(server.Endpoint);

		CallToolResult invalidTimeout = await client.CallToolAsync("continue_until_break", Request(new {
			cpu = nameof(CpuType.Nes),
			timeoutMs = 0
		}));
		CallToolResult staleContext = await client.CallToolAsync("get_break_context", Request(new {
			before = 0,
			after = 0,
			maxStackDepth = 1
		}));
		CallToolResult oversizedDisassembly = await client.CallToolAsync("disassemble", Request(new {
			cpu = nameof(CpuType.Nes),
			space = (string?)null,
			address = (uint?)0x8000,
			before = McpDebuggerLimits.MaxDisassemblyRows - 1,
			after = 1
		}));
		CallToolResult unsupportedStep = await client.CallToolAsync("step", Request(new {
			cpu = nameof(CpuType.Nes),
			stepType = "over"
		}));

		AssertErrorCode(invalidTimeout, "invalid_timeout");
		AssertErrorCode(staleContext, "stale_context");
		AssertErrorCode(oversizedDisassembly, "invalid_range");
		AssertErrorCode(unsupportedStep, "invalid_step_type");
	}

	[Fact]
	public async Task ContinueUntilBreak_ProtocolCancellationReturnsCancelledAndReleasesWaiter()
	{
		TaskCompletionSource resumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
		FakeMcpEmulatorApi api = CreateRunningApi();
		api.ResumeDebuggerHandler = () => resumed.TrySetResult();
		using McpServer server = new(
			new McpEmulatorService(
				api,
				debuggerLifetime: new DebuggerLifetimeCoordinator(() => { }, () => { })
			),
			message => {
				if(message.Contains("failed with cancelled", StringComparison.Ordinal)) {
					cancelled.TrySetResult();
				}
			}
		);
		await server.StartAsync(0);
		await using McpClient client = await CreateClientAsync(server.Endpoint);
		using CancellationTokenSource cancellation = new();

		Task<CallToolResult> pending = client.CallToolAsync("continue_until_break", Request(new {
			cpu = nameof(CpuType.Nes),
			timeoutMs = McpDebuggerLimits.MaxContinueTimeoutMs
		}), cancellationToken: cancellation.Token).AsTask();
		await resumed.Task.WaitAsync(TimeSpan.FromSeconds(2));
		cancellation.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
		await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
		CallToolResult subsequent = await client.CallToolAsync("continue_until_break", Request(new {
			cpu = nameof(CpuType.Nes),
			timeoutMs = McpDebuggerLimits.MinContinueTimeoutMs
		}));
		AssertErrorCode(subsequent, "timeout");
	}

	[Fact]
	public async Task WriteMemory_RejectsOutOfRangeIntegersBeforeCallingService()
	{
		FakeMcpEmulatorApi api = CreateRunningApi();
		using McpServer server = new(new McpEmulatorService(api));
		await server.StartAsync(0);
		await using McpClient client = await CreateClientAsync(server.Endpoint);

		CallToolResult negative = await client.CallToolAsync("write_memory", Request(new {
			space = nameof(MemoryType.NesWorkRam),
			address = 0U,
			data = new[] { -1 }
		}));
		CallToolResult tooLarge = await client.CallToolAsync("write_memory", Request(new {
			space = nameof(MemoryType.NesWorkRam),
			address = 0U,
			data = new[] { 256 }
		}));
		AssertInvalidByteValue(negative);
		AssertInvalidByteValue(tooLarge);
		Assert.Equal(0, api.IsRunningCalls);
		Assert.Equal(0, api.SetMemoryValuesCalls);
	}

	[Fact]
	public async Task WriteMemory_RejectsOversizePayloadBeforeScanningOrCallingService()
	{
		FakeMcpEmulatorApi api = CreateRunningApi();
		using McpServer server = new(new McpEmulatorService(api));
		await server.StartAsync(0);
		await using McpClient client = await CreateClientAsync(server.Endpoint);
		int[] data = new int[McpEmulatorService.MaxTransferSize + 1];
		data[0] = -1;

		CallToolResult result = await client.CallToolAsync("write_memory", Request(new {
			space = nameof(MemoryType.NesWorkRam),
			address = 0U,
			data
		}));

		Assert.True(result.IsError);
		Assert.Equal("payload_too_large", result.StructuredContent!.Value.GetProperty("code").GetString());
		Assert.Equal(0, api.IsRunningCalls);
		Assert.Equal(0, api.SetMemoryValuesCalls);
	}

	[Fact]
	public async Task Host_RejectsRequestBodiesAboveConfiguredCeiling()
	{
		using McpServer server = new(new McpEmulatorService(CreateRunningApi()));
		await server.StartAsync(0);
		using HttpClient client = new();
		client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
		using ByteArrayContent content = new(new byte[600 * 1024]);
		content.Headers.ContentType = new("application/json");

		using HttpResponseMessage response = await client.PostAsync(server.Endpoint, content);

		Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
	}

	[Fact]
	public async Task ToolLogs_ContainOnlyRequestTypeOutcomeCodeAndDuration()
	{
		List<string> logs = [];
		FakeMcpEmulatorApi api = CreateRunningApi();
		using McpServer server = new(new McpEmulatorService(api), logs.Add);
		await server.StartAsync(0);
		await using McpClient client = await CreateClientAsync(server.Endpoint);

		await client.CallToolAsync("get_emulator_status");
		await client.CallToolAsync("read_memory", Request(new {
			space = nameof(MemoryType.NesWorkRam),
			address = 15U,
			count = 173
		}));

		Assert.Collection(
			logs,
			log => Assert.Matches(@"^\[MCP\] Tool get_emulator_status succeeded in \d+ ms\.$", log),
			log => Assert.Matches(@"^\[MCP\] Tool read_memory failed with invalid_range in \d+ ms\.$", log)
		);
		Assert.DoesNotContain(logs, log => log.Contains("NesWorkRam") || log.Contains("173") || log.Contains("A1B2"));
	}

	[Fact]
	public async Task ToolCall_WhenGenerationChangesDuringInterop_ReturnsStateChanged()
	{
		FakeMcpEmulatorApi api = CreateRunningApi();
		using McpServer server = new(new McpEmulatorService(api));
		api.OnRead = () => server.ProcessNotification(new NotificationEventArgs {
			NotificationType = ConsoleNotificationType.StateLoaded
		});
		await server.StartAsync(0);
		await using McpClient client = await CreateClientAsync(server.Endpoint);

		CallToolResult result = await client.CallToolAsync("read_memory", Request(new {
			space = nameof(MemoryType.NesWorkRam),
			address = 0U,
			count = 2
		}));

		Assert.True(result.IsError);
		Assert.Equal("state_changed", result.StructuredContent!.Value.GetProperty("code").GetString());
	}

	[Fact]
	public async Task ConcurrentProtocolCalls_AreSerializedByTheService()
	{
		using ManualResetEventSlim readEntered = new();
		using ManualResetEventSlim releaseRead = new();
		FakeMcpEmulatorApi api = CreateRunningApi();
		api.OnRead = () => {
			readEntered.Set();
			releaseRead.Wait(TimeSpan.FromSeconds(5));
		};
		using McpServer server = new(new McpEmulatorService(api));
		await server.StartAsync(0);
		await using McpClient client = await CreateClientAsync(server.Endpoint);

		Task<CallToolResult> read = client.CallToolAsync("read_memory", Request(new {
			space = nameof(MemoryType.NesWorkRam),
			address = 0U,
			count = 2
		})).AsTask();
		Assert.True(readEntered.Wait(TimeSpan.FromSeconds(5)));
		Task<CallToolResult> status = client.CallToolAsync("get_emulator_status").AsTask();
		await Task.Delay(100);

		Assert.False(status.IsCompleted);
		Assert.Equal(1, api.IsRunningCalls);
		releaseRead.Set();
		await Task.WhenAll(read, status).WaitAsync(TimeSpan.FromSeconds(5));
	}

	[Fact]
	public async Task ClientCanReconnectWithoutRestartingHost()
	{
		using McpServer server = new(new McpEmulatorService(CreateRunningApi()));
		await server.StartAsync(0);
		await using(McpClient firstClient = await CreateClientAsync(server.Endpoint)) {
			Assert.Equal(33, (await firstClient.ListToolsAsync()).Count);
		}

		await using McpClient secondClient = await CreateClientAsync(server.Endpoint);
		CallToolResult status = await secondClient.CallToolAsync("get_emulator_status");

		Assert.False(status.IsError);
	}

	[Fact]
	public async Task Lifecycle_IsIdempotentBoundedAndStopsListening()
	{
		using McpServer server = new(new McpEmulatorService(CreateRunningApi()));
		Task firstStart = server.StartAsync(0);
		Task secondStart = server.StartAsync(0);
		Assert.Same(firstStart, secondStart);
		await firstStart;
		Uri endpoint = server.Endpoint;

		long started = Environment.TickCount64;
		server.Stop(TimeSpan.FromSeconds(30));
		server.Stop(TimeSpan.FromSeconds(30));

		Assert.InRange(Environment.TickCount64 - started, 0, 2500);
		using HttpClient httpClient = new();
		await Assert.ThrowsAsync<HttpRequestException>(() => httpClient.GetAsync(endpoint));
	}

	[Fact]
	public async Task AutomationToolLogs_OmitRequestsStateBytesAndPngData()
	{
		List<string> logs = [];
		byte[] state = [0xDE, 0xAD, 0xBE, 0xEF];
		byte[] png = [0x89, 0x50, 0x4E, 0x47];
		FakeMcpEmulatorApi api = CreateRunningApi();
		api.ControllerTopology = [new(0, 0, ControllerType.NesController, [new("private-button", 0, false)])];
		api.CreateSaveStateHandler = () => McpServiceResult<byte[]>.Success(state);
		api.CaptureScreenshotHandler = () => McpServiceResult<McpScreenshotCapture>.Success(
			new(new(1, 1, 1, png.Length, 0, 0), png));
		using McpServer server = new(new McpEmulatorService(api), logs.Add);
		await server.StartAsync(0);
		await using McpClient client = await CreateClientAsync(server.Endpoint);

		await client.CallToolAsync("create_save_state");
		await client.CallToolAsync("capture_screenshot");
		await client.CallToolAsync("run_experiment", Request(new {
			cpu = "Nes",
			saveStateId = (string?)null,
			segments = new[] { new {
				frames = 1,
				controllers = new[] { new { port = 0, buttons = new[] { "private-button" } } },
				checkpoint = (string?)null
			} },
			timeoutMs = 1000,
			observations = new[] { new {
				id = "private-observation",
				checkpoint = "final",
				space = "private-space",
				address = 0U,
				count = 1,
				decode = (object?)null
			} },
			assertions = new[] { new {
				id = "private-assertion",
				checkpoint = "final",
				observationId = "private-observation",
				@operator = "equal",
				expectedBytes = new[] { 173 },
				expectedValue = (long?)null,
				minimumValue = (long?)null,
				maximumValue = (long?)null,
				mask = (ulong?)null,
				referenceObservationId = (string?)null
			} },
			captureFinalScreenshot = false,
			failFast = false
		}));

		Assert.Equal(3, logs.Count);
		Assert.All(logs, log => Assert.Matches(
			@"^\[MCP\] Tool [a-z_]+ (succeeded|failed with [a-z_]+) in \d+ ms\.$", log));
		string combined = string.Join('\n', logs);
		Assert.DoesNotContain("private", combined);
		Assert.DoesNotContain("173", combined);
		Assert.DoesNotContain(Convert.ToBase64String(state), combined);
		Assert.DoesNotContain(Convert.ToBase64String(png), combined);
	}

	[Fact]
	public async Task Stop_WhenHostCleanupExceedsTimeout_DetachesHostAndCleansServiceExactlyOnce()
	{
		TaskCompletionSource releaseHostCleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource hostCleanupCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
		using ManualResetEventSlim hostCleanupEntered = new();
		int hostCleanupCalls = 0;
		int debuggerReleaseCalls = 0;
		FakeMcpEmulatorApi api = CreateRunningApi();
		TrackingBreakpointCollection breakpoints = new();
		McpEmulatorService service = new(
			api,
			debuggerLifetime: new DebuggerLifetimeCoordinator(() => { }, () => debuggerReleaseCalls++),
			breakpointCollection: breakpoints
		);
		using McpServer server = new(
			service,
			applicationCleanup: async (application, _) => {
				Interlocked.Increment(ref hostCleanupCalls);
				hostCleanupEntered.Set();
				await releaseHostCleanup.Task;
				await application.DisposeAsync();
				hostCleanupCompleted.SetResult();
			}
		);
		await server.StartAsync(0);
		Assert.True(service.SetBreakpoint(
			nameof(CpuType.Nes),
			nameof(MemoryType.NesWorkRam),
			"write",
			0,
			null,
			null
		).IsSuccess);

		long started = Environment.TickCount64;
		server.Stop(TimeSpan.FromMilliseconds(100));
		long elapsed = Environment.TickCount64 - started;

		Assert.True(hostCleanupEntered.IsSet);
		Assert.InRange(elapsed, 0, 1000);
		Assert.Equal(1, hostCleanupCalls);
		Assert.Equal(0, breakpoints.DisposeCalls);
		Assert.Equal(0, breakpoints.DetachCalls);
		Assert.Equal(0, breakpoints.EmptyReplaceCalls);
		Assert.Equal(0, debuggerReleaseCalls);
		Assert.Equal(0, api.ClearExclusiveControllerOverridesCalls);
		Assert.Equal("server_stopping", service.GetStatus().Error!.Code);
		Assert.Throws<InvalidOperationException>(() => server.Endpoint);
		Action restart = () => { _ = server.StartAsync(0); };
		Assert.IsType<InvalidOperationException>(Record.Exception(restart));
		int nativeCallsAfterStop = NativeCallCount(api);

		releaseHostCleanup.SetResult();
		await hostCleanupCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		server.CompleteCoreRelease();

		Assert.Equal(nativeCallsAfterStop, NativeCallCount(api));
		server.Stop(TimeSpan.Zero);
		server.Dispose();
		Assert.Equal(1, hostCleanupCalls);
		Assert.Equal(0, breakpoints.DisposeCalls);
		Assert.Equal(1, breakpoints.DetachCalls);
		Assert.Equal(0, breakpoints.EmptyReplaceCalls);
		Assert.Equal(0, debuggerReleaseCalls);
	}

	[Fact]
	public async Task AutomationStop_WithBlockedNativeCall_IsBoundedAndNeverStartsNativeCleanup()
	{
		using ManualResetEventSlim readEntered = new();
		using ManualResetEventSlim releaseRead = new();
		FakeMcpEmulatorApi api = CreateRunningApi();
		api.OnRead = () => {
			readEntered.Set();
			releaseRead.Wait(TimeSpan.FromSeconds(5));
		};
		TrackingBreakpointCollection breakpoints = new();
		McpEmulatorService service = new(api, breakpointCollection: breakpoints);
		McpServer server = new(service);
		McpStateIdentity identity = server.EmulatorIdentity.Current;
		Assert.True(server.SaveStates.Create([1], identity, DateTimeOffset.UnixEpoch).IsSuccess);
		Task<McpServiceResult<MemoryRead>> read = Task.Run(() =>
			service.ReadMemory(nameof(MemoryType.NesWorkRam), 0, 1));
		Assert.True(readEntered.Wait(TimeSpan.FromSeconds(5)));

		long started = Environment.TickCount64;
		server.Stop(TimeSpan.FromMilliseconds(100));
		long elapsed = Environment.TickCount64 - started;

		Assert.InRange(elapsed, 0, 1000);
		Assert.Throws<ObjectDisposedException>(() => server.SaveStates.Pin("missing"));
		Assert.Equal(0, breakpoints.DisposeCalls);
		Assert.Equal(0, breakpoints.DetachCalls);
		Assert.Equal(0, api.ClearExclusiveControllerOverridesCalls);
		Assert.Throws<InvalidOperationException>(() => server.Endpoint);
		Action restart = () => { _ = server.StartAsync(0); };
		Assert.IsType<InvalidOperationException>(Record.Exception(restart));

		started = Environment.TickCount64;
		server.Stop(TimeSpan.Zero);
		server.Dispose();
		Assert.InRange(Environment.TickCount64 - started, 0, 500);
		Assert.Equal(0, breakpoints.DisposeCalls);
		Assert.Equal(0, api.ClearExclusiveControllerOverridesCalls);

		releaseRead.Set();
		await read.WaitAsync(TimeSpan.FromSeconds(5));
		server.CompleteCoreRelease();
		Assert.Equal(0, breakpoints.DisposeCalls);
		Assert.Equal(1, breakpoints.DetachCalls);
		Assert.Equal(0, api.ClearExclusiveControllerOverridesCalls);
	}

	[Fact]
	public void AutomationStop_DetachesManagedOwnershipWithoutNativeDebuggerCalls()
	{
		FakeMcpEmulatorApi api = CreateRunningApi();
		TrackingBreakpointCollection breakpoints = new();
		int debuggerInitializeCalls = 0;
		int debuggerReleaseCalls = 0;
		TraceLoggerCoordinator traceCoordinator = new();
		McpEmulatorService service = new(
			api,
			debuggerLifetime: new DebuggerLifetimeCoordinator(() => debuggerInitializeCalls++, () => debuggerReleaseCalls++),
			breakpointCollection: breakpoints,
			traceCoordinator: traceCoordinator);
		McpServer server = new(service);
		McpStateIdentity identity = server.EmulatorIdentity.Current;
		Assert.True(server.SaveStates.Create([1], identity, DateTimeOffset.UnixEpoch).IsSuccess);
		Assert.True(service.SetBreakpoint(nameof(CpuType.Nes), nameof(MemoryType.NesWorkRam), "write", 0, null, null).IsSuccess);
		Assert.True(service.ConfigureExecutionTrace(nameof(CpuType.Nes), "enable", false, false, null, null).IsSuccess);
		object otherTraceOwner = new();
		Assert.False(traceCoordinator.TryAcquireAndExecute(otherTraceOwner, () => { }));
		int setTraceCalls = api.SetTraceOptionsCalls;

		server.Stop(TimeSpan.FromSeconds(1));

		Assert.Throws<ObjectDisposedException>(() => server.SaveStates.Pin("missing"));
		Assert.Equal("server_stopping", service.GetStatus().Error?.Code);
		Assert.Equal(0, breakpoints.DetachCalls);
		Assert.Equal(0, breakpoints.DisposeCalls);
		Assert.Equal(0, breakpoints.EmptyReplaceCalls);
		Assert.Equal(1, debuggerInitializeCalls);
		Assert.Equal(0, debuggerReleaseCalls);
		Assert.Equal(setTraceCalls, api.SetTraceOptionsCalls);
		Assert.Equal(0, api.ClearExecutionTraceCalls);
		Assert.Equal(0, api.ClearExclusiveControllerOverridesCalls);
		Assert.False(traceCoordinator.TryAcquireAndExecute(otherTraceOwner, () => { }));

		server.CompleteCoreRelease();
		Assert.Equal(1, breakpoints.DetachCalls);
		Assert.True(traceCoordinator.TryAcquireAndExecute(otherTraceOwner, () => { }));

		server.Stop(TimeSpan.Zero);
		server.Dispose();
		Assert.Equal(1, breakpoints.DetachCalls);
		Assert.Equal(0, debuggerReleaseCalls);
		Assert.Equal(0, api.ClearExclusiveControllerOverridesCalls);
	}

	[Fact]
	public async Task AutomationStop_DoesNotWaitForTracePublicationLockBeforeCoreRelease()
	{
		using ManualResetEventSlim publicationEntered = new();
		using ManualResetEventSlim releasePublication = new();
		FakeMcpEmulatorApi api = CreateRunningApi();
		api.SetTraceOptionsHandler = (_, _) => {
			publicationEntered.Set();
			releasePublication.Wait(TimeSpan.FromSeconds(5));
		};
		TraceLoggerCoordinator traceCoordinator = new();
		TrackingBreakpointCollection breakpoints = new();
		McpEmulatorService service = new(
			api,
			debuggerLifetime: new DebuggerLifetimeCoordinator(() => { }, () => { }),
			breakpointCollection: breakpoints,
			traceCoordinator: traceCoordinator);
		McpServer server = new(service);
		McpStateIdentity identity = server.EmulatorIdentity.Current;
		Assert.True(server.SaveStates.Create([1], identity, DateTimeOffset.UnixEpoch).IsSuccess);
		Task<McpServiceResult<TraceConfiguration>> publication = Task.Run(() =>
			service.ConfigureExecutionTrace(nameof(CpuType.Nes), "enable", false, false, null, null));
		Assert.True(publicationEntered.Wait(TimeSpan.FromSeconds(5)));

		long started = Environment.TickCount64;
		server.Stop(TimeSpan.FromMilliseconds(100));

		Assert.InRange(Environment.TickCount64 - started, 0, 1000);
		Assert.Throws<ObjectDisposedException>(() => server.SaveStates.Pin("missing"));
		Assert.Equal(0, breakpoints.DetachCalls);
		Assert.Equal(0, api.ClearExecutionTraceCalls);
		Assert.Equal(0, api.ClearExclusiveControllerOverridesCalls);

		releasePublication.Set();
		Assert.Equal("server_stopping", (await publication.WaitAsync(TimeSpan.FromSeconds(5))).Error?.Code);
		server.CompleteCoreRelease();
		server.CompleteCoreRelease();
		Assert.Equal(1, breakpoints.DetachCalls);
		Assert.Equal(0, api.ClearExecutionTraceCalls);
		Assert.Equal(0, api.ClearExclusiveControllerOverridesCalls);
	}

	[Fact]
	public async Task AutomationStop_DoesNotWaitForBreakpointPublicationLockBeforeCoreRelease()
	{
		using ManualResetEventSlim publicationEntered = new();
		using ManualResetEventSlim releasePublication = new();
		FakeMcpEmulatorApi api = CreateRunningApi();
		TrackingBreakpointCollection breakpoints = new() {
			ReplaceHandler = () => {
				publicationEntered.Set();
				releasePublication.Wait(TimeSpan.FromSeconds(5));
			}
		};
		McpEmulatorService service = new(
			api,
			debuggerLifetime: new DebuggerLifetimeCoordinator(() => { }, () => { }),
			breakpointCollection: breakpoints);
		McpServer server = new(service);
		McpStateIdentity identity = server.EmulatorIdentity.Current;
		Assert.True(server.SaveStates.Create([1], identity, DateTimeOffset.UnixEpoch).IsSuccess);
		Task<McpServiceResult<McpBreakpoint>> publication = Task.Run(() =>
			service.SetBreakpoint(nameof(CpuType.Nes), nameof(MemoryType.NesWorkRam), "write", 0, null, null));
		Assert.True(publicationEntered.Wait(TimeSpan.FromSeconds(5)));

		long started = Environment.TickCount64;
		server.Stop(TimeSpan.FromMilliseconds(100));

		Assert.InRange(Environment.TickCount64 - started, 0, 1000);
		Assert.Throws<ObjectDisposedException>(() => server.SaveStates.Pin("missing"));
		Assert.Equal(0, breakpoints.DetachCalls);
		Assert.Equal(0, api.ClearExclusiveControllerOverridesCalls);

		releasePublication.Set();
		Assert.Equal("server_stopping", (await publication.WaitAsync(TimeSpan.FromSeconds(5))).Error?.Code);
		server.CompleteCoreRelease();
		server.Dispose();
		Assert.Equal(1, breakpoints.DetachCalls);
		Assert.Equal(0, breakpoints.DisposeCalls);
		Assert.Equal(0, api.ClearExclusiveControllerOverridesCalls);
	}

	private static async Task<McpClient> CreateClientAsync(Uri endpoint)
	{
		HttpClientTransport transport = new(new HttpClientTransportOptions {
			Endpoint = endpoint,
			TransportMode = HttpTransportMode.StreamableHttp
		});
		return await McpClient.CreateAsync(transport);
	}

	private static Dictionary<string, object?> Request(object request) => new() { ["request"] = request };

	private static int NativeCallCount(FakeMcpEmulatorApi api) =>
		api.DebuggerRequestBlockStateCalls
		+ api.IsRunningCalls
		+ api.IsPausedCalls
		+ api.GetRomInfoCalls
		+ api.GetMemorySizeCalls
		+ api.GetMemoryValuesCalls
		+ api.SetMemoryValuesCalls
		+ api.GetNesCpuStateCalls
		+ api.PauseCalls
		+ api.ResumeCalls
		+ api.ResumeDebuggerCalls
		+ api.IsExecutionStoppedCalls
		+ api.StepCalls
		+ api.GetDebuggerFeaturesCalls
		+ api.EvaluateExpressionCalls
		+ api.GetProgramCounterCalls
		+ api.GetDisassemblyOutputCalls
		+ api.GetDisassemblyRowAddressCalls
		+ api.GetAbsoluteAddressCalls
		+ api.GetRelativeAddressCalls
		+ api.GetCallstackCalls
		+ api.SetTraceOptionsCalls
		+ api.ClearExecutionTraceCalls
		+ api.GetExecutionTraceSizeCalls
		+ api.GetExecutionTraceCalls;

	private static JsonElement RequestProperties(IEnumerable<McpClientTool> tools, string toolName)
	{
		return tools.Single(tool => tool.Name == toolName).JsonSchema
			.GetProperty("properties")
			.GetProperty("request")
			.GetProperty("properties");
	}

	private static void SendBreak(McpEmulatorService service, BreakEvent breakEvent)
	{
		IntPtr pointer = Marshal.AllocHGlobal(Marshal.SizeOf<BreakEvent>());
		try {
			Marshal.StructureToPtr(breakEvent, pointer, false);
			service.ProcessNotification(new() {
				NotificationType = ConsoleNotificationType.CodeBreak,
				Parameter = pointer
			});
		} finally {
			Marshal.FreeHGlobal(pointer);
		}
	}

	private static void AssertPagingSchema(IEnumerable<McpClientTool> tools, string toolName)
	{
		AssertSchemaBound(tools, toolName, "offset", "minimum", 0);
		AssertSchemaBound(tools, toolName, "limit", "minimum", 1);
		AssertSchemaBound(tools, toolName, "limit", "maximum", McpAutomationLimits.MaxResultPage);
	}

	private static void AssertSchemaBound(
		IEnumerable<McpClientTool> tools, string toolName, string property, string bound, int expected)
	{
		Assert.Equal(expected, RequestProperties(tools, toolName).GetProperty(property).GetProperty(bound).GetInt32());
	}

	private static void AssertSchemaEnum(
		IEnumerable<McpClientTool> tools, string toolName, string property, params string[] expected)
	{
		Assert.Equal(
			expected,
			RequestProperties(tools, toolName).GetProperty(property).GetProperty("enum")
				.EnumerateArray().Select(value => value.GetString())
		);
	}

	private static void AssertResourceIdSchema(JsonElement schema, bool nullable = false)
	{
		JsonElement stringSchema = schema;
		if(schema.TryGetProperty("anyOf", out JsonElement anyOf)) {
			JsonElement[] branches = anyOf.EnumerateArray().ToArray();
			stringSchema = branches.Single(branch => branch.GetProperty("type").GetString() == "string");
			Assert.Equal(nullable, branches.Any(branch => branch.GetProperty("type").GetString() == "null"));
		} else if(schema.GetProperty("type").ValueKind == JsonValueKind.Array) {
			string?[] types = schema.GetProperty("type").EnumerateArray().Select(value => value.GetString()).ToArray();
			Assert.Contains("string", types);
			Assert.Equal(nullable, types.Contains("null"));
		} else {
			Assert.Equal("string", schema.GetProperty("type").GetString());
			Assert.False(nullable);
		}

		Assert.Equal(32, stringSchema.GetProperty("minLength").GetInt32());
		Assert.Equal(32, stringSchema.GetProperty("maxLength").GetInt32());
		Assert.Equal("^[0-9a-f]{32}$", stringSchema.GetProperty("pattern").GetString());
	}

	private static void AssertSchemaEnum(
		IEnumerable<McpClientTool> tools, string toolName, string property, params int[] expected)
	{
		Assert.Equal(
			expected,
			RequestProperties(tools, toolName).GetProperty(property).GetProperty("enum")
				.EnumerateArray().Select(value => value.GetInt32())
		);
	}

	private static void AssertInvalidByteValue(CallToolResult result)
	{
		Assert.True(result.IsError);
		JsonElement error = result.StructuredContent!.Value;
		Assert.Equal("invalid_byte_value", error.GetProperty("code").GetString());
		Assert.Equal("Write data must contain only integer values from 0 through 255.", error.GetProperty("message").GetString());
		Assert.False(error.TryGetProperty("bytesWritten", out _));
	}

	private static void AssertErrorCode(CallToolResult result, string code)
	{
		Assert.True(result.IsError);
		Assert.Equal(code, result.StructuredContent!.Value.GetProperty("code").GetString());
	}

	private static FakeMcpEmulatorApi CreateRunningApi()
	{
		FakeMcpEmulatorApi api = new() {
			Running = true,
			RomInfo = new RomInfo {
				ConsoleType = ConsoleType.Nes,
				RomPath = "game.nes",
				CpuTypes = [CpuType.Nes]
			},
			ReadData = [0xA1, 0xB2],
			NesCpuState = new NesCpuState { PC = 0x1234 }
		};
		api.MemorySizes[MemoryType.NesWorkRam] = 16;
		return api;
	}

	private sealed class ProtocolBreakpointCollection : IMcpBreakpointCollection
	{
		private readonly Dictionary<int, long> _stableIdsByNativeId = [];

		public void Replace(IReadOnlyList<BreakpointManager.ExternalBreakpoint> breakpoints)
		{
			_stableIdsByNativeId.Clear();
			for(int i = 0; i < breakpoints.Count; i++) {
				_stableIdsByNativeId.Add(i + 1, breakpoints[i].StableId);
			}
		}

		public bool TryGetStableId(int nativeBreakpointId, out long stableId) =>
			_stableIdsByNativeId.TryGetValue(nativeBreakpointId, out stableId);

		public void Dispose() { }
	}

	private sealed class TrackingBreakpointCollection : IMcpBreakpointCollection
	{
		public int EmptyReplaceCalls { get; private set; }
		public int DisposeCalls { get; private set; }
		public int DetachCalls { get; private set; }
		public Action? ReplaceHandler { get; init; }

		public void Replace(IReadOnlyList<BreakpointManager.ExternalBreakpoint> breakpoints)
		{
			ReplaceHandler?.Invoke();
			if(breakpoints.Count == 0) {
				EmptyReplaceCalls++;
			}
		}

		public bool TryGetStableId(int nativeBreakpointId, out long stableId)
		{
			stableId = 0;
			return false;
		}

		public void Dispose()
		{
			DisposeCalls++;
		}

		public void Detach()
		{
			DetachCalls++;
		}
	}

}
