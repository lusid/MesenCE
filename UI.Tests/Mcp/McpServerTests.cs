using System.Net;
using System.Text.Json;
using Mesen.Interop;
using Mesen.Mcp;
using Mesen.Windows;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace UI.Tests.Mcp;

public sealed class McpServerTests
{
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
			"disabled-config", "config", "start", "dispose", "core-stop", "listener-dispose", "core-release"
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
	public async Task Discovery_ExposesExactlyFiveToolsOnLoopbackMcpRoute()
	{
		using McpServer server = new(new McpEmulatorService(new FakeMcpEmulatorApi()));
		await server.StartAsync(0);

		Assert.Equal(IPAddress.Loopback, IPAddress.Parse(server.Endpoint.Host));
		using HttpClient httpClient = new();
		Assert.Equal(HttpStatusCode.NotFound, (await httpClient.GetAsync(new Uri(server.Endpoint, "/unrelated"))).StatusCode);

		await using McpClient client = await CreateClientAsync(server.Endpoint);
		IList<McpClientTool> tools = await client.ListToolsAsync();

		Assert.Equal(
			["get_cpu_registers", "get_emulator_status", "list_memory_spaces", "read_memory", "write_memory"],
			[.. tools.Select(tool => tool.Name).Order()]
		);
		Assert.All(tools, tool => {
			Assert.Contains("live", tool.Description);
			Assert.Contains("without pausing", tool.Description);
		});
		Assert.Contains("modifies emulator state immediately", tools.Single(tool => tool.Name == "write_memory").Description);
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
			data = new byte[] { 0x44, 0x55 }
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
			Assert.Equal(5, (await firstClient.ListToolsAsync()).Count);
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
	public async Task Stop_WithBlockedToolCall_IsBoundedAndDoesNotResurrectHost()
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
		Uri endpoint = server.Endpoint;
		await using McpClient client = await CreateClientAsync(endpoint);
		Task<CallToolResult> read = client.CallToolAsync("read_memory", Request(new {
			space = nameof(MemoryType.NesWorkRam),
			address = 0U,
			count = 2
		})).AsTask();
		Assert.True(readEntered.Wait(TimeSpan.FromSeconds(5)));

		long started = Environment.TickCount64;
		server.Stop(TimeSpan.FromSeconds(30));

		Assert.InRange(Environment.TickCount64 - started, 0, 2500);
		Assert.Throws<InvalidOperationException>(() => server.Endpoint);
		Action restart = () => { _ = server.StartAsync(0); };
		Assert.IsType<InvalidOperationException>(Record.Exception(restart));
		releaseRead.Set();
		Exception requestFailure = await Assert.ThrowsAnyAsync<Exception>(async () => await read.WaitAsync(TimeSpan.FromSeconds(5)));
		Assert.IsNotType<TimeoutException>(requestFailure);
		server.Stop(TimeSpan.FromSeconds(2));
		Assert.Throws<InvalidOperationException>(() => server.Endpoint);
		using HttpClient httpClient = new();
		await Assert.ThrowsAsync<HttpRequestException>(() => httpClient.GetAsync(endpoint));
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

	private sealed class FakeMcpEmulatorApi : IMcpEmulatorApi
	{
		public bool Running { get; init; }
		public RomInfo RomInfo { get; init; } = new();
		public Dictionary<MemoryType, int> MemorySizes { get; } = [];
		public byte[] ReadData { get; init; } = [];
		public NesCpuState NesCpuState { get; init; }
		public Action? OnRead { get; set; }
		public int IsRunningCalls { get; private set; }
		public int GetMemoryValuesCalls { get; private set; }
		public int SetMemoryValuesCalls { get; private set; }

		public bool IsRunning()
		{
			IsRunningCalls++;
			return Running;
		}

		public bool IsPaused() => false;
		public RomInfo GetRomInfo() => RomInfo;
		public int GetMemorySize(MemoryType type) => MemorySizes.GetValueOrDefault(type);

		public byte[] GetMemoryValues(MemoryType type, uint start, uint endInclusive)
		{
			GetMemoryValuesCalls++;
			OnRead?.Invoke();
			return ReadData;
		}

		public void SetMemoryValues(MemoryType type, uint start, byte[] data)
		{
			SetMemoryValuesCalls++;
		}

		public NesCpuState GetNesCpuState() => NesCpuState;
	}
}
