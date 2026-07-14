using System;

namespace Mesen.Debugger.Utilities;

internal interface ITraceLoggerCoordinator
{
	bool IsOwner(object owner);
	bool TryAcquireAndExecute(object owner, Action operation);
	bool TryExecute(object owner, Action operation);
	bool TryReleaseAndExecute(object owner, Action operation);
	void Release(object owner);
}

internal sealed class TraceLoggerCoordinator : ITraceLoggerCoordinator
{
	internal static ITraceLoggerCoordinator Shared { get; } = new TraceLoggerCoordinator();

	private readonly object _lock = new();
	private object? _owner;

	public bool IsOwner(object owner)
	{
		lock(_lock) {
			return ReferenceEquals(_owner, owner);
		}
	}

	public bool TryAcquireAndExecute(object owner, Action operation)
	{
		lock(_lock) {
			if(_owner is not null && !ReferenceEquals(_owner, owner)) {
				return false;
			}

			bool acquired = _owner is null;
			_owner = owner;
			try {
				operation();
				return true;
			} catch {
				if(acquired) {
					_owner = null;
				}
				throw;
			}
		}
	}

	public bool TryExecute(object owner, Action operation)
	{
		lock(_lock) {
			if(!ReferenceEquals(_owner, owner)) {
				return false;
			}
			operation();
			return true;
		}
	}

	public bool TryReleaseAndExecute(object owner, Action operation)
	{
		lock(_lock) {
			if(!ReferenceEquals(_owner, owner)) {
				return false;
			}
			operation();
			_owner = null;
			return true;
		}
	}

	public void Release(object owner)
	{
		lock(_lock) {
			if(ReferenceEquals(_owner, owner)) {
				_owner = null;
			}
		}
	}
}
