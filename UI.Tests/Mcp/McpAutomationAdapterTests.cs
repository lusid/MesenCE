using Mesen.Config;
using Mesen.Interop;
using Mesen.Mcp;

namespace UI.Tests.Mcp;

public sealed class McpAutomationAdapterTests
{
	private static readonly McpControllerTopology Pad1 = new(
		0,
		0,
		ControllerType.NesController,
		[
			new("a", 0, false),
			new("b", 1, false),
			new("start", 3, false),
			new("select", 2, false),
			new("up", 4, false),
			new("down", 5, false),
			new("left", 6, false),
			new("right", 7, false)
		]);

	[Fact]
	public void Capabilities_UseConfiguredNativePadMetadata()
	{
		FakeMcpEmulatorApi api = FakeMcpEmulatorApi.RunningNes();
		api.ControllerTopology = [Pad1];
		NesMcpAutomationAdapter adapter = new(api);

		McpAutomationCapabilities capabilities = adapter.GetCapabilities(api, new(4, 7));

		Assert.True(capabilities.DeterministicFrames);
		McpControllerCapability controller = Assert.Single(capabilities.Controllers);
		Assert.Equal(0, controller.Port);
		Assert.Equal("NesController", controller.DeviceType);
		Assert.True(controller.ExclusiveInput);
		Assert.Equal(
			[("a", 0), ("b", 1), ("start", 3), ("select", 2), ("up", 4), ("down", 5), ("left", 6), ("right", 7)],
			controller.Controls.Select(control => (control.Id, control.NativeId)));
	}

	[Fact]
	public void Capabilities_RejectUnsupportedConfiguredPeripheral()
	{
		FakeMcpEmulatorApi api = FakeMcpEmulatorApi.RunningNes();
		api.ControllerTopology = [new(0, 0, ControllerType.NesZapper, [new("trigger", 0, false)])];
		NesMcpAutomationAdapter adapter = new(api);

		McpAutomationCapabilities capabilities = adapter.GetCapabilities(api, default);

		Assert.False(capabilities.DeterministicFrames);
		Assert.False(Assert.Single(capabilities.Controllers).ExclusiveInput);
		Assert.Contains(capabilities.Limitations, limitation => limitation.Contains("NesZapper", StringComparison.Ordinal));
		Assert.Equal("unsupported_capability", adapter.ValidateInput([]).Error?.Code);
	}

	[Fact]
	public void Capabilities_DoNotAdvertiseDeterminismWithoutConfiguredControllers()
	{
		FakeMcpEmulatorApi api = FakeMcpEmulatorApi.RunningNes();
		NesMcpAutomationAdapter adapter = new(api);

		McpAutomationCapabilities capabilities = adapter.GetCapabilities(api, default);

		Assert.False(capabilities.DeterministicFrames);
		Assert.Equal("unsupported_capability", adapter.ValidateInput([]).Error?.Code);
	}

	[Theory]
	[InlineData("nes")]
	[InlineData("NES")]
	[InlineData("")]
	public void ResolveFrameCpu_RequiresExactNesName(string cpu)
	{
		NesMcpAutomationAdapter adapter = new(FakeMcpEmulatorApi.RunningNes());

		Assert.Equal("invalid_request", adapter.ResolveFrameCpu(cpu).Error?.Code);
		Assert.Equal(CpuType.Nes, adapter.ResolveFrameCpu("Nes").Value);
	}

	[Fact]
	public void ValidateInput_ProducesPressedAndOmittedPortNeutralStates()
	{
		FakeMcpEmulatorApi api = FakeMcpEmulatorApi.RunningNes();
		api.ControllerTopology = [Pad1, Pad1 with { Index = 1, PhysicalPort = 1 }];
		NesMcpAutomationAdapter adapter = new(api);

		McpServiceResult<IReadOnlyList<McpExclusiveControllerState>> result = adapter.ValidateInput([
			new(0, ["start"])
		]);

		Assert.True(result.IsSuccess);
		Assert.Collection(result.Value!,
			state => {
				Assert.Equal(0, state.Port);
				McpControllerValue value = Assert.Single(state.Values);
				Assert.Equal(3, value.ControlId);
				Assert.Equal(1, value.Value);
			},
			state => {
				Assert.Equal(1, state.Port);
				Assert.Empty(state.Values);
			});
	}

	[Fact]
	public void ValidateInput_EmptyButtonsMeansExclusiveNeutral()
	{
		FakeMcpEmulatorApi api = FakeMcpEmulatorApi.RunningNes();
		api.ControllerTopology = [Pad1];
		NesMcpAutomationAdapter adapter = new(api);

		McpExclusiveControllerState state = Assert.Single(adapter.ValidateInput([new(0, [])]).Value!);

		Assert.True(state.Enabled);
		Assert.Empty(state.Values);
	}

	[Fact]
	public void ValidateInput_RejectsInvalidCaseSensitiveButtonWithoutMutation()
	{
		FakeMcpEmulatorApi api = FakeMcpEmulatorApi.RunningNes();
		api.ControllerTopology = [Pad1];
		NesMcpAutomationAdapter adapter = new(api);

		McpServiceResult<IReadOnlyList<McpExclusiveControllerState>> result = adapter.ValidateInput([new(0, ["Start"])]);

		Assert.Equal("invalid_request", result.Error?.Code);
		Assert.Equal(0, api.SetExclusiveControllerOverrideCalls);
	}

	[Fact]
	public void ValidateInput_RejectsDuplicateButtonsAndPorts()
	{
		FakeMcpEmulatorApi api = FakeMcpEmulatorApi.RunningNes();
		api.ControllerTopology = [Pad1];
		NesMcpAutomationAdapter adapter = new(api);

		Assert.Equal("invalid_request", adapter.ValidateInput([new(0, ["a", "a"])]).Error?.Code);
		Assert.Equal("invalid_request", adapter.ValidateInput([new(0, []), new(0, [])]).Error?.Code);
	}

	[Fact]
	public void NativeApi_ExposesExplicitClearForCleanup()
	{
		FakeMcpEmulatorApi api = FakeMcpEmulatorApi.RunningNes();

		api.ClearExclusiveControllerOverrides();

		Assert.Equal(1, api.ClearExclusiveControllerOverridesCalls);
	}

	[Fact]
	public void Registry_OnlySelectsTheNesAdapter()
	{
		McpAutomationAdapterRegistry registry = new(FakeMcpEmulatorApi.RunningNes());

		Assert.IsType<NesMcpAutomationAdapter>(registry.GetAdapter(ConsoleType.Nes));
		Assert.Null(registry.GetAdapter(ConsoleType.Snes));
	}
}
