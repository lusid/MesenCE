using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using Mesen.Interop;
using Mesen.Mcp;

namespace UI.Tests.Mcp;

public sealed class McpNativeInteropTests
{
	private static string ReadRepositoryFile(string relativePath, [CallerFilePath] string testFile = "")
	{
		string repositoryRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", ".."));
		return File.ReadAllText(Path.Combine(repositoryRoot, relativePath));
	}

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

	[Fact]
	public void ScreenshotInteropInfo_HasFixedAbiLayout()
	{
		Assert.Equal(LayoutKind.Sequential, typeof(InteropScreenshotInfo).StructLayoutAttribute?.Value);
		Assert.Equal(0, Marshal.OffsetOf<InteropScreenshotInfo>(nameof(InteropScreenshotInfo.Width)).ToInt32());
		Assert.Equal(4, Marshal.OffsetOf<InteropScreenshotInfo>(nameof(InteropScreenshotInfo.Height)).ToInt32());
		Assert.Equal(8, Marshal.OffsetOf<InteropScreenshotInfo>(nameof(InteropScreenshotInfo.FrameNumber)).ToInt32());
		Assert.Equal(12, Marshal.OffsetOf<InteropScreenshotInfo>(nameof(InteropScreenshotInfo.PngLength)).ToInt32());
		Assert.Equal(16, Marshal.SizeOf<InteropScreenshotInfo>());
		Assert.Equal(7, (int)InteropApiResult.NoFrame);
	}

	[Fact]
	public void ScreenshotCapture_CopiesBytesAndMetadataAndReleasesBufferOnce()
	{
		byte[] png = [0x89, 0x50, 0x4E, 0x47];
		IntPtr pointer = Marshal.AllocHGlobal(png.Length);
		Marshal.Copy(png, 0, pointer, png.Length);
		int releaseCount = 0;

		InteropScreenshotResult result = EmuApi.CaptureScreenshot(
			(out InteropOwnedBuffer buffer, out InteropScreenshotInfo info) => {
				buffer = new InteropOwnedBuffer { Data = pointer, Length = (uint)png.Length };
				info = new InteropScreenshotInfo { Width = 256, Height = 240, FrameNumber = 123, PngLength = (uint)png.Length };
				return InteropApiResult.Success;
			},
			data => {
				Assert.Equal(pointer, data);
				releaseCount++;
				Marshal.FreeHGlobal(data);
			});

		Assert.Equal(InteropApiResult.Success, result.Result);
		Assert.Equal(png, result.Png);
		Assert.Equal(256u, result.Info.Width);
		Assert.Equal(240u, result.Info.Height);
		Assert.Equal(123u, result.Info.FrameNumber);
		Assert.Equal((uint)png.Length, result.Info.PngLength);
		Assert.Equal(1, releaseCount);
	}

	[Fact]
	public void ScreenshotCapture_NoFrameIsExplicitAndDoesNotReleaseNullBuffer()
	{
		int releaseCount = 0;

		InteropScreenshotResult result = EmuApi.CaptureScreenshot(
			(out InteropOwnedBuffer buffer, out InteropScreenshotInfo info) => {
				buffer = default;
				info = default;
				return InteropApiResult.NoFrame;
			},
			_ => releaseCount++);

		Assert.Equal(InteropApiResult.NoFrame, result.Result);
		Assert.Empty(result.Png);
		Assert.Equal(0, releaseCount);
	}

	[Theory]
	[InlineData(4097u, 1u, 1u, InteropApiResult.InvalidData)]
	[InlineData(1u, 4097u, 1u, InteropApiResult.InvalidData)]
	[InlineData(4096u, 4096u, 1u, InteropApiResult.Success)]
	[InlineData(4096u, 4097u, 1u, InteropApiResult.InvalidData)]
	[InlineData(1u, 1u, 0u, InteropApiResult.InvalidData)]
	[InlineData(1u, 1u, 8u * 1024u * 1024u + 1u, InteropApiResult.PayloadTooLarge)]
	public void ScreenshotValidateLimits_RejectsInvalidDimensionsPixelsAndPayload(
		uint width,
		uint height,
		uint pngLength,
		InteropApiResult expected)
	{
		InteropScreenshotInfo info = new() { Width = width, Height = height, PngLength = pngLength };

		Assert.Equal(expected, EmuApi.ValidateScreenshotLimits(info));
	}

	[Fact]
	public void ScreenshotCapture_RejectsInvalidNativePayloadAndReleasesBufferOnce()
	{
		IntPtr pointer = Marshal.AllocHGlobal(1);
		int releaseCount = 0;

		InteropScreenshotResult result = EmuApi.CaptureScreenshot(
			(out InteropOwnedBuffer buffer, out InteropScreenshotInfo info) => {
				buffer = new InteropOwnedBuffer { Data = pointer, Length = 1 };
				info = new InteropScreenshotInfo { Width = 256, Height = 240, FrameNumber = 9, PngLength = 2 };
				return InteropApiResult.Success;
			},
			data => {
				releaseCount++;
				Marshal.FreeHGlobal(data);
			});

		Assert.Equal(InteropApiResult.InvalidData, result.Result);
		Assert.Equal(9u, result.Info.FrameNumber);
		Assert.Empty(result.Png);
		Assert.Equal(1, releaseCount);
	}

	[Fact]
	public void ScreenshotAdapter_PreservesMetadataAndReportsCaptureFailures()
	{
		byte[] png = [0x89, 0x50, 0x4E, 0x47];
		InteropScreenshotInfo info = new() { Width = 256, Height = 240, FrameNumber = 456, PngLength = (uint)png.Length };
		MesenMcpEmulatorApi successApi = new(
			() => default,
			_ => default,
			() => new InteropScreenshotResult(InteropApiResult.Success, info, png));
		MesenMcpEmulatorApi noFrameApi = new(
			() => default,
			_ => default,
			() => new InteropScreenshotResult(InteropApiResult.NoFrame, default, []));
		MesenMcpEmulatorApi encodeFailureApi = new(
			() => default,
			_ => default,
			() => new InteropScreenshotResult(InteropApiResult.EncodeFailed, info, []));

		McpServiceResult<McpScreenshotCapture> capture = successApi.CaptureScreenshot();

		Assert.Equal(256, capture.Value?.Metadata.Width);
		Assert.Equal(240, capture.Value?.Metadata.Height);
		Assert.Equal(456u, capture.Value?.Metadata.FrameNumber);
		Assert.Equal(png, capture.Value?.Png);
		Assert.Equal("no_frame", noFrameApi.CaptureScreenshot().Error?.Code);
		Assert.Equal("encoding_failed", encodeFailureApi.CaptureScreenshot().Error?.Code);
	}

	[Fact]
	public void ScreenshotNativeCapture_HudDrawAndDisplayCopyAreInsideFrameLock()
	{
		string filter = ReadRepositoryFile("Core/Shared/Video/BaseVideoFilter.cpp");
		string decoder = ReadRepositoryFile("Core/Shared/Video/VideoDecoder.cpp");
		int methodStart = filter.IndexOf("BaseVideoFilter::SendFrameWithHud", StringComparison.Ordinal);
		int lockIndex = filter.IndexOf("_frameLock.AcquireSafe()", methodStart, StringComparison.Ordinal);
		int filterIndex = filter.IndexOf("ApplyFilter(ppuOutputBuffer)", lockIndex, StringComparison.Ordinal);
		int hudIndex = filter.IndexOf("debugHud->Draw", filterIndex, StringComparison.Ordinal);
		int copyIndex = filter.IndexOf("memcpy(_displayBuffer", hudIndex, StringComparison.Ordinal);

		Assert.True(methodStart >= 0 && lockIndex > methodStart);
		Assert.True(filterIndex > lockIndex && hudIndex > filterIndex && copyIndex > hudIndex);
		Assert.Contains("SendFrameWithHud", decoder);
		Assert.DoesNotContain("GetDebugHud()->Draw", decoder);
		Assert.DoesNotContain("videoFilter->GetOutputBuffer()", decoder);
	}

	[Fact]
	public void ScreenshotNativeCapture_InvalidScaleDoesNotMultiplyFailureMetadata()
	{
		string filter = ReadRepositoryFile("Core/Shared/Video/BaseVideoFilter.cpp");

		Assert.Contains("capture.Width = 0;", filter);
		Assert.Contains("capture.Height = 0;", filter);
		Assert.DoesNotContain("capture.Width = filterScale == 0 ? 0 : frameInfo.Width * filterScale", filter);
		Assert.DoesNotContain("capture.Height = filterScale == 0 ? 0 : frameInfo.Height * filterScale", filter);
	}
}
