using System;
using System.Threading;

namespace Mesen.Mcp;

internal readonly record struct McpOperationTicket(long Generation, ulong DebuggerBlockState);

internal sealed class McpEmulatorGate
{
	private readonly IMcpEmulatorApi _api;
	private readonly McpExecutionCoordinator _executionCoordinator;
	private readonly SemaphoreSlim _emulatorSemaphore = new(1, 1);
	private readonly CancellationTokenSource _shutdownCancellation = new();
	private long _emulatorGeneration;
	private int _transitionActive;
	private int _shutdownStarted;

	internal McpEmulatorGate(IMcpEmulatorApi api, McpExecutionCoordinator? executionCoordinator = null)
	{
		_api = api;
		_executionCoordinator = executionCoordinator ?? new();
	}

	internal McpExecutionCoordinator ExecutionCoordinator => _executionCoordinator;
	internal long Generation => Volatile.Read(ref _emulatorGeneration);
	internal CancellationToken ShutdownToken => _shutdownCancellation.Token;

	internal McpServiceResult<T> Execute<T>(Func<McpServiceResult<T>> operation)
	{
		return ExecuteLocked(_ => operation());
	}

	internal McpServiceResult<T> ExecuteMutation<T>(McpExecutionMutation mutation, Func<McpServiceResult<T>> operation)
	{
		return ExecuteLocked(_ => ExecuteMutationReserved(mutation, operation));
	}

	internal McpServiceResult<T> ExecuteMutationWithTicket<T>(
		McpExecutionMutation mutation,
		Func<McpOperationTicket, McpServiceResult<T>> operation)
	{
		return ExecuteLocked(ticket => {
			return ExecuteMutationReserved(mutation, () => operation(ticket));
		});
	}

	internal McpServiceResult<T> ExecuteOwned<T>(long leaseId, Func<McpServiceResult<T>> operation)
	{
		return ExecuteLocked(_ => operation(), preflight: () => _executionCoordinator.ValidateOwnedMutation(leaseId));
	}

	internal McpServiceResult<T> ExecuteOwnedStateLoad<T>(
		long leaseId,
		Func<McpOperationTicket, McpServiceResult<T>> operation)
	{
		return ExecuteLocked(
			operation,
			preflight: () => _executionCoordinator.ValidateOwnedMutation(leaseId),
			postflight: (ticket, result) => Volatile.Read(ref _emulatorGeneration) ==
				ticket.Generation + (result.IsSuccess ? 1 : 0));
	}

	internal McpServiceResult<T> ExecuteOwnedWithDebuggerLease<T>(
		long leaseId,
		Action acquireDebuggerLease,
		Func<McpOperationTicket, Func<McpServiceResult<bool>>, McpServiceResult<T>> operation)
	{
		return ExecuteLocked(
			operation,
			acquireDebuggerLease,
			() => _executionCoordinator.ValidateOwnedMutation(leaseId));
	}

	internal McpServiceResult<McpOperationTicket> CaptureTicket()
	{
		return ExecuteLocked(ticket => McpServiceResult<McpOperationTicket>.Success(ticket));
	}

	internal McpServiceResult<T> ExecuteWithTicket<T>(Func<McpOperationTicket, McpServiceResult<T>> operation)
	{
		return ExecuteLocked(operation);
	}

	internal McpServiceResult<T> ExecuteWithDebuggerLease<T>(
		Action acquireDebuggerLease,
		Func<McpOperationTicket, Func<McpServiceResult<bool>>, McpServiceResult<T>> operation)
	{
		return ExecuteLocked(operation, acquireDebuggerLease);
	}

	internal McpServiceResult<T> ExecuteMutationWithDebuggerLease<T>(
		McpExecutionMutation mutation,
		Action acquireDebuggerLease,
		Func<McpOperationTicket, Func<McpServiceResult<bool>>, McpServiceResult<T>> operation)
	{
		return ExecuteLocked((ticket, prepareDebuggerLease) => {
			return ExecuteMutationReserved(mutation, () => operation(ticket, prepareDebuggerLease));
		}, acquireDebuggerLease);
	}

	internal McpServiceResult<T> ExecuteForTicket<T>(McpOperationTicket ticket, Func<McpServiceResult<T>> operation)
	{
		return ExecuteLocked(currentTicket => currentTicket != ticket
			? StateChanged<T>()
			: operation());
	}

	internal McpServiceResult<T> ExecuteOwnedForTicket<T>(
		long leaseId,
		McpOperationTicket ticket,
		Func<McpServiceResult<T>> operation)
	{
		return ExecuteLocked(
			currentTicket => currentTicket != ticket ? StateChanged<T>() : operation(),
			preflight: () => _executionCoordinator.ValidateOwnedMutation(leaseId));
	}

	internal void NotifyEmulatorStateChanged()
	{
		Interlocked.Increment(ref _emulatorGeneration);
	}

	internal void BeginEmulatorTransition()
	{
		Interlocked.Exchange(ref _transitionActive, 1);
		Interlocked.Increment(ref _emulatorGeneration);
	}

	internal void EndEmulatorTransition()
	{
		Interlocked.Increment(ref _emulatorGeneration);
		Interlocked.Exchange(ref _transitionActive, 0);
	}

	internal void BeginShutdown()
	{
		if(Interlocked.Exchange(ref _shutdownStarted, 1) == 0) {
			_shutdownCancellation.Cancel();
		}
	}

	internal void DrainOperations()
	{
		_emulatorSemaphore.Wait();
		_emulatorSemaphore.Release();
	}

	internal McpServiceResult<T> ExecuteTerminalCleanup<T>(Func<McpServiceResult<T>> operation)
	{
		_emulatorSemaphore.Wait();
		try {
			try {
				return operation();
			} catch(Exception) {
				return InteropFailure<T>();
			}
		} finally {
			_emulatorSemaphore.Release();
		}
	}

	internal bool TryExecuteTerminalCleanup<T>(TimeSpan timeout, Func<McpServiceResult<T>> operation)
	{
		if(timeout <= TimeSpan.Zero || !_emulatorSemaphore.Wait(timeout)) {
			return false;
		}
		try {
			try {
				operation();
			} catch(Exception) {
				// Terminal native cleanup is best-effort and cannot prevent managed shutdown.
			}
			return true;
		} finally {
			_emulatorSemaphore.Release();
		}
	}

	private McpServiceResult<T> ExecuteLocked<T>(
		Func<McpOperationTicket, McpServiceResult<T>> operation,
		Func<McpServiceResult<bool>>? preflight = null,
		Func<McpOperationTicket, McpServiceResult<T>, bool>? postflight = null)
	{
		return ExecuteLocked((ticket, _) => operation(ticket), null, preflight, postflight);
	}

	private McpServiceResult<T> ExecuteLocked<T>(
		Func<McpOperationTicket, Func<McpServiceResult<bool>>, McpServiceResult<T>> operation,
		Action? acquireDebuggerLease,
		Func<McpServiceResult<bool>>? preflight = null,
		Func<McpOperationTicket, McpServiceResult<T>, bool>? postflight = null)
	{
		_emulatorSemaphore.Wait();
		try {
			if(Volatile.Read(ref _shutdownStarted) != 0) {
				return McpServiceResult<T>.Failure("server_stopping", "The MCP server is shutting down.");
			}
			if(preflight is not null) {
				McpServiceResult<bool> validation = preflight();
				if(!validation.IsSuccess) {
					return McpServiceResult<T>.Failure(validation.Error!.Code, validation.Error.Message);
				}
			}

			long generation = Volatile.Read(ref _emulatorGeneration);
			if(!TryGetDebuggerRequestBlockState(out ulong debuggerBlockState)) {
				return InteropFailure<T>();
			}
			if(Volatile.Read(ref _transitionActive) != 0 || IsDebuggerRequestBlocked(debuggerBlockState)) {
				return StateChanged<T>();
			}
			if(generation != Volatile.Read(ref _emulatorGeneration)) {
				return StateChanged<T>();
			}

			McpServiceResult<T> result;
			try {
				result = operation(new(generation, debuggerBlockState), PrepareDebuggerLease);
			} catch(Exception) {
				result = InteropFailure<T>();
			}

			if(!TryGetDebuggerRequestBlockState(out ulong endingDebuggerBlockState)) {
				return InteropFailure<T>();
			}
			if((postflight is null
					? generation != Volatile.Read(ref _emulatorGeneration)
					: !postflight(new(generation, debuggerBlockState), result))
				|| Volatile.Read(ref _transitionActive) != 0
				|| debuggerBlockState != endingDebuggerBlockState
				|| IsDebuggerRequestBlocked(endingDebuggerBlockState)) {
				return StateChanged<T>();
			}
			return result;

			McpServiceResult<bool> PrepareDebuggerLease()
			{
				if(acquireDebuggerLease is null) {
					return InteropFailure<bool>();
				}

				acquireDebuggerLease();
				if(!TryGetDebuggerRequestBlockState(out ulong refreshedDebuggerBlockState)) {
					return InteropFailure<bool>();
				}
				if(generation != Volatile.Read(ref _emulatorGeneration)
					|| Volatile.Read(ref _transitionActive) != 0
					|| IsDebuggerRequestBlocked(refreshedDebuggerBlockState)) {
					return StateChanged<bool>();
				}

				debuggerBlockState = refreshedDebuggerBlockState;
				return McpServiceResult<bool>.Success(true);
			}
		} finally {
			_emulatorSemaphore.Release();
		}
	}

	private bool TryGetDebuggerRequestBlockState(out ulong state)
	{
		try {
			state = _api.GetDebuggerRequestBlockState();
			return true;
		} catch(Exception) {
			state = 0;
			return false;
		}
	}

	private static McpServiceResult<T> ExecuteValidated<T>(
		McpServiceResult<bool> validation,
		Func<McpServiceResult<T>> operation)
	{
		return validation.IsSuccess
			? operation()
			: McpServiceResult<T>.Failure(validation.Error!.Code, validation.Error.Message);
	}

	private McpServiceResult<T> ExecuteMutationReserved<T>(
		McpExecutionMutation mutation,
		Func<McpServiceResult<T>> operation)
	{
		McpServiceResult<IDisposable> reservation = _executionCoordinator.TryAcquireMutation(mutation);
		if(!reservation.IsSuccess) {
			return McpServiceResult<T>.Failure(reservation.Error!.Code, reservation.Error.Message);
		}
		using IDisposable ownedReservation = reservation.Value!;
		return operation();
	}

	private static McpServiceResult<T> InteropFailure<T>()
	{
		return McpServiceResult<T>.Failure("interop_failure", "Native emulator interop failed.");
	}

	private static McpServiceResult<T> StateChanged<T>()
	{
		return McpServiceResult<T>.Failure("state_changed", "Emulator state changed during the operation.");
	}

	private static bool IsDebuggerRequestBlocked(ulong state) => (state & 1) != 0;
}
