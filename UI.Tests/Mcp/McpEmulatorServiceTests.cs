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
	public void ListMemorySpaces_UsesRomClassificationAsTheSingleReadOnlyRule()
	{
		FakeMcpEmulatorApi api = new() { Running = true, DefaultMemorySize = 1 };
		McpEmulatorService service = new(api);

		IReadOnlyList<MemorySpace> spaces = service.ListMemorySpaces().Value!;

		Assert.All(spaces, space => {
			MemoryType type = Enum.Parse<MemoryType>(space.Id);
			Assert.Equal(!type.IsRomMemory(), space.CanWrite);
		});
		Assert.Contains(spaces, x => x.Id == nameof(MemoryType.NesPrgRom) && !x.CanWrite);
		Assert.Contains(spaces, x => x.Id == nameof(MemoryType.NesWorkRam) && x.CanWrite);
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
	[InlineData(0U, McpEmulatorService.MaxTransferSize + 1)]
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

	private static FakeMcpEmulatorApi CreateApiWithMemory(MemoryType type, int size)
	{
		FakeMcpEmulatorApi api = new() { Running = true };
		api.MemorySizes[type] = size;
		return api;
	}

	private sealed class FakeMcpEmulatorApi : IMcpEmulatorApi
	{
		public bool Running { get; init; }
		public bool Paused { get; init; }
		public RomInfo RomInfo { get; init; } = new() { ConsoleType = ConsoleType.Nes, RomPath = "game.nes" };
		public Dictionary<MemoryType, int> MemorySizes { get; } = [];
		public int DefaultMemorySize { get; init; }
		public byte[] ReadData { get; set; } = [0];
		public Action? OnRead { get; set; }
		public int IsRunningCalls { get; private set; }
		public int GetRomInfoCalls { get; private set; }
		public int GetMemoryValuesCalls { get; private set; }
		public int SetMemoryValuesCalls { get; private set; }
		public uint LastReadStart { get; private set; }
		public uint LastReadEndInclusive { get; private set; }
		public uint LastWriteStart { get; private set; }
		public byte[]? LastWriteData { get; private set; }

		public bool IsRunning()
		{
			IsRunningCalls++;
			return Running;
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

		public NesCpuState GetNesCpuState() => default;
	}
}
