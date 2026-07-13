using Mesen.Debugger;
using Mesen.Interop;
using static Mesen.Debugger.BreakpointManager;

namespace UI.Tests.Debugger;

public sealed class BreakpointManagerTests : IDisposable
{
	private readonly IDisposable _nativeSetterOverride;
	private readonly ExternalBreakpointCollection _external;
	private IReadOnlyList<InteropBreakpoint> _nativeBreakpoints = [];

	public BreakpointManagerTests()
	{
		BreakpointManager.ResetForTests();
		_nativeSetterOverride = BreakpointManager.OverrideNativeBreakpointSetterForTests(
			breakpoints => _nativeBreakpoints = breakpoints
		);
		_external = BreakpointManager.CreateExternalBreakpointCollection();
	}

	[Fact]
	public void Replace_PreservesInternalBreakpointsAndMapsStableIdsAfterReordering()
	{
		ExternalBreakpointCollection external = _external;
		BreakpointManager.AddCpuType(CpuType.Nes);
		BreakpointManager.AddBreakpoints([CreateBreakpoint(0x8000)]);
		BreakpointManager.Asserts = [CreateBreakpoint(0x9000)];
		BreakpointManager.AddTemporaryBreakpoint(CreateBreakpoint(0xA000));

		external.Replace([
			new ExternalBreakpoint(41, CreateBreakpoint(0xB000)),
			new ExternalBreakpoint(99, CreateBreakpoint(0xC000))
		]);

		IReadOnlyList<InteropBreakpoint> nativeBreakpoints = _nativeBreakpoints;
		Assert.Equal(5, nativeBreakpoints.Count);
		Assert.Contains(nativeBreakpoints, bp => bp.StartAddress == 0x8000);
		Assert.Contains(nativeBreakpoints, bp => bp.StartAddress == 0x9000);
		Assert.Contains(nativeBreakpoints, bp => bp.StartAddress == 0xA000);

		external.Replace([
			new ExternalBreakpoint(99, CreateBreakpoint(0xC000))
		]);

		Assert.Equal(4, _nativeBreakpoints.Count);
		Assert.Contains(_nativeBreakpoints, bp => bp.StartAddress == 0x8000);
		Assert.Contains(_nativeBreakpoints, bp => bp.StartAddress == 0x9000);
		Assert.Contains(_nativeBreakpoints, bp => bp.StartAddress == 0xA000);
		int previousExternalNativeId = _nativeBreakpoints.Single(bp => bp.StartAddress == 0xC000).Id;

		BreakpointManager.AddBreakpoints([CreateBreakpoint(0x8100)]);

		int externalNativeId = _nativeBreakpoints.Single(bp => bp.StartAddress == 0xC000).Id;
		Assert.NotEqual(previousExternalNativeId, externalNativeId);
		Assert.True(external.TryGetStableId(externalNativeId, out long stableId));
		Assert.Equal(99, stableId);
		Assert.Null(BreakpointManager.GetBreakpointById(externalNativeId));
	}

	[Fact]
	public void Replace_EmitsExternalBreakpointWithoutActiveUiCpu()
	{
		_external.Replace([
			new ExternalBreakpoint(41, CreateBreakpoint(0xB000))
		]);

		InteropBreakpoint nativeBreakpoint = Assert.Single(_nativeBreakpoints);
		Assert.Equal(CpuType.Nes, nativeBreakpoint.CpuType);
		Assert.Equal(0xB000, nativeBreakpoint.StartAddress);
	}

	public void Dispose()
	{
		_external.Dispose();
		_nativeSetterOverride.Dispose();
		BreakpointManager.ResetForTests();
	}

	private static Breakpoint CreateBreakpoint(uint address) => new()
	{
		CpuType = CpuType.Nes,
		MemoryType = MemoryType.NesMemory,
		StartAddress = address,
		EndAddress = address,
		BreakOnExec = true
	};
}
