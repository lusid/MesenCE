using System;
using System.Diagnostics;

namespace Mesen.Mcp;

internal interface IMcpMonotonicClock
{
	long GetTimestamp();
	TimeSpan GetElapsedTime(long start, long end);
}

internal sealed class McpMonotonicClock : IMcpMonotonicClock
{
	internal static McpMonotonicClock Instance { get; } = new();

	private McpMonotonicClock() { }

	public long GetTimestamp() => Stopwatch.GetTimestamp();
	public TimeSpan GetElapsedTime(long start, long end) => Stopwatch.GetElapsedTime(start, end);
}
