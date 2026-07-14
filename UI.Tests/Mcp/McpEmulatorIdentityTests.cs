using Mesen.Mcp;

namespace UI.Tests.Mcp;

public sealed class McpEmulatorIdentityTests
{
	[Fact]
	public void MutableStateChange_PreservesRomIdentityAndAdvancesGeneration()
	{
		McpEmulatorIdentity identity = new();

		identity.NotifyMutableStateChanged();

		Assert.Equal(new McpStateIdentity(0, 1), identity.Current);
	}

	[Fact]
	public void RomChange_AdvancesRomIdentityAndMutableGeneration()
	{
		McpEmulatorIdentity identity = new();
		identity.NotifyMutableStateChanged();

		identity.NotifyRomChanged();

		Assert.Equal(new McpStateIdentity(1, 2), identity.Current);
	}
}
