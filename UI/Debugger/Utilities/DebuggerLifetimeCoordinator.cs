using Mesen.Interop;
using System;
using System.Threading;

namespace Mesen.Debugger.Utilities;

internal interface IDebuggerLifetimeCoordinator
{
	IDebuggerLifetimeLease Acquire();
}

internal interface IDebuggerLifetimeLease : IDisposable
{
	void Detach();
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

	public IDebuggerLifetimeLease Acquire()
	{
		lock(_lock) {
			if(_leaseCount == 0) {
				_initialize();
			}
			_leaseCount++;
		}
		return new Lease(this);
	}

	private void Release(bool releaseNative)
	{
		lock(_lock) {
			if(--_leaseCount == 0 && releaseNative) {
				_release();
			}
		}
	}

	private sealed class Lease(DebuggerLifetimeCoordinator owner) : IDebuggerLifetimeLease
	{
		private DebuggerLifetimeCoordinator? _owner = owner;
		public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(releaseNative: true);
		public void Detach() => Interlocked.Exchange(ref _owner, null)?.Release(releaseNative: false);
	}
}
