using System;
using System.Threading;
using System.Threading.Tasks;

namespace Mesen.Mcp;

internal enum McpExecutionMutation
{
	Pause,
	Resume,
	Step,
	Continue,
	Experiment
}

internal sealed class McpExecutionCoordinator
{
	private readonly SemaphoreSlim _ownership = new(1, 1);
	private readonly CancellationTokenSource _shutdownCancellation = new();
	private long _nextLeaseId;
	private long _activeLeaseId;
	private int _quarantined;
	private int _shutdownStarted;

	internal CancellationToken ShutdownToken => _shutdownCancellation.Token;
	internal bool IsQuarantined => Volatile.Read(ref _quarantined) != 0;

	internal async ValueTask<McpServiceResult<McpExecutionLease>> TryAcquireAsync(CancellationToken cancellationToken)
	{
		if(Volatile.Read(ref _shutdownStarted) != 0) {
			return ServerStopping<McpExecutionLease>();
		}
		if(cancellationToken.IsCancellationRequested) {
			return McpServiceResult<McpExecutionLease>.Failure("cancelled", "The operation was cancelled.");
		}
		if(IsQuarantined) {
			return ExecutionStateUnknown<McpExecutionLease>();
		}

		bool acquired;
		try {
			using CancellationTokenSource linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
				cancellationToken,
				_shutdownCancellation.Token);
			acquired = await _ownership.WaitAsync(0, linkedCancellation.Token).ConfigureAwait(false);
		} catch(OperationCanceledException) {
			return Volatile.Read(ref _shutdownStarted) != 0
				? ServerStopping<McpExecutionLease>()
				: McpServiceResult<McpExecutionLease>.Failure("cancelled", "The operation was cancelled.");
		}
		if(!acquired) {
			return OperationInProgress<McpExecutionLease>();
		}

		if(Volatile.Read(ref _shutdownStarted) != 0) {
			_ownership.Release();
			return ServerStopping<McpExecutionLease>();
		}
		if(IsQuarantined) {
			_ownership.Release();
			return ExecutionStateUnknown<McpExecutionLease>();
		}

		long leaseId = Interlocked.Increment(ref _nextLeaseId);
		Volatile.Write(ref _activeLeaseId, leaseId);
		return McpServiceResult<McpExecutionLease>.Success(new(this, leaseId));
	}

	internal McpServiceResult<bool> ValidateMutation(McpExecutionMutation mutation)
	{
		if(Volatile.Read(ref _shutdownStarted) != 0) {
			return ServerStopping<bool>();
		}
		if(IsQuarantined && mutation != McpExecutionMutation.Pause) {
			return ExecutionStateUnknown<bool>();
		}
		if(_ownership.CurrentCount == 0) {
			return OperationInProgress<bool>();
		}
		return McpServiceResult<bool>.Success(true);
	}

	internal McpServiceResult<bool> ValidateOwnedMutation(long leaseId)
	{
		if(Volatile.Read(ref _shutdownStarted) != 0) {
			return ServerStopping<bool>();
		}
		return leaseId != 0 && Volatile.Read(ref _activeLeaseId) == leaseId
			? McpServiceResult<bool>.Success(true)
			: OperationInProgress<bool>();
	}

	internal void EnterQuarantine() => Interlocked.Exchange(ref _quarantined, 1);
	internal void ConfirmStoppedAndClearQuarantine() => Interlocked.Exchange(ref _quarantined, 0);
	internal void NotifyLifecycleRecovery() => Interlocked.Exchange(ref _quarantined, 0);

	internal void BeginShutdown()
	{
		if(Interlocked.Exchange(ref _shutdownStarted, 1) == 0) {
			_shutdownCancellation.Cancel();
		}
	}

	internal void Release(long leaseId)
	{
		if(Interlocked.CompareExchange(ref _activeLeaseId, 0, leaseId) == leaseId) {
			_ownership.Release();
		}
	}

	private static McpServiceResult<T> OperationInProgress<T>() =>
		McpServiceResult<T>.Failure("operation_in_progress", "Another execution operation is in progress.");

	private static McpServiceResult<T> ExecutionStateUnknown<T>() =>
		McpServiceResult<T>.Failure("execution_state_unknown", "Emulator execution state is unknown; pause to recover.");

	private static McpServiceResult<T> ServerStopping<T>() =>
		McpServiceResult<T>.Failure("server_stopping", "The MCP server is shutting down.");
}

internal sealed class McpExecutionLease : IAsyncDisposable
{
	private McpExecutionCoordinator? _coordinator;

	internal McpExecutionLease(McpExecutionCoordinator coordinator, long leaseId)
	{
		_coordinator = coordinator;
		LeaseId = leaseId;
	}

	internal long LeaseId { get; }

	public ValueTask DisposeAsync()
	{
		Interlocked.Exchange(ref _coordinator, null)?.Release(LeaseId);
		return ValueTask.CompletedTask;
	}
}
