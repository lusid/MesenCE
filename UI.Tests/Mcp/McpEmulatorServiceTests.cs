using System.Text.Json;
using Mesen.Interop;
using Mesen.Mcp;

namespace UI.Tests.Mcp;

public sealed class McpEmulatorServiceTests
{
	[Fact]
	public void GetStatus_WhenNoGameIsLoaded_DoesNotRequestRomInfo()
	{
		FakeMcpEmulatorApi api = new() { Running = false };
		McpEmulatorService service = new(api);

		McpServiceResult<EmulatorStatus> result = service.GetStatus();

		Assert.True(result.IsSuccess);
		Assert.False(result.Value!.GameLoaded);
		Assert.Equal("stopped", result.Value.State);
		Assert.Null(result.Value.System);
		Assert.Null(result.Value.RomName);
		Assert.Equal(0, api.GetRomInfoCalls);
	}

	[Fact]
	public void GetStatus_WhenGameIsLoaded_ReturnsDisplayMetadataWithoutPaths()
	{
		FakeMcpEmulatorApi api = new() {
			Running = true,
			RomInfo = new RomInfo {
				ConsoleType = ConsoleType.Nes,
				RomPath = "/private/roms/game.nes",
				PatchPath = "/private/patches/game.ips"
			}
		};
		McpEmulatorService service = new(api);

		McpServiceResult<EmulatorStatus> result = service.GetStatus();

		Assert.True(result.IsSuccess);
		Assert.True(result.Value!.GameLoaded);
		Assert.Equal(nameof(ConsoleType.Nes), result.Value.System);
		Assert.Equal("game", result.Value.RomName);
		Assert.Equal("running", result.Value.State);
		Assert.DoesNotContain("/private", JsonSerializer.Serialize(result.Value));
	}

	[Fact]
	public void GetStatus_WhenGameIsPaused_ReturnsPausedState()
	{
		FakeMcpEmulatorApi api = new() { Running = true, Paused = true };
		McpEmulatorService service = new(api);

		Assert.Equal("paused", service.GetStatus().Value!.State);
	}

	[Fact]
	public void GetCpuRegisters_WhenNoGameIsLoaded_DoesNotRequestCpuState()
	{
		FakeMcpEmulatorApi api = new() { Running = false };
		McpEmulatorService service = new(api);

		McpServiceResult<CpuRegisters> result = service.GetCpuRegisters();

		Assert.Equal("no_game", result.Error!.Code);
		Assert.Equal(0, api.GetRomInfoCalls);
		Assert.Equal(0, api.GetNesCpuStateCalls);
	}

	[Fact]
	public void GetCpuRegisters_WhenNesCpuIsUnavailable_ReturnsStructuredUnsupportedError()
	{
		FakeMcpEmulatorApi api = new() {
			Running = true,
			RomInfo = new RomInfo {
				ConsoleType = ConsoleType.Nes,
				CpuTypes = [CpuType.Snes]
			}
		};
		McpEmulatorService service = new(api);

		McpServiceResult<CpuRegisters> result = service.GetCpuRegisters();

		Assert.Equal("registers_not_supported", result.Error!.Code);
		Assert.Equal(0, api.GetNesCpuStateCalls);
	}

	[Fact]
	public void GetCpuRegisters_WhenNesCpuIsAvailable_ReturnsOrderedRawRegisterValues()
	{
		FakeMcpEmulatorApi api = new() {
			Running = true,
			RomInfo = new RomInfo {
				ConsoleType = ConsoleType.Nes,
				CpuTypes = [CpuType.Nes]
			},
			NesCpuState = new NesCpuState {
				A = 0x0A,
				X = 0xB1,
				Y = 0x02,
				SP = 0x03,
				PC = 0x04D5,
				PS = 0x13,
				IRQFlag = 0x06,
				NMIFlag = true,
				CycleCount = 0x789ABCDEF
			}
		};
		McpEmulatorService service = new(api);

		McpServiceResult<CpuRegisters> result = service.GetCpuRegisters();

		Assert.True(result.IsSuccess);
		Assert.Equal(nameof(ConsoleType.Nes), result.Value!.System);
		Assert.Equal(nameof(CpuType.Nes), result.Value.Cpu);
		Assert.Equal("6502", result.Value.Architecture);
		Assert.Collection(
			result.Value.Registers,
			register => AssertRegister(register, "A", 0x0A, 8, "0A"),
			register => AssertRegister(register, "X", 0xB1, 8, "B1"),
			register => AssertRegister(register, "Y", 0x02, 8, "02"),
			register => AssertRegister(register, "SP", 0x03, 8, "03"),
			register => AssertRegister(register, "PC", 0x04D5, 16, "04D5"),
			register => AssertRegister(register, "PS", 0x13, 8, "13"),
			register => AssertRegister(register, "IRQFlag", 0x06, 8, "06"),
			register => AssertRegister(register, "NMIFlag", 1, 1, "1"),
			register => AssertRegister(register, "CycleCount", 0x789ABCDEF, 64, "0000000789ABCDEF")
		);
		Assert.Equal(1, api.GetNesCpuStateCalls);
	}

	[Fact]
	public void ListMemorySpaces_ReturnsOnlyPositiveActiveEnumValues()
	{
		FakeMcpEmulatorApi api = new() { Running = true };
		api.MemorySizes[MemoryType.NesMemory] = 0x10000;
		api.MemorySizes[MemoryType.NesPrgRom] = 0x8000;
		api.MemorySizes[MemoryType.NesWorkRam] = 0;
		api.MemorySizes[MemoryType.None] = 1;
		McpEmulatorService service = new(api);

		IReadOnlyList<MemorySpace> spaces = service.ListMemorySpaces().Value!;

		Assert.Equal([nameof(MemoryType.NesMemory), nameof(MemoryType.NesPrgRom)], spaces.Select(x => x.Id));
		Assert.Equal(["CPU", "PRG"], spaces.Select(x => x.DisplayName));
		Assert.DoesNotContain(spaces, x => x.Id == nameof(MemoryType.None));
		Assert.All(spaces, x => Assert.True(x.Size > 0));
		Assert.All(spaces, x => Assert.True(x.CanRead));
	}

	[Fact]
	public void MemoryCapabilities_ClassifyEveryMemoryTypeAndKnownNoOpSpacesAsReadOnly()
	{
		MemoryType[] readOnly = [
			MemoryType.NecDspMemory,
			MemoryType.SnesPrgRom, MemoryType.SnesRegister, MemoryType.SpcRom,
			MemoryType.DspProgramRom, MemoryType.DspDataRom,
			MemoryType.St018PrgRom, MemoryType.St018DataRom,
			MemoryType.SufamiTurboFirmware, MemoryType.SufamiTurboSecondCart,
			MemoryType.GbPrgRom, MemoryType.GbBootRom,
			MemoryType.NesPrgRom, MemoryType.NesChrRom,
			MemoryType.PcePrgRom,
			MemoryType.SmsPrgRom, MemoryType.SmsBootRom, MemoryType.SmsPort,
			MemoryType.GbaPrgRom, MemoryType.GbaBootRom,
			MemoryType.WsPrgRom, MemoryType.WsBootRom, MemoryType.WsPort,
			MemoryType.None
		];

		foreach(MemoryType type in Enum.GetValues<MemoryType>()) {
			Assert.Equal(!readOnly.Contains(type), McpMemoryCapabilities.CanWrite(type));
		}

		FakeMcpEmulatorApi api = new() { Running = true, DefaultMemorySize = 1 };
		IReadOnlyList<MemorySpace> spaces = new McpEmulatorService(api).ListMemorySpaces().Value!;
		Assert.Contains(spaces, x => x.Id == nameof(MemoryType.SnesRegister) && !x.CanWrite);
		Assert.Contains(spaces, x => x.Id == nameof(MemoryType.SmsPort) && !x.CanWrite);
		Assert.Contains(spaces, x => x.Id == nameof(MemoryType.WsPort) && !x.CanWrite);
		Assert.Contains(spaces, x => x.Id == nameof(MemoryType.WsBootRom) && !x.CanWrite);
		Assert.Contains(spaces, x => x.Id == nameof(MemoryType.NesWorkRam) && x.CanWrite);
		Assert.Contains(spaces, x => x.Id == nameof(MemoryType.NesMemory) && x.CanWrite);
	}

	[Fact]
	public void ReadAndWriteMemory_WhenNoGameIsLoaded_DoNotAccessMemory()
	{
		FakeMcpEmulatorApi api = new() { Running = false };
		McpEmulatorService service = new(api);

		McpServiceResult<MemoryRead> read = service.ReadMemory(nameof(MemoryType.NesWorkRam), 0, 1);
		McpServiceResult<MemoryWrite> write = service.WriteMemory(nameof(MemoryType.NesWorkRam), 0, [1]);

		Assert.Equal("no_game", read.Error!.Code);
		Assert.Equal("no_game", write.Error!.Code);
		Assert.Equal(0, api.GetMemoryValuesCalls);
		Assert.Equal(0, api.SetMemoryValuesCalls);
	}

	[Theory]
	[InlineData("nesWorkRam")]
	[InlineData("None")]
	public void ReadMemory_WhenSpaceIdIsNotAnExactNamedActiveSpace_DoesNotRead(string id)
	{
		FakeMcpEmulatorApi api = CreateApiWithMemory(MemoryType.NesWorkRam, 16);
		McpEmulatorService service = new(api);

		McpServiceResult<MemoryRead> result = service.ReadMemory(id, 0, 1);

		Assert.Equal("unknown_memory_space", result.Error!.Code);
		Assert.Equal(0, api.GetMemoryValuesCalls);
	}

	[Fact]
	public void ReadMemory_WhenSpaceIdIsAnEnumOrdinal_DoesNotExposeOrAcceptIt()
	{
		FakeMcpEmulatorApi api = CreateApiWithMemory(MemoryType.NesWorkRam, 16);
		McpEmulatorService service = new(api);
		string ordinal = ((int)MemoryType.NesWorkRam).ToString();

		McpServiceResult<MemoryRead> result = service.ReadMemory(ordinal, 0, 1);

		Assert.Equal("unknown_memory_space", result.Error!.Code);
		Assert.Equal(0, api.GetMemoryValuesCalls);
	}

	[Theory]
	[InlineData(0U, -1)]
	[InlineData(0U, 0)]
	[InlineData(16U, 1)]
	[InlineData(uint.MaxValue, 2)]
	[InlineData(15U, 2)]
	public void ReadMemory_WhenRangeIsInvalid_DoesNotRead(uint address, int count)
	{
		FakeMcpEmulatorApi api = CreateApiWithMemory(MemoryType.NesWorkRam, 16);
		McpEmulatorService service = new(api);

		McpServiceResult<MemoryRead> result = service.ReadMemory(nameof(MemoryType.NesWorkRam), address, count);

		Assert.Equal("invalid_range", result.Error!.Code);
		Assert.Equal(0, api.GetMemoryValuesCalls);
	}

	[Fact]
	public void ReadMemory_WhenCountExceedsLimit_ReturnsPayloadTooLargeBeforeRangeValidation()
	{
		FakeMcpEmulatorApi api = new() { Running = true };
		McpEmulatorService service = new(api);

		McpServiceResult<MemoryRead> result = service.ReadMemory("invalid", uint.MaxValue, McpEmulatorService.MaxTransferSize + 1);

		Assert.Equal("payload_too_large", result.Error!.Code);
		Assert.Equal(0, api.DebuggerRequestBlockStateCalls);
		Assert.Equal(0, api.IsRunningCalls);
		Assert.Equal(0, api.GetMemoryValuesCalls);
	}

	[Fact]
	public void ReadMemory_AllowsTheFinalByteAndReturnsUppercaseHex()
	{
		FakeMcpEmulatorApi api = CreateApiWithMemory(MemoryType.NesWorkRam, 16);
		api.ReadData = [0x0A];
		McpEmulatorService service = new(api);

		McpServiceResult<MemoryRead> result = service.ReadMemory(nameof(MemoryType.NesWorkRam), 15, 1);

		Assert.True(result.IsSuccess);
		Assert.Equal(15U, api.LastReadStart);
		Assert.Equal(15U, api.LastReadEndInclusive);
		Assert.Equal("0A", result.Value!.Hex);
		Assert.Equal([0x0A], result.Value.Data);
	}

	[Theory]
	[InlineData(16U, 1)]
	[InlineData(uint.MaxValue, 2)]
	[InlineData(15U, 2)]
	public void WriteMemory_WhenRangeIsInvalid_DoesNotWrite(uint address, int dataLength)
	{
		FakeMcpEmulatorApi api = CreateApiWithMemory(MemoryType.NesWorkRam, 16);
		McpEmulatorService service = new(api);

		McpServiceResult<MemoryWrite> result = service.WriteMemory(nameof(MemoryType.NesWorkRam), address, new byte[dataLength]);

		Assert.Equal("invalid_range", result.Error!.Code);
		Assert.Equal(0, api.SetMemoryValuesCalls);
	}

	[Fact]
	public void WriteMemory_WhenDataIsEmptyOrAboveLimit_DoesNotWrite()
	{
		FakeMcpEmulatorApi api = CreateApiWithMemory(MemoryType.NesWorkRam, int.MaxValue);
		McpEmulatorService service = new(api);

		Assert.Equal("invalid_range", service.WriteMemory(nameof(MemoryType.NesWorkRam), 0, []).Error!.Code);
		Assert.Equal("invalid_range", service.WriteMemory(nameof(MemoryType.NesWorkRam), 0, new byte[McpEmulatorService.MaxTransferSize + 1]).Error!.Code);
		Assert.Equal(0, api.SetMemoryValuesCalls);
	}

	[Fact]
	public void WriteMemory_WhenSpaceIsReadOnly_DoesNotWrite()
	{
		FakeMcpEmulatorApi api = CreateApiWithMemory(MemoryType.NesPrgRom, 16);
		McpEmulatorService service = new(api);

		McpServiceResult<MemoryWrite> result = service.WriteMemory(nameof(MemoryType.NesPrgRom), 0, [1]);

		Assert.Equal("memory_space_read_only", result.Error!.Code);
		Assert.Equal(0, api.SetMemoryValuesCalls);
	}

	[Theory]
	[InlineData(MemoryType.SnesRegister)]
	[InlineData(MemoryType.SmsPort)]
	[InlineData(MemoryType.WsPort)]
	[InlineData(MemoryType.WsBootRom)]
	[InlineData(MemoryType.NecDspMemory)]
	public void WriteMemory_WhenNativeBulkSetterCannotWriteWholeSpace_RejectsWithoutInterop(MemoryType type)
	{
		FakeMcpEmulatorApi api = CreateApiWithMemory(type, 16);
		McpServiceResult<MemoryWrite> result = new McpEmulatorService(api).WriteMemory(type.ToString(), 0, [1]);

		Assert.Equal("memory_space_read_only", result.Error!.Code);
		Assert.Equal(0, api.SetMemoryValuesCalls);
	}

	[Fact]
	public void WriteMemory_WhenSpaceIdIsNotAnExactNamedActiveSpace_DoesNotWrite()
	{
		FakeMcpEmulatorApi api = CreateApiWithMemory(MemoryType.NesWorkRam, 16);
		McpEmulatorService service = new(api);

		McpServiceResult<MemoryWrite> result = service.WriteMemory("nesWorkRam", 0, [1]);

		Assert.Equal("unknown_memory_space", result.Error!.Code);
		Assert.Equal(0, api.SetMemoryValuesCalls);
	}

	[Fact]
	public void WriteMemory_FullyValidatesThenPerformsOneBulkFinalByteWrite()
	{
		FakeMcpEmulatorApi api = CreateApiWithMemory(MemoryType.NesWorkRam, 16);
		McpEmulatorService service = new(api);

		McpServiceResult<MemoryWrite> result = service.WriteMemory(nameof(MemoryType.NesWorkRam), 15, [0xAB]);

		Assert.True(result.IsSuccess);
		Assert.Equal(1, api.SetMemoryValuesCalls);
		Assert.Equal(15U, api.LastWriteStart);
		Assert.Equal([0xAB], api.LastWriteData);
		Assert.Equal(1, result.Value!.Count);
	}

	[Fact]
	public async Task EmulatorOperations_AreSerializedAcrossServiceMethods()
	{
		using ManualResetEventSlim readEntered = new();
		using ManualResetEventSlim releaseRead = new();
		using ManualResetEventSlim statusStarted = new();
		FakeMcpEmulatorApi api = CreateApiWithMemory(MemoryType.NesWorkRam, 16);
		api.OnRead = () => {
			readEntered.Set();
			releaseRead.Wait(TimeSpan.FromSeconds(5));
		};
		McpEmulatorService service = new(api);

		Task<McpServiceResult<MemoryRead>> read = Task.Run(() => service.ReadMemory(nameof(MemoryType.NesWorkRam), 0, 1));
		Assert.True(readEntered.Wait(TimeSpan.FromSeconds(5)));
		Task<McpServiceResult<EmulatorStatus>> status = Task.Run(() => {
			statusStarted.Set();
			return service.GetStatus();
		});
		Assert.True(statusStarted.Wait(TimeSpan.FromSeconds(5)));
		Task completed = await Task.WhenAny(status, Task.Delay(100));
		Assert.NotSame(status, completed);

		releaseRead.Set();
		await Task.WhenAll(read, status).WaitAsync(TimeSpan.FromSeconds(5));
		Assert.True((await read).IsSuccess);
		Assert.True((await status).IsSuccess);
	}

	[Fact]
	public void EmulatorOperation_WhenTransitionBeginsDuringInterop_ReturnsStateChanged()
	{
		FakeMcpEmulatorApi api = CreateApiWithMemory(MemoryType.NesWorkRam, 16);
		McpEmulatorService service = new(api);
		api.OnRead = service.BeginEmulatorTransition;

		McpServiceResult<MemoryRead> result = service.ReadMemory(nameof(MemoryType.NesWorkRam), 0, 1);

		Assert.Equal("state_changed", result.Error!.Code);
	}

	[Fact]
	public void EmulatorOperation_WhenTransitionBeginsAndEndsDuringInterop_ReturnsStateChanged()
	{
		FakeMcpEmulatorApi api = CreateApiWithMemory(MemoryType.NesWorkRam, 16);
		McpEmulatorService service = new(api);
		api.OnRead = () => {
			service.BeginEmulatorTransition();
			service.EndEmulatorTransition();
		};

		McpServiceResult<MemoryRead> result = service.ReadMemory(nameof(MemoryType.NesWorkRam), 0, 1);

		Assert.Equal("state_changed", result.Error!.Code);
	}

	[Fact]
	public void EmulatorOperation_WhenTransitionIsActive_RejectsBeforeNativeDataAccessAndRecoversAfterEnd()
	{
		FakeMcpEmulatorApi api = CreateApiWithMemory(MemoryType.NesWorkRam, 16);
		McpEmulatorService service = new(api);
		service.BeginEmulatorTransition();

		McpServiceResult<MemoryRead> blocked = service.ReadMemory(nameof(MemoryType.NesWorkRam), 0, 1);

		Assert.Equal("state_changed", blocked.Error!.Code);
		Assert.Equal(0, api.IsRunningCalls);
		Assert.Equal(0, api.GetMemoryValuesCalls);

		service.EndEmulatorTransition();
		Assert.True(service.ReadMemory(nameof(MemoryType.NesWorkRam), 0, 1).IsSuccess);
		Assert.Equal(1, api.GetMemoryValuesCalls);
	}

	[Fact]
	public void EmulatorOperation_WhenDebuggerRequestsAreBlocked_RejectsBeforeNativeDataAccess()
	{
		FakeMcpEmulatorApi api = CreateApiWithMemory(MemoryType.NesWorkRam, 16);
		api.SetDebuggerRequestBlocked(true);
		McpEmulatorService service = new(api);

		McpServiceResult<MemoryRead> result = service.ReadMemory(nameof(MemoryType.NesWorkRam), 0, 1);

		Assert.Equal("state_changed", result.Error!.Code);
		Assert.Equal(0, api.IsRunningCalls);
		Assert.Equal(0, api.GetMemoryValuesCalls);
	}

	[Fact]
	public void EmulatorOperation_BeginningDuringResetMutation_ReturnsStateChangedAndRecoversAfterward()
	{
		FakeMcpEmulatorApi api = CreateApiWithMemory(MemoryType.NesWorkRam, 16);
		api.SetDebuggerRequestBlocked(true);
		McpEmulatorService service = new(api);

		Assert.Equal("state_changed", service.ReadMemory(nameof(MemoryType.NesWorkRam), 0, 1).Error!.Code);
		Assert.Equal(0, api.GetMemoryValuesCalls);

		api.SetDebuggerRequestBlocked(false);
		Assert.True(service.ReadMemory(nameof(MemoryType.NesWorkRam), 0, 1).IsSuccess);
		Assert.Equal(1, api.GetMemoryValuesCalls);
	}

	[Fact]
	public void EmulatorOperation_BeginningDuringDeserializeMutation_ReturnsStateChangedAndRecoversAfterward()
	{
		FakeMcpEmulatorApi api = CreateApiWithMemory(MemoryType.NesWorkRam, 16);
		api.SetDebuggerRequestBlocked(true);
		McpEmulatorService service = new(api);

		Assert.Equal("state_changed", service.GetCpuRegisters().Error!.Code);
		Assert.Equal(0, api.GetNesCpuStateCalls);

		api.SetDebuggerRequestBlocked(false);
		Assert.True(service.ReadMemory(nameof(MemoryType.NesWorkRam), 0, 1).IsSuccess);
		Assert.Equal(1, api.GetMemoryValuesCalls);
	}

	[Fact]
	public void EmulatorOperation_WhenDebuggerBlockStartsAndEndsAroundDefaultInteropResult_ReturnsStateChanged()
	{
		FakeMcpEmulatorApi api = CreateApiWithMemory(MemoryType.NesWorkRam, 16);
		api.OnRead = () => {
			api.SetDebuggerRequestBlocked(true);
			api.ReadData = [];
			api.SetDebuggerRequestBlocked(false);
		};
		McpEmulatorService service = new(api);

		McpServiceResult<MemoryRead> result = service.ReadMemory(nameof(MemoryType.NesWorkRam), 0, 1);

		Assert.Equal("state_changed", result.Error!.Code);
	}

	[Fact]
	public void EmulatorOperation_WhenInteropThrows_ReturnsInteropFailure()
	{
		FakeMcpEmulatorApi api = CreateApiWithMemory(MemoryType.NesWorkRam, 16);
		api.OnRead = () => throw new InvalidOperationException("native details");

		McpServiceResult<MemoryRead> result = new McpEmulatorService(api).ReadMemory(nameof(MemoryType.NesWorkRam), 0, 1);

		Assert.Equal("interop_failure", result.Error!.Code);
		Assert.DoesNotContain("native details", result.Error.Message);
	}

	[Fact]
	public void EmulatorOperation_WhenInitialDebuggerBlockSnapshotThrows_ReturnsSanitizedInteropFailure()
	{
		FakeMcpEmulatorApi api = CreateApiWithMemory(MemoryType.NesWorkRam, 16);
		api.ThrowOnDebuggerRequestBlockStateCall = 1;

		McpServiceResult<MemoryRead> result = new McpEmulatorService(api).ReadMemory(nameof(MemoryType.NesWorkRam), 0, 1);

		Assert.Equal("interop_failure", result.Error!.Code);
		Assert.Equal("Native emulator interop failed.", result.Error.Message);
		Assert.Equal(0, api.IsRunningCalls);
		Assert.Equal(0, api.GetMemoryValuesCalls);
	}

	[Fact]
	public void EmulatorOperation_WhenFinalDebuggerBlockSnapshotThrows_ReturnsSanitizedInteropFailure()
	{
		FakeMcpEmulatorApi api = CreateApiWithMemory(MemoryType.NesWorkRam, 16);
		api.ThrowOnDebuggerRequestBlockStateCall = 2;

		McpServiceResult<MemoryRead> result = new McpEmulatorService(api).ReadMemory(nameof(MemoryType.NesWorkRam), 0, 1);

		Assert.Equal("interop_failure", result.Error!.Code);
		Assert.Equal("Native emulator interop failed.", result.Error.Message);
		Assert.Equal(1, api.GetMemoryValuesCalls);
	}

	[Fact]
	public async Task Shutdown_RejectsQueuedOperationAndDrainsActiveInteropBeforeRelease()
	{
		using ManualResetEventSlim readEntered = new();
		using ManualResetEventSlim releaseRead = new();
		using ManualResetEventSlim readExited = new();
		FakeMcpEmulatorApi api = CreateApiWithMemory(MemoryType.NesWorkRam, 16);
		api.OnRead = () => {
			readEntered.Set();
			releaseRead.Wait(TimeSpan.FromSeconds(5));
			readExited.Set();
		};
		McpEmulatorService service = new(api);

		Task<McpServiceResult<MemoryRead>> active = Task.Run(() => service.ReadMemory(nameof(MemoryType.NesWorkRam), 0, 1));
		Assert.True(readEntered.Wait(TimeSpan.FromSeconds(5)));
		Task<McpServiceResult<EmulatorStatus>> queued = Task.Run(service.GetStatus);
		await Task.Delay(100);
		service.BeginShutdown();
		Task drain = Task.Run(service.DrainOperations);
		Assert.False(drain.IsCompleted);

		releaseRead.Set();
		await drain.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.True(readExited.IsSet);
		Assert.True((await active).IsSuccess);
		Assert.Equal("server_stopping", (await queued).Error!.Code);
		Assert.Equal(1, api.IsRunningCalls);
	}

	private static FakeMcpEmulatorApi CreateApiWithMemory(MemoryType type, int size)
	{
		FakeMcpEmulatorApi api = new() { Running = true };
		api.MemorySizes[type] = size;
		return api;
	}

	private static void AssertRegister(CpuRegister register, string name, ulong value, int bits, string hex)
	{
		Assert.Equal(name, register.Name);
		Assert.Equal(value, register.Value);
		Assert.Equal(bits, register.Bits);
		Assert.Equal(hex, register.Hex);
	}

	private sealed class FakeMcpEmulatorApi : IMcpEmulatorApi
	{
		public bool Running { get; init; }
		public bool Paused { get; init; }
		public RomInfo RomInfo { get; init; } = new() { ConsoleType = ConsoleType.Nes, RomPath = "game.nes" };
		public Dictionary<MemoryType, int> MemorySizes { get; } = [];
		public int DefaultMemorySize { get; init; }
		public byte[] ReadData { get; set; } = [0];
		public NesCpuState NesCpuState { get; init; }
		public Action? OnRead { get; set; }
		public ulong DebuggerRequestBlockState { get; private set; }
		public int DebuggerRequestBlockStateCalls { get; private set; }
		public int ThrowOnDebuggerRequestBlockStateCall { get; set; }
		public int IsRunningCalls { get; private set; }
		public int GetRomInfoCalls { get; private set; }
		public int GetMemoryValuesCalls { get; private set; }
		public int SetMemoryValuesCalls { get; private set; }
		public int GetNesCpuStateCalls { get; private set; }
		public uint LastReadStart { get; private set; }
		public uint LastReadEndInclusive { get; private set; }
		public uint LastWriteStart { get; private set; }
		public byte[]? LastWriteData { get; private set; }

		public bool IsRunning()
		{
			IsRunningCalls++;
			return Running;
		}

		public ulong GetDebuggerRequestBlockState()
		{
			DebuggerRequestBlockStateCalls++;
			if(DebuggerRequestBlockStateCalls == ThrowOnDebuggerRequestBlockStateCall) {
				throw new InvalidOperationException("native snapshot details");
			}
			return DebuggerRequestBlockState;
		}

		public void SetDebuggerRequestBlocked(bool blocked)
		{
			DebuggerRequestBlockState = (((DebuggerRequestBlockState >> 1) + 1) << 1) | (blocked ? 1UL : 0);
		}

		public bool IsPaused() => Paused;

		public RomInfo GetRomInfo()
		{
			GetRomInfoCalls++;
			return RomInfo;
		}

		public int GetMemorySize(MemoryType type) => MemorySizes.GetValueOrDefault(type, DefaultMemorySize);

		public byte[] GetMemoryValues(MemoryType type, uint start, uint endInclusive)
		{
			GetMemoryValuesCalls++;
			LastReadStart = start;
			LastReadEndInclusive = endInclusive;
			OnRead?.Invoke();
			return ReadData;
		}

		public void SetMemoryValues(MemoryType type, uint start, byte[] data)
		{
			SetMemoryValuesCalls++;
			LastWriteStart = start;
			LastWriteData = data;
		}

		public NesCpuState GetNesCpuState()
		{
			GetNesCpuStateCalls++;
			return NesCpuState;
		}
	}
}
