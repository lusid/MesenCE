using System.Runtime.InteropServices;
using Mesen.Interop;
using Mesen.Mcp;

namespace UI.Tests.Mcp;

public sealed class McpNativeInteropTests
{
	[Fact]
	public void SaveStateInteropApiResult_HasFixedNumericValues()
	{
		Assert.Equal(0, (int)InteropApiResult.Success);
		Assert.Equal(1, (int)InteropApiResult.NoGameLoaded);
		Assert.Equal(2, (int)InteropApiResult.InvalidArgument);
		Assert.Equal(3, (int)InteropApiResult.InvalidData);
		Assert.Equal(4, (int)InteropApiResult.PayloadTooLarge);
		Assert.Equal(5, (int)InteropApiResult.AllocationFailed);
		Assert.Equal(6, (int)InteropApiResult.EncodeFailed);
	}

	[Fact]
	public void SaveStateInteropOwnedBuffer_HasFixed64BitAbiLayout()
	{
		Assert.Equal(LayoutKind.Sequential, typeof(InteropOwnedBuffer).StructLayoutAttribute?.Value);
		Assert.Equal(typeof(IntPtr), typeof(InteropOwnedBuffer).GetField(nameof(InteropOwnedBuffer.Data))?.FieldType);
		Assert.Equal(typeof(uint), typeof(InteropOwnedBuffer).GetField(nameof(InteropOwnedBuffer.Length))?.FieldType);
		Assert.Equal(0, Marshal.OffsetOf<InteropOwnedBuffer>(nameof(InteropOwnedBuffer.Data)).ToInt32());
		Assert.Equal(8, Marshal.OffsetOf<InteropOwnedBuffer>(nameof(InteropOwnedBuffer.Length)).ToInt32());
		Assert.Equal(16, Marshal.SizeOf<InteropOwnedBuffer>());
	}

	[Fact]
	public void SaveStateCreate_CopiesNativeBytesAndReleasesBufferOnce()
	{
		IntPtr pointer = Marshal.AllocHGlobal(3);
		Marshal.Copy(new byte[] { 0x4D, 0x53, 0x53 }, 0, pointer, 3);
		int releaseCount = 0;

		InteropBufferResult result = EmuApi.CreateSaveState(
			(out InteropOwnedBuffer buffer) => {
				buffer = new InteropOwnedBuffer { Data = pointer, Length = 3 };
				return InteropApiResult.Success;
			},
			data => {
				Assert.Equal(pointer, data);
				releaseCount++;
				Marshal.FreeHGlobal(data);
			});

		Assert.Equal(InteropApiResult.Success, result.Result);
		Assert.Equal(new byte[] { 0x4D, 0x53, 0x53 }, result.Data);
		Assert.Equal(1, releaseCount);
	}

	[Fact]
	public void SaveStateAdapter_InvalidNativeResultBecomesInteropFailure()
	{
		MesenMcpEmulatorApi api = new(
			() => new InteropBufferResult((InteropApiResult)int.MaxValue, []),
			_ => (InteropApiResult)int.MaxValue);

		Assert.Equal("interop_failure", api.CreateSaveState().Error?.Code);
		Assert.Equal("interop_failure", api.LoadSaveState([0x4D, 0x53, 0x53]).Error?.Code);
	}

	[Fact]
	public void SaveStateFake_InvokesCreateAndLoadHandlers()
	{
		byte[] state = [0x4D, 0x53, 0x53];
		FakeMcpEmulatorApi api = new() {
			CreateSaveStateHandler = () => McpServiceResult<byte[]>.Success(state),
			LoadSaveStateHandler = data => McpServiceResult<bool>.Success(ReferenceEquals(state, data))
		};

		McpServiceResult<byte[]> created = api.CreateSaveState();
		McpServiceResult<bool> loaded = api.LoadSaveState(created.Value!);

		Assert.Same(state, created.Value);
		Assert.True(loaded.Value);
		Assert.Equal(1, api.CreateSaveStateCalls);
		Assert.Equal(1, api.LoadSaveStateCalls);
	}
}
