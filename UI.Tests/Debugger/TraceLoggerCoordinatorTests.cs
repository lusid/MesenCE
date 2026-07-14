using Mesen.Debugger.Utilities;

namespace UI.Tests.Debugger;

public sealed class TraceLoggerCoordinatorTests
{
	[Fact]
	public void UiOperations_ExecuteOnlyForOwnerAndReleaseAllowsNextClient()
	{
		TraceLoggerCoordinator coordinator = new();
		object uiOwner = new();
		object mcpOwner = new();
		int uiMutations = 0;
		int mcpMutations = 0;

		Assert.True(coordinator.TryAcquireAndExecute(uiOwner, () => uiMutations++));
		Assert.True(coordinator.TryExecute(uiOwner, () => uiMutations++));
		Assert.False(coordinator.TryAcquireAndExecute(mcpOwner, () => mcpMutations++));
		Assert.False(coordinator.TryExecute(mcpOwner, () => mcpMutations++));
		Assert.True(coordinator.TryReleaseAndExecute(uiOwner, () => uiMutations++));
		Assert.True(coordinator.TryAcquireAndExecute(mcpOwner, () => mcpMutations++));

		Assert.Equal(3, uiMutations);
		Assert.Equal(1, mcpMutations);
	}

	[Fact]
	public void FailedInitialConfigurationRollsBackOwnership()
	{
		TraceLoggerCoordinator coordinator = new();
		object failedOwner = new();
		object nextOwner = new();

		Assert.Throws<InvalidOperationException>(() =>
			coordinator.TryAcquireAndExecute(failedOwner, () => throw new InvalidOperationException()));

		Assert.True(coordinator.TryAcquireAndExecute(nextOwner, () => { }));
	}
}
