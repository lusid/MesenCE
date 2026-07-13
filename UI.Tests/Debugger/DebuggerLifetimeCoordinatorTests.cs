using Mesen.Debugger.Utilities;

namespace UI.Tests.Debugger;

public sealed class DebuggerLifetimeCoordinatorTests
{
	[Fact]
	public void OverlappingLeases_InitializeOnceAndReleaseAfterLastOwner()
	{
		List<string> events = [];
		DebuggerLifetimeCoordinator coordinator = new(
			() => events.Add("initialize"),
			() => events.Add("release")
		);

		IDisposable ui = coordinator.Acquire();
		IDisposable mcp = coordinator.Acquire();
		ui.Dispose();
		Assert.Equal(["initialize"], events);

		mcp.Dispose();
		Assert.Equal(["initialize", "release"], events);
	}

	[Fact]
	public void Lease_DisposeIsIdempotent()
	{
		int releases = 0;
		DebuggerLifetimeCoordinator coordinator = new(() => { }, () => releases++);
		IDisposable lease = coordinator.Acquire();

		lease.Dispose();
		lease.Dispose();

		Assert.Equal(1, releases);
	}
}
