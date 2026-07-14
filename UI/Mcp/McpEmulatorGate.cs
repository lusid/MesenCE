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
		return ExecuteLocked(_ => ExecuteValidated(_executionCoordinator.ValidateMutation(mutation), operation));
	}

	internal McpServiceResult<T> ExecuteMutationWithTicket<T>(
		McpExecutionMutation mutation,
		Func<McpOperationTicket, McpServiceResult<T>> operation)
	{
		return ExecuteLocked(ticket => {
			McpServiceResult<bool> validation = _executionCoordinator.ValidateMutation(mutation);
			return validation.IsSuccess
				? operation(ticket)
				: McpServiceResult<T>.Failure(validation.Error!.Code, validation.Error.Message);
		});
	}

	internal McpServiceResult<T> ExecuteOwned<T>(long leaseId, Func<McpServiceResult<T>> operation)
	{
		return ExecuteLocked(_ => ExecuteValidated(_executionCoordinator.ValidateOwnedMutation(leaseId), operation));
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
			McpServiceResult<bool> validation = _executionCoordinator.ValidateMutation(mutation);
			return validation.IsSuccess
				? operation(ticket, prepareDebuggerLease)
				: McpServiceResult<T>.Failure(validation.Error!.Code, validation.Error.Message);
		}, acquireDebuggerLease);
	}

	internal McpServiceResult<T> ExecuteForTicket<T>(McpOperationTicket ticket, Func<McpServiceResult<T>> operation)
	{
		return ExecuteLocked(currentTicket => currentTicket != ticket
			? StateChanged<T>()
			: operation());
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

	private McpServiceResult<T> ExecuteLocked<T>(Func<McpOperationTicket, McpServiceResult<T>> operation)
	{
		return ExecuteLocked((ticket, _) => operation(ticket), null);
	}

	private McpServiceResult<T> ExecuteLocked<T>(
		Func<McpOperationTicket, Func<McpServiceResult<bool>>, McpServiceResult<T>> operation,
		Action? acquireDebuggerLease)
	{
		_emulatorSemaphore.Wait();
		try {
			if(Volatile.Read(ref _shutdownStarted) != 0) {
				return McpServiceResult<T>.Failure("server_stopping", "The MCP server is shutting down.");
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
			if(generation != Volatile.Read(ref _emulatorGeneration)
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
