using System.Net;
using System.Text.Json;
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
			"disabled-config", "config", "start", "dispose", "listener-dispose", "core-stop", "core-release"
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

		Assert.Equal(["dispose", "core-stop", "core-release"], events);
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
	public async Task Discovery_ExposesExactlyNineteenToolsWithBoundedSchemasOnLoopbackMcpRoute()
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
				"configure_execution_trace", "continue_until_break", "disassemble", "get_break_context",
				"get_call_stack", "get_cpu_registers", "get_emulator_status", "get_execution_trace",
				"list_breakpoints", "list_memory_spaces", "map_address", "pause", "read_memory",
				"remove_all_breakpoints", "remove_breakpoint", "resume", "set_breakpoint", "step",
				"write_memory"
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

		HashSet<string> readOnlyTools = [
			"disassemble", "get_break_context", "get_call_stack", "get_cpu_registers", "get_emulator_status",
			"get_execution_trace", "list_breakpoints", "list_memory_spaces", "map_address", "read_memory"
		];
		Assert.All(tools, tool => Assert.Equal(readOnlyTools.Contains(tool.Name), tool.ProtocolTool.Annotations!.ReadOnlyHint));
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
			Assert.Equal(19, (await firstClient.ListToolsAsync()).Count);
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
		Assert.Equal(1, breakpoints.DetachCalls);
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
		Assert.Equal(1, breakpoints.DetachCalls);
		Assert.Equal(0, breakpoints.DisposeCalls);
		Assert.Equal(0, breakpoints.EmptyReplaceCalls);
		Assert.Equal(1, debuggerInitializeCalls);
		Assert.Equal(0, debuggerReleaseCalls);
		Assert.Equal(setTraceCalls, api.SetTraceOptionsCalls);
		Assert.Equal(0, api.ClearExecutionTraceCalls);
		Assert.Equal(0, api.ClearExclusiveControllerOverridesCalls);
		Assert.True(traceCoordinator.TryAcquireAndExecute(otherTraceOwner, () => { }));

		server.Stop(TimeSpan.Zero);
		server.Dispose();
		Assert.Equal(1, breakpoints.DetachCalls);
		Assert.Equal(0, debuggerReleaseCalls);
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

		public void Replace(IReadOnlyList<BreakpointManager.ExternalBreakpoint> breakpoints)
		{
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
