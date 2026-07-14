using System.Threading;

namespace Mesen.Mcp;

internal readonly record struct McpStateIdentity(long RomIdentity, long MutableStateGeneration);

internal sealed class McpEmulatorIdentity
{
	private long _romIdentity;
	private long _mutableStateGeneration;

	internal McpStateIdentity Current => new(
		Volatile.Read(ref _romIdentity),
		Volatile.Read(ref _mutableStateGeneration));

	internal void NotifyMutableStateChanged() => Interlocked.Increment(ref _mutableStateGeneration);

	internal void NotifyRomChanged()
	{
		Interlocked.Increment(ref _romIdentity);
		Interlocked.Increment(ref _mutableStateGeneration);
	}
}
