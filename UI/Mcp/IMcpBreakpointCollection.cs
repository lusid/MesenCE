using System;
using System.Collections.Generic;
using Mesen.Debugger;

namespace Mesen.Mcp;

internal interface IMcpBreakpointCollection : IDisposable
{
	void Replace(IReadOnlyList<BreakpointManager.ExternalBreakpoint> breakpoints);
	bool TryGetStableId(int nativeBreakpointId, out long stableId);
	void Detach() => Dispose();
}

internal sealed class McpBreakpointCollection : IMcpBreakpointCollection
{
	private readonly BreakpointManager.ExternalBreakpointCollection _collection =
		BreakpointManager.CreateExternalBreakpointCollection();
	public void Replace(IReadOnlyList<BreakpointManager.ExternalBreakpoint> breakpoints) => _collection.Replace(breakpoints);
	public bool TryGetStableId(int nativeBreakpointId, out long stableId) => _collection.TryGetStableId(nativeBreakpointId, out stableId);
	public void Detach() => _collection.Detach();
	public void Dispose() => _collection.Dispose();
}
