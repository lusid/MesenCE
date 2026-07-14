using Mesen.Interop;
using Mesen.Mcp;

namespace UI.Tests.Mcp;

public sealed class McpEmulatorGateTests
{
	[Fact]
	public async Task Execute_ReadOnlyCallRemainsAvailableWhileExecutionLeaseIsOwned()
	{
		McpExecutionCoordinator coordinator = new();
		McpEmulatorGate gate = new(FakeMcpEmulatorApi.RunningNes(), coordinator);
		await using McpExecutionLease lease = (await coordinator.TryAcquireAsync(CancellationToken.None)).Value!;

		McpServiceResult<int> result = gate.Execute(() => McpServiceResult<int>.Success(1));

		Assert.Equal(1, result.Value);
	}

	[Fact]
	public async Task ExecuteMutation_RejectsCompetitorAndAllowsOwner()
	{
		McpExecutionCoordinator coordinator = new();
		McpEmulatorGate gate = new(FakeMcpEmulatorApi.RunningNes(), coordinator);
		await using McpExecutionLease lease = (await coordinator.TryAcquireAsync(CancellationToken.None)).Value!;
		bool competitorCalled = false;

		McpServiceResult<int> competitor = gate.ExecuteMutation(McpExecutionMutation.Step, () => {
			competitorCalled = true;
			return McpServiceResult<int>.Success(1);
		});
		McpServiceResult<int> owner = gate.ExecuteOwned(lease.LeaseId, () => McpServiceResult<int>.Success(2));

		Assert.Equal("operation_in_progress", competitor.Error?.Code);
		Assert.False(competitorCalled);
		Assert.Equal(2, owner.Value);
	}

	[Fact]
	public async Task ExecuteMutation_BlocksLeaseAcquisitionUntilMutationCompletes()
	{
		using ManualResetEventSlim mutationEntered = new();
		using ManualResetEventSlim releaseMutation = new();
		McpExecutionCoordinator coordinator = new();
		McpEmulatorGate gate = new(FakeMcpEmulatorApi.RunningNes(), coordinator);
		Task<McpServiceResult<int>> mutation = Task.Run(() => gate.ExecuteMutation(McpExecutionMutation.Resume, () => {
			mutationEntered.Set();
			releaseMutation.Wait(TimeSpan.FromSeconds(5));
			return McpServiceResult<int>.Success(1);
		}));
		Assert.True(mutationEntered.Wait(TimeSpan.FromSeconds(5)));

		McpServiceResult<McpExecutionLease> blocked = await coordinator.TryAcquireAsync(CancellationToken.None);
		Assert.Equal("operation_in_progress", blocked.Error?.Code);

		releaseMutation.Set();
		Assert.True((await mutation.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
		await using McpExecutionLease lease = (await coordinator.TryAcquireAsync(CancellationToken.None)).Value!;
	}

	[Fact]
	public async Task Execute_SerializesOperations()
	{
		using ManualResetEventSlim firstEntered = new();
		using ManualResetEventSlim releaseFirst = new();
		using ManualResetEventSlim secondStarted = new();
		McpEmulatorGate gate = new(FakeMcpEmulatorApi.RunningNes());

		Task<McpServiceResult<int>> first = Task.Run(() => gate.Execute(() => {
			firstEntered.Set();
			releaseFirst.Wait(TimeSpan.FromSeconds(5));
			return McpServiceResult<int>.Success(1);
		}));
		Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(5)));
		Task<McpServiceResult<int>> second = Task.Run(() => {
			secondStarted.Set();
			return gate.Execute(() => McpServiceResult<int>.Success(2));
		});
		Assert.True(secondStarted.Wait(TimeSpan.FromSeconds(5)));
		Assert.NotSame(second, await Task.WhenAny(second, Task.Delay(100)));

		releaseFirst.Set();
		McpServiceResult<int>[] results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
		Assert.All(results, result => Assert.True(result.IsSuccess));
	}

	[Fact]
	public void Execute_WhenTransitionBeginsDuringOperation_ReturnsStateChanged()
	{
		McpEmulatorGate gate = new(FakeMcpEmulatorApi.RunningNes());

		McpServiceResult<int> result = gate.Execute(() => {
			gate.BeginEmulatorTransition();
			return McpServiceResult<int>.Success(1);
		});

		Assert.Equal("state_changed", result.Error!.Code);
	}

	[Fact]
	public void Execute_WhenTransitionBeginsAndEndsDuringOperation_ReturnsStateChanged()
	{
		McpEmulatorGate gate = new(FakeMcpEmulatorApi.RunningNes());

		McpServiceResult<int> result = gate.Execute(() => {
			gate.BeginEmulatorTransition();
			gate.EndEmulatorTransition();
			return McpServiceResult<int>.Success(1);
		});

		Assert.Equal("state_changed", result.Error!.Code);
	}

	[Fact]
	public void Execute_WhenTransitionIsActive_RejectsBeforeOperationAndRecoversAfterEnd()
	{
		McpEmulatorGate gate = new(FakeMcpEmulatorApi.RunningNes());
		bool called = false;
		gate.BeginEmulatorTransition();

		McpServiceResult<int> blocked = gate.Execute(() => {
			called = true;
			return McpServiceResult<int>.Success(1);
		});

		Assert.Equal("state_changed", blocked.Error!.Code);
		Assert.False(called);
		gate.EndEmulatorTransition();
		Assert.True(gate.Execute(() => McpServiceResult<int>.Success(1)).IsSuccess);
	}

	[Fact]
	public void Execute_WhenDebuggerRequestsAreBlocked_RejectsBeforeOperationAndRecoversAfterward()
	{
		FakeMcpEmulatorApi api = FakeMcpEmulatorApi.RunningNes();
		api.SetDebuggerRequestBlocked(true);
		McpEmulatorGate gate = new(api);
		bool called = false;

		McpServiceResult<int> blocked = gate.Execute(() => {
			called = true;
			return McpServiceResult<int>.Success(1);
		});

		Assert.Equal("state_changed", blocked.Error!.Code);
		Assert.False(called);
		api.SetDebuggerRequestBlocked(false);
		Assert.True(gate.Execute(() => McpServiceResult<int>.Success(1)).IsSuccess);
	}

	[Fact]
	public void Execute_WhenDebuggerBlockStartsAndEndsDuringOperation_ReturnsStateChanged()
	{
		FakeMcpEmulatorApi api = FakeMcpEmulatorApi.RunningNes();
		McpEmulatorGate gate = new(api);

		McpServiceResult<int> result = gate.Execute(() => {
			api.SetDebuggerRequestBlocked(true);
			api.SetDebuggerRequestBlocked(false);
			return McpServiceResult<int>.Success(1);
		});

		Assert.Equal("state_changed", result.Error!.Code);
	}

	[Fact]
	public void ExecuteWithDebuggerLease_WhenAcquireChangesBlockState_RefreshesSnapshotBeforeMutation()
	{
		FakeMcpEmulatorApi api = FakeMcpEmulatorApi.RunningNes();
		McpEmulatorGate gate = new(api);
		int mutations = 0;

		McpServiceResult<int> result = gate.ExecuteWithDebuggerLease(
			() => {
				api.SetDebuggerRequestBlocked(true);
				api.SetDebuggerRequestBlocked(false);
			},
			(_, prepareDebuggerLease) => {
				McpServiceResult<bool> lease = prepareDebuggerLease();
				if(!lease.IsSuccess) {
					return McpServiceResult<int>.Failure(lease.Error!.Code, lease.Error.Message);
				}
				mutations++;
				return McpServiceResult<int>.Success(1);
			}
		);

		Assert.True(result.IsSuccess);
		Assert.Equal(1, mutations);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void ExecuteWithDebuggerLease_WhenAcquireStartsTransitionOrBlock_RejectsBeforeMutation(bool transition)
	{
		FakeMcpEmulatorApi api = FakeMcpEmulatorApi.RunningNes();
		McpEmulatorGate gate = new(api);
		bool mutated = false;

		McpServiceResult<int> result = gate.ExecuteWithDebuggerLease(
			() => {
				if(transition) {
					gate.BeginEmulatorTransition();
				} else {
					api.SetDebuggerRequestBlocked(true);
				}
			},
			(_, prepareDebuggerLease) => {
				McpServiceResult<bool> lease = prepareDebuggerLease();
				if(!lease.IsSuccess) {
					return McpServiceResult<int>.Failure(lease.Error!.Code, lease.Error.Message);
				}
				mutated = true;
				return McpServiceResult<int>.Success(1);
			}
		);

		Assert.Equal("state_changed", result.Error!.Code);
		Assert.False(mutated);
	}

	[Fact]
	public void Execute_WhenOperationThrows_ReturnsSanitizedInteropFailure()
	{
		McpEmulatorGate gate = new(FakeMcpEmulatorApi.RunningNes());

		McpServiceResult<int> result = gate.Execute<int>(() => throw new InvalidOperationException("native details"));

		Assert.Equal("interop_failure", result.Error!.Code);
		Assert.Equal("Native emulator interop failed.", result.Error.Message);
	}

	[Theory]
	[InlineData(1, false)]
	[InlineData(2, true)]
	public void Execute_WhenDebuggerBlockSnapshotThrows_ReturnsSanitizedInteropFailure(int throwOnCall, bool operationCalled)
	{
		FakeMcpEmulatorApi api = FakeMcpEmulatorApi.RunningNes();
		api.ThrowOnDebuggerRequestBlockStateCall = throwOnCall;
		McpEmulatorGate gate = new(api);
		bool called = false;

		McpServiceResult<int> result = gate.Execute(() => {
			called = true;
			return McpServiceResult<int>.Success(1);
		});

		Assert.Equal("interop_failure", result.Error!.Code);
		Assert.Equal("Native emulator interop failed.", result.Error.Message);
		Assert.Equal(operationCalled, called);
	}

	[Fact]
	public void Execute_WhenGenerationChangesDuringOperation_ReturnsStateChanged()
	{
		McpEmulatorGate gate = new(FakeMcpEmulatorApi.RunningNes());

		McpServiceResult<int> result = gate.Execute(() => {
			gate.NotifyEmulatorStateChanged();
			return McpServiceResult<int>.Success(1);
		});

		Assert.Equal("state_changed", result.Error!.Code);
	}

	[Fact]
	public void ExecuteWithTicket_PassesCapturedTicketWithoutReacquiringGate()
	{
		McpEmulatorGate gate = new(FakeMcpEmulatorApi.RunningNes());
		McpOperationTicket captured = default;

		McpServiceResult<int> result = gate.ExecuteWithTicket(ticket => {
			captured = ticket;
			return McpServiceResult<int>.Success(1);
		});

		Assert.True(result.IsSuccess);
		Assert.Equal(gate.Generation, captured.Generation);
	}

	[Fact]
	public void ExecuteForTicket_WhenGenerationChanged_ReturnsStateChangedWithoutRunningCompletion()
	{
		FakeMcpEmulatorApi api = FakeMcpEmulatorApi.RunningNes();
		McpEmulatorGate gate = new(api);
		McpOperationTicket ticket = gate.CaptureTicket().Value!;
		gate.NotifyEmulatorStateChanged();
		bool called = false;

		McpServiceResult<int> result = gate.ExecuteForTicket(ticket, () => {
			called = true;
			return McpServiceResult<int>.Success(1);
		});

		Assert.Equal("state_changed", result.Error!.Code);
		Assert.False(called);
	}

	[Fact]
	public void ExecuteForTicket_WhenDebuggerBlockStateChanged_ReturnsStateChangedWithoutRunningCompletion()
	{
		FakeMcpEmulatorApi api = FakeMcpEmulatorApi.RunningNes();
		McpEmulatorGate gate = new(api);
		McpOperationTicket ticket = gate.CaptureTicket().Value!;
		api.SetDebuggerRequestBlocked(true);
		api.SetDebuggerRequestBlocked(false);
		bool called = false;

		McpServiceResult<int> result = gate.ExecuteForTicket(ticket, () => {
			called = true;
			return McpServiceResult<int>.Success(1);
		});

		Assert.Equal("state_changed", result.Error!.Code);
		Assert.False(called);
	}

	[Fact]
	public async Task BeginShutdown_CancelsTokenRejectsQueuedOperationAndDrainWaitsForActiveOperation()
	{
		using ManualResetEventSlim operationEntered = new();
		using ManualResetEventSlim releaseOperation = new();
		using ManualResetEventSlim operationExited = new();
		McpEmulatorGate gate = new(FakeMcpEmulatorApi.RunningNes());
		Task<McpServiceResult<int>> active = Task.Run(() => gate.Execute(() => {
			operationEntered.Set();
			releaseOperation.Wait(TimeSpan.FromSeconds(5));
			operationExited.Set();
			return McpServiceResult<int>.Success(1);
		}));
		Assert.True(operationEntered.Wait(TimeSpan.FromSeconds(5)));
		Task<McpServiceResult<int>> queued = Task.Run(() => gate.Execute(() => McpServiceResult<int>.Success(2)));
		await Task.Delay(100);

		gate.BeginShutdown();
		Task drain = Task.Run(gate.DrainOperations);

		Assert.True(gate.ShutdownToken.IsCancellationRequested);
		Assert.False(drain.IsCompleted);
		releaseOperation.Set();
		await drain.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.True(operationExited.IsSet);
		Assert.True((await active).IsSuccess);
		Assert.Equal("server_stopping", (await queued).Error!.Code);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void ExecuteTerminalCleanup_BypassesTransitionOrDebuggerBlockAndSanitizesExceptions(bool transition)
	{
		FakeMcpEmulatorApi api = FakeMcpEmulatorApi.RunningNes();
		McpEmulatorGate gate = new(api);
		if(transition) {
			gate.BeginEmulatorTransition();
		} else {
			api.SetDebuggerRequestBlocked(true);
		}
		bool called = false;

		McpServiceResult<int> result = gate.ExecuteTerminalCleanup(() => {
			called = true;
			return McpServiceResult<int>.Success(1);
		});
		McpServiceResult<int> failed = gate.ExecuteTerminalCleanup<int>(() => throw new InvalidOperationException("native details"));

		Assert.True(result.IsSuccess);
		Assert.True(called);
		Assert.Equal("interop_failure", failed.Error!.Code);
		Assert.Equal("Native emulator interop failed.", failed.Error.Message);
	}

}
