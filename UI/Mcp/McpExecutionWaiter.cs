using System;
using System.Threading;
using System.Threading.Tasks;
using Mesen.Interop;

namespace Mesen.Mcp;

internal enum McpStopReason
{
	StepCompleted,
	Breakpoint,
	Pause,
	Reset,
	RomTransition,
	StateChanged,
	Cancelled,
	Timeout,
	ServerStopping,
	NativeFailure
}

internal readonly record struct McpStopResult(
	McpStopReason Reason,
	int? CompletedFrames,
	BreakEvent? BreakEvent,
	bool StopConfirmed,
	McpStateIdentity? EventStateIdentity = null);

internal sealed class McpExecutionWaiter
{
	private readonly object _lock = new();
	private Registration? _active;

	internal Registration? TryRegisterFrame(
		long leaseId,
		CpuType cpuType,
		int frameCount,
		McpStateIdentity stateIdentity)
	{
		return TryRegister(new Registration(this, leaseId, cpuType, frameCount, stateIdentity, waitForAnyStop: false));
	}

	internal Registration? TryRegisterStop(long leaseId, McpStateIdentity stateIdentity)
	{
		return TryRegister(new Registration(this, leaseId, default, 0, stateIdentity, waitForAnyStop: true));
	}

	internal void NotifyCodeBreak(BreakEvent copied, McpStateIdentity stateIdentity)
	{
		Registration? active;
		McpStopResult result;
		lock(_lock) {
			active = _active;
			if(active is null) {
				return;
			}
			if(active.StateIdentity != stateIdentity) {
				result = new(McpStopReason.StateChanged, null, null, true);
			} else if(!active.WaitForAnyStop && copied.Source == BreakSource.PpuStep) {
				if(active.CpuType != copied.SourceCpu) {
					return;
				}
				result = new(McpStopReason.StepCompleted, active.FrameCount, null, true);
			} else {
				McpStopReason reason = copied.Source == BreakSource.Pause
					? McpStopReason.Pause
					: McpStopReason.Breakpoint;
				result = new(reason, null, copied, true, stateIdentity);
			}
		}
		active.Completion.TrySetResult(result);
	}

	internal void NotifyStop(McpStopReason reason, bool stopConfirmed = true)
	{
		Registration? active;
		lock(_lock) {
			active = _active;
		}
		active?.Completion.TrySetResult(new(reason, null, null, stopConfirmed));
	}

	private Registration? TryRegister(Registration registration)
	{
		lock(_lock) {
			if(_active is not null) {
				return null;
			}
			_active = registration;
			return registration;
		}
	}

	private void Remove(Registration registration)
	{
		lock(_lock) {
			if(ReferenceEquals(_active, registration)) {
				_active = null;
			}
		}
	}

	internal sealed class Registration : IDisposable
	{
		private McpExecutionWaiter? _owner;

		internal Registration(
			McpExecutionWaiter owner,
			long leaseId,
			CpuType cpuType,
			int frameCount,
			McpStateIdentity stateIdentity,
			bool waitForAnyStop)
		{
			_owner = owner;
			LeaseId = leaseId;
			CpuType = cpuType;
			FrameCount = frameCount;
			StateIdentity = stateIdentity;
			WaitForAnyStop = waitForAnyStop;
		}

		internal long LeaseId { get; }
		internal CpuType CpuType { get; }
		internal int FrameCount { get; }
		internal McpStateIdentity StateIdentity { get; }
		internal bool WaitForAnyStop { get; }
		internal TaskCompletionSource<McpStopResult> Completion { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public void Dispose()
		{
			Interlocked.Exchange(ref _owner, null)?.Remove(this);
		}
	}
}
