using System.Threading;

namespace Mesen.Mcp;

internal readonly record struct McpStateIdentity(
	long RomIdentity,
	long MutableStateGeneration,
	long StateLoadedSequence = 0);

internal sealed class McpEmulatorIdentity
{
	private long _romIdentity;
	private long _mutableStateGeneration;
	private long _stateLoadedSequence;

	internal McpStateIdentity Current => new(
		Volatile.Read(ref _romIdentity),
		Volatile.Read(ref _mutableStateGeneration),
		Volatile.Read(ref _stateLoadedSequence));

	internal void NotifyMutableStateChanged() => Interlocked.Increment(ref _mutableStateGeneration);

	internal void NotifyStateLoaded()
	{
		Interlocked.Increment(ref _mutableStateGeneration);
		Interlocked.Increment(ref _stateLoadedSequence);
	}

	internal void NotifyRomChanged()
	{
		Interlocked.Increment(ref _romIdentity);
		Interlocked.Increment(ref _mutableStateGeneration);
	}
}
