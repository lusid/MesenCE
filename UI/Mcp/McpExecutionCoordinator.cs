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
	private readonly object _stateLock = new();
	private long _nextLeaseId;
	private long _activeLeaseId;
	private bool _mutationReservationActive;
	private bool _quarantined;
	private bool _shutdownStarted;

	internal CancellationToken ShutdownToken => _shutdownCancellation.Token;
	internal bool IsQuarantined
	{
		get {
			lock(_stateLock) {
				return _quarantined;
			}
		}
	}

	internal async ValueTask<McpServiceResult<McpExecutionLease>> TryAcquireAsync(CancellationToken cancellationToken)
	{
		if(cancellationToken.IsCancellationRequested) {
			return McpServiceResult<McpExecutionLease>.Failure("cancelled", "The operation was cancelled.");
		}
		lock(_stateLock) {
			if(_shutdownStarted) {
				return ServerStopping<McpExecutionLease>();
			}
			if(_quarantined) {
				return ExecutionStateUnknown<McpExecutionLease>();
			}
		}

		bool acquired;
		try {
			using CancellationTokenSource linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
				cancellationToken,
				_shutdownCancellation.Token);
			acquired = await _ownership.WaitAsync(0, linkedCancellation.Token).ConfigureAwait(false);
		} catch(OperationCanceledException) {
			lock(_stateLock) {
				return _shutdownStarted
					? ServerStopping<McpExecutionLease>()
					: McpServiceResult<McpExecutionLease>.Failure("cancelled", "The operation was cancelled.");
			}
		}
		if(!acquired) {
			return OperationInProgress<McpExecutionLease>();
		}

		McpServiceResult<McpExecutionLease>? rejected = null;
		McpExecutionLease? lease = null;
		lock(_stateLock) {
			if(_shutdownStarted) {
				rejected = ServerStopping<McpExecutionLease>();
			} else if(_quarantined) {
				rejected = ExecutionStateUnknown<McpExecutionLease>();
			} else {
				long leaseId = ++_nextLeaseId;
				_activeLeaseId = leaseId;
				lease = new(this, leaseId);
			}
		}
		if(rejected is not null) {
			_ownership.Release();
			return rejected;
		}
		return McpServiceResult<McpExecutionLease>.Success(lease!);
	}

	internal McpServiceResult<bool> ValidateMutation(McpExecutionMutation mutation)
	{
		lock(_stateLock) {
			if(_shutdownStarted) {
				return ServerStopping<bool>();
			}
			if(_quarantined && mutation != McpExecutionMutation.Pause) {
				return ExecutionStateUnknown<bool>();
			}
			if(_activeLeaseId != 0 || _mutationReservationActive) {
				return OperationInProgress<bool>();
			}
			return McpServiceResult<bool>.Success(true);
		}
	}

	internal McpServiceResult<bool> ValidateOwnedMutation(long leaseId)
	{
		lock(_stateLock) {
			if(_shutdownStarted) {
				return ServerStopping<bool>();
			}
			if(_quarantined) {
				return ExecutionStateUnknown<bool>();
			}
			return leaseId != 0 && _activeLeaseId == leaseId
				? McpServiceResult<bool>.Success(true)
				: OperationInProgress<bool>();
		}
	}

	internal McpServiceResult<IDisposable> TryAcquireMutation(McpExecutionMutation mutation)
	{
		McpServiceResult<bool> validation = ValidateMutation(mutation);
		if(!validation.IsSuccess) {
			return McpServiceResult<IDisposable>.Failure(validation.Error!.Code, validation.Error.Message);
		}
		if(!_ownership.Wait(0)) {
			return OperationInProgress<IDisposable>();
		}

		McpServiceResult<IDisposable>? rejected = null;
		lock(_stateLock) {
			if(_shutdownStarted) {
				rejected = ServerStopping<IDisposable>();
			} else if(_quarantined && mutation != McpExecutionMutation.Pause) {
				rejected = ExecutionStateUnknown<IDisposable>();
			} else {
				_mutationReservationActive = true;
			}
		}
		if(rejected is not null) {
			_ownership.Release();
			return rejected;
		}
		return McpServiceResult<IDisposable>.Success(new MutationLease(this));
	}

	internal void EnterQuarantine()
	{
		lock(_stateLock) {
			_quarantined = true;
		}
	}

	internal void ConfirmStoppedAndClearQuarantine()
	{
		lock(_stateLock) {
			_quarantined = false;
		}
	}

	internal void NotifyLifecycleRecovery() => ConfirmStoppedAndClearQuarantine();

	internal void BeginShutdown()
	{
		bool cancel;
		lock(_stateLock) {
			cancel = !_shutdownStarted;
			_shutdownStarted = true;
		}
		if(cancel) {
			_shutdownCancellation.Cancel();
		}
	}

	internal void Release(long leaseId)
	{
		bool release = false;
		lock(_stateLock) {
			if(_activeLeaseId == leaseId) {
				_activeLeaseId = 0;
				release = true;
			}
		}
		if(release) {
			_ownership.Release();
		}
	}

	private void ReleaseMutation()
	{
		lock(_stateLock) {
			_mutationReservationActive = false;
		}
		_ownership.Release();
	}

	private static McpServiceResult<T> OperationInProgress<T>() =>
		McpServiceResult<T>.Failure("operation_in_progress", "Another execution operation is in progress.");

	private static McpServiceResult<T> ExecutionStateUnknown<T>() =>
		McpServiceResult<T>.Failure("execution_state_unknown", "Emulator execution state is unknown; pause to recover.");

	private static McpServiceResult<T> ServerStopping<T>() =>
		McpServiceResult<T>.Failure("server_stopping", "The MCP server is shutting down.");

	private sealed class MutationLease : IDisposable
	{
		private McpExecutionCoordinator? _coordinator;

		internal MutationLease(McpExecutionCoordinator coordinator)
		{
			_coordinator = coordinator;
		}

		public void Dispose()
		{
			Interlocked.Exchange(ref _coordinator, null)?.ReleaseMutation();
		}
	}
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
