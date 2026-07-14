using Mesen.Mcp;

namespace UI.Tests.Mcp;

public sealed class McpExecutionCoordinatorTests
{
	[Fact]
	public async Task ExecutionLease_BlocksCompetingMutationUntilDisposed()
	{
		McpExecutionCoordinator coordinator = new();
		await using McpExecutionLease lease = Assert.IsType<McpExecutionLease>(
			(await coordinator.TryAcquireAsync(CancellationToken.None)).Value);

		Assert.Equal("operation_in_progress", coordinator.ValidateMutation(McpExecutionMutation.Step).Error?.Code);
		Assert.True(coordinator.ValidateOwnedMutation(lease.LeaseId).IsSuccess);

		await lease.DisposeAsync();
		Assert.True(coordinator.ValidateMutation(McpExecutionMutation.Step).IsSuccess);
	}

	[Fact]
	public async Task TryAcquireAsync_WhenLeaseIsOwned_ReturnsOperationInProgress()
	{
		McpExecutionCoordinator coordinator = new();
		await using McpExecutionLease lease = (await coordinator.TryAcquireAsync(CancellationToken.None)).Value!;

		McpServiceResult<McpExecutionLease> result = await coordinator.TryAcquireAsync(CancellationToken.None);

		Assert.Equal("operation_in_progress", result.Error?.Code);
	}

	[Fact]
	public async Task BeginShutdown_CancelsOwnershipAndRejectsAcquisition()
	{
		McpExecutionCoordinator coordinator = new();
		coordinator.BeginShutdown();

		McpServiceResult<McpExecutionLease> result = await coordinator.TryAcquireAsync(CancellationToken.None);

		Assert.True(coordinator.ShutdownToken.IsCancellationRequested);
		Assert.Equal("server_stopping", result.Error?.Code);
	}

	[Fact]
	public async Task TryAcquireAsync_WhenCallerCancelled_ReturnsCancelled()
	{
		McpExecutionCoordinator coordinator = new();
		using CancellationTokenSource cancellation = new();
		cancellation.Cancel();

		McpServiceResult<McpExecutionLease> result = await coordinator.TryAcquireAsync(cancellation.Token);

		Assert.Equal("cancelled", result.Error?.Code);
	}

	[Fact]
	public void Quarantine_AllowsOnlyPauseRecoveryMutation()
	{
		McpExecutionCoordinator coordinator = new();
		coordinator.EnterQuarantine();

		Assert.True(coordinator.ValidateMutation(McpExecutionMutation.Pause).IsSuccess);
		Assert.Equal("execution_state_unknown", coordinator.ValidateMutation(McpExecutionMutation.Resume).Error?.Code);
		Assert.Equal("execution_state_unknown", coordinator.ValidateMutation(McpExecutionMutation.Step).Error?.Code);
		Assert.Equal("execution_state_unknown", coordinator.ValidateMutation(McpExecutionMutation.Continue).Error?.Code);
		Assert.Equal("execution_state_unknown", coordinator.ValidateMutation(McpExecutionMutation.Experiment).Error?.Code);
		Assert.True(coordinator.IsQuarantined);
	}

	[Fact]
	public async Task Quarantine_RejectsExistingOwner()
	{
		McpExecutionCoordinator coordinator = new();
		await using McpExecutionLease lease = (await coordinator.TryAcquireAsync(CancellationToken.None)).Value!;

		coordinator.EnterQuarantine();

		Assert.Equal("execution_state_unknown", coordinator.ValidateOwnedMutation(lease.LeaseId).Error?.Code);
	}

	[Fact]
	public async Task QuarantineAndAcquisition_AreSerializedAtAdmission()
	{
		for(int i = 0; i < 100; i++) {
			McpExecutionCoordinator coordinator = new();
			using Barrier start = new(2);
			Task quarantine = Task.Run(() => {
				start.SignalAndWait();
				coordinator.EnterQuarantine();
			});
			Task<McpServiceResult<McpExecutionLease>> acquisition = Task.Run(async () => {
				start.SignalAndWait();
				return await coordinator.TryAcquireAsync(CancellationToken.None);
			});

			await Task.WhenAll(quarantine, acquisition);
			McpServiceResult<McpExecutionLease> result = await acquisition;
			if(result.IsSuccess) {
				await using McpExecutionLease lease = result.Value!;
				Assert.Equal("execution_state_unknown", coordinator.ValidateOwnedMutation(lease.LeaseId).Error?.Code);
			} else {
				Assert.Equal("execution_state_unknown", result.Error?.Code);
			}
		}
	}

	[Fact]
	public void ConfirmedStopAndLifecycleRecovery_ClearQuarantine()
	{
		McpExecutionCoordinator coordinator = new();
		coordinator.EnterQuarantine();

		coordinator.ConfirmStoppedAndClearQuarantine();
		Assert.False(coordinator.IsQuarantined);

		coordinator.EnterQuarantine();
		coordinator.NotifyLifecycleRecovery();
		Assert.False(coordinator.IsQuarantined);
	}
}
