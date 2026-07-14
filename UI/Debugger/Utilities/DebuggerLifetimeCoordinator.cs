using Mesen.Interop;
using System;
using System.Threading;

namespace Mesen.Debugger.Utilities;

internal interface IDebuggerLifetimeCoordinator
{
	IDisposable Acquire();
}

internal sealed class DebuggerLifetimeCoordinator : IDebuggerLifetimeCoordinator
{
	internal static IDebuggerLifetimeCoordinator Shared { get; } =
		new DebuggerLifetimeCoordinator(DebugApi.InitializeDebugger, DebugApi.ReleaseDebugger);

	private readonly object _lock = new();
	private readonly Action _initialize;
	private readonly Action _release;
	private int _leaseCount;

	internal DebuggerLifetimeCoordinator(Action initialize, Action release)
	{
		_initialize = initialize;
		_release = release;
	}

	public IDisposable Acquire()
	{
		lock(_lock) {
			if(_leaseCount == 0) {
				_initialize();
			}
			_leaseCount++;
		}
		return new Lease(this);
	}

	private void Release()
	{
		lock(_lock) {
			if(--_leaseCount == 0) {
				_release();
			}
		}
	}

	private sealed class Lease(DebuggerLifetimeCoordinator owner) : IDisposable
	{
		private DebuggerLifetimeCoordinator? _owner = owner;
		public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
	}
}
