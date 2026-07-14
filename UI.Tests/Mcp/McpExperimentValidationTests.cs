using Mesen.Mcp;

namespace UI.Tests.Mcp;

public sealed class McpExperimentValidationTests
{
	private static readonly McpExperimentValidationContext Context = new(
		"Nes",
		[
			new("ram", "RAM", 65536, true, true),
			new("rom", "ROM", 32768, true, false),
			new("write-only", "Write only", 256, false, true)
		]);

	[Fact]
	public void ValidRequest_ResolvesOrderedCheckpointsAndReferences()
	{
		RunExperimentRequest request = ValidRequest();

		McpValidatedExperiment experiment = Assert.IsType<McpValidatedExperiment>(
			McpExperimentAssertions.Validate(request, Context).Value);

		Assert.Equal(["initial", "middle", "final"], experiment.Checkpoints.Select(item => item.Name));
		Assert.Equal(3, experiment.TotalFrames);
		Assert.Equal(4, experiment.TotalObservedBytes);
		Assert.Equal(0, experiment.Assertions[0].ReferenceObservationIndex);
		Assert.Equal(1, experiment.Assertions[0].ObservationIndex);
		Assert.Equal(1, experiment.Segments[0].CheckpointIndex);
	}

	public static TheoryData<string, Func<RunExperimentRequest, RunExperimentRequest>, string> InvalidRequests => new()
	{
		{ "wrong CPU case", request => request with { Cpu = "nes" }, "invalid_request" },
		{ "no segments", request => request with { Segments = [] }, "invalid_request" },
		{ "zero frames", request => request with { Segments = [new(0, [], null)] }, "invalid_request" },
		{ "reserved checkpoint", request => request with { Segments = [new(1, [], "initial")] }, "invalid_checkpoint" },
		{ "duplicate checkpoint", request => request with { Segments = [new(1, [], "same"), new(1, [], "same")] }, "invalid_checkpoint" },
		{ "missing observation checkpoint", request => request with { Observations = [request.Observations[0] with { Checkpoint = "missing" }] }, "invalid_checkpoint" },
		{ "duplicate observation", request => request with { Observations = [request.Observations[0], request.Observations[0]] }, "invalid_request" },
		{ "null observation checkpoint", request => request with { Observations = [request.Observations[0] with { Checkpoint = null! }] }, "invalid_checkpoint" },
		{ "null observation space", request => request with { Observations = [request.Observations[0] with { Space = null! }] }, "invalid_request" },
		{ "unknown space", request => request with { Observations = [request.Observations[0] with { Space = "missing" }] }, "invalid_request" },
		{ "unreadable space", request => request with { Observations = [request.Observations[0] with { Space = "write-only" }] }, "invalid_request" },
		{ "zero count", request => request with { Observations = [request.Observations[0] with { Count = 0 }] }, "invalid_range" },
		{ "range past end", request => request with { Observations = [request.Observations[0] with { Address = 65535, Count = 2 }] }, "invalid_range" },
		{ "address arithmetic overflow", request => request with { Observations = [request.Observations[0] with { Address = uint.MaxValue, Count = 2 }] }, "invalid_range" },
		{ "invalid width", request => request with { Observations = [request.Observations[0] with { Decode = new(3, false, "little") }] }, "invalid_request" },
		{ "invalid byte order case", request => request with { Observations = [request.Observations[0] with { Decode = new(2, false, "Little") }] }, "invalid_request" },
		{ "count differs from width", request => request with { Observations = [request.Observations[0] with { Count = 1, Decode = new(2, false, "little") }] }, "invalid_request" },
		{ "duplicate assertion", request => request with { Assertions = [request.Assertions[0], request.Assertions[0]] }, "invalid_request" },
		{ "null assertion observation", request => request with { Assertions = [request.Assertions[0] with { ObservationId = null! }] }, "invalid_request" },
		{ "null assertion checkpoint", request => request with { Assertions = [request.Assertions[0] with { Checkpoint = null! }] }, "invalid_checkpoint" },
		{ "assertion checkpoint mismatch", request => request with { Assertions = [request.Assertions[0] with { Checkpoint = "final" }] }, "invalid_checkpoint" },
		{ "unknown operator case", request => request with { Assertions = [request.Assertions[0] with { Operator = "Changed" }] }, "invalid_request" },
		{ "forward reference", request => request with { Assertions = [request.Assertions[0] with { ObservationId = "before", Checkpoint = "initial", ReferenceObservationId = "after" }] }, "invalid_checkpoint" },
		{ "raw reference length mismatch", request => request with { Observations = [request.Observations[0], request.Observations[1] with { Count = 1 }], Assertions = [request.Assertions[0]] }, "invalid_request" },
	};

	[Theory]
	[MemberData(nameof(InvalidRequests))]
	public void InvalidRequest_IsRejectedWithoutMutation(string name, Func<RunExperimentRequest, RunExperimentRequest> change, string code)
	{
		_ = name;
		McpServiceResult<McpValidatedExperiment> result = McpExperimentAssertions.Validate(change(ValidRequest()), Context);

		Assert.Equal(code, result.Error?.Code);
	}

	public static TheoryData<string, McpDecodeRequest?, string, int[]?, long?, long?, long?, ulong?, string?, bool> OperandCases => new()
	{
		{ "raw equal", null, "equal", new[] { 1, 2 }, null, null, null, null, null, true },
		{ "raw byte below range", null, "equal", new[] { -1, 2 }, null, null, null, null, null, false },
		{ "raw byte above range", null, "not_equal", new[] { 1, 256 }, null, null, null, null, null, false },
		{ "raw wrong expected length", null, "equal", new[] { 1 }, null, null, null, null, null, false },
		{ "raw scalar operand", null, "equal", new[] { 1, 2 }, 1, null, null, null, null, false },
		{ "raw changed", null, "changed", null, null, null, null, null, "before", true },
		{ "raw changed expected bytes forbidden", null, "changed", new[] { 1, 2 }, null, null, null, null, "before", false },
		{ "unsigned equal maximum", new(1, false, "little"), "equal", null, 255, null, null, null, null, true },
		{ "unsigned equal overflow", new(1, false, "little"), "equal", null, 256, null, null, null, null, false },
		{ "unsigned negative", new(1, false, "little"), "not_equal", null, -1, null, null, null, null, false },
		{ "signed equal minimum", new(1, true, "little"), "equal", null, -128, null, null, null, null, true },
		{ "signed equal overflow", new(1, true, "little"), "equal", null, 128, null, null, null, null, false },
		{ "range", new(2, true, "little"), "range", null, null, -32768, 32767, null, null, true },
		{ "reversed range", new(2, true, "little"), "range", null, null, 1, 0, null, null, false },
		{ "range missing maximum", new(2, true, "little"), "range", null, null, 0, null, null, null, false },
		{ "masked", new(4, false, "little"), "masked_equal", null, uint.MaxValue, null, null, uint.MaxValue, null, true },
		{ "mask width overflow", new(1, false, "little"), "masked_equal", null, 0, null, null, 256, null, false },
		{ "relative", new(2, false, "little"), "relative_not_equal", null, null, null, null, null, "before", true },
		{ "relative expected forbidden", new(2, false, "little"), "increased", null, 1, null, null, null, "before", false },
		{ "decoded raw bytes forbidden", new(2, false, "little"), "equal", new[] { 1, 2 }, 1, null, null, null, null, false },
	};

	[Theory]
	[MemberData(nameof(OperandCases))]
	public void OperatorOperandsAndScalarRanges_AreValidated(
		string name,
		McpDecodeRequest? decode,
		string operation,
		int[]? bytes,
		long? expected,
		long? minimum,
		long? maximum,
		ulong? mask,
		string? reference,
		bool valid)
	{
		_ = name;
		McpMemoryObservationRequest before = new("before", "initial", "ram", 0, decode?.Width ?? 2, decode);
		McpMemoryObservationRequest after = new("after", "final", "ram", 0, decode?.Width ?? 2, decode);
		RunExperimentRequest request = ValidRequest() with {
			Observations = [before, after],
			Assertions = [new("test", "final", "after", operation, bytes, expected, minimum, maximum, mask, reference)]
		};

		Assert.Equal(valid, McpExperimentAssertions.Validate(request, Context).IsSuccess);
	}

	[Theory]
	[InlineData(1, false, 255)]
	[InlineData(2, false, 65535)]
	[InlineData(4, false, 4294967295)]
	[InlineData(1, true, 127)]
	[InlineData(2, true, 32767)]
	[InlineData(4, true, 2147483647)]
	public void ScalarMaximum_IsAcceptedForEveryWidth(int width, bool signed, long maximum)
	{
		RunExperimentRequest request = ScalarRequest(new(width, signed, "big"), "equal", maximum);
		Assert.True(McpExperimentAssertions.Validate(request, Context).IsSuccess);
	}

	[Theory]
	[InlineData("relative_equal")]
	[InlineData("relative_not_equal")]
	[InlineData("increased")]
	[InlineData("decreased")]
	[InlineData("changed")]
	[InlineData("unchanged")]
	public void EveryScalarReferenceOperator_IsAccepted(string operation)
	{
		RunExperimentRequest request = ScalarRequest(new(2, true, "little"), operation, null) with {
			Assertions = [new("test", "final", "after", operation, null, null, null, null, null, "before")]
		};

		Assert.True(McpExperimentAssertions.Validate(request, Context).IsSuccess);
	}

	[Fact]
	public void DecodeCompatibility_RequiresIdenticalWidthSignednessAndByteOrder()
	{
		foreach(McpDecodeRequest incompatible in new McpDecodeRequest[] { new(1, false, "little"), new(2, true, "little"), new(2, false, "big") }) {
			RunExperimentRequest request = ScalarRequest(new(2, false, "little"), "changed", null) with {
				Observations = [
					new("before", "initial", "ram", 0, incompatible.Width, incompatible),
					new("after", "final", "ram", 0, 2, new(2, false, "little"))
				]
			};
			Assert.False(McpExperimentAssertions.Validate(request, Context).IsSuccess);
		}
	}

	[Fact]
	public void ExactLimits_AreAccepted()
	{
		McpInputSegment[] segments = Enumerable.Range(0, 256)
			.Select(index => new McpInputSegment(index < 16 ? 15 : 14, [], $"c{index}"))
			.ToArray();
		McpMemoryObservationRequest[] observations = Enumerable.Range(0, 256)
			.Select(index => new McpMemoryObservationRequest($"o{index}", "final", "ram", 0, 256, null))
			.ToArray();
		McpAssertionRequest[] assertions = Enumerable.Range(0, 256)
			.Select(index => new McpAssertionRequest($"a{index}", "final", $"o{index}", "equal", new int[256], null, null, null, null, null))
			.ToArray();

		McpServiceResult<McpValidatedExperiment> result = McpExperimentAssertions.Validate(
			ValidRequest() with { Segments = segments, Observations = observations, Assertions = assertions },
			Context);

		Assert.True(result.IsSuccess);
		Assert.Equal(3600, result.Value?.TotalFrames);
		Assert.Equal(65536, result.Value?.TotalObservedBytes);
	}

	[Theory]
	[InlineData("segments")]
	[InlineData("frames")]
	[InlineData("observations")]
	[InlineData("assertions")]
	[InlineData("bytes")]
	public void Limits_AreRejectedAboveMaximum(string limit)
	{
		RunExperimentRequest request = ValidRequest();
		request = limit switch {
			"segments" => request with { Segments = Enumerable.Repeat(new McpInputSegment(1, [], null), 257).ToArray() },
			"frames" => request with { Segments = [new(3601, [], null)] },
			"observations" => request with { Observations = Enumerable.Range(0, 257).Select(index => new McpMemoryObservationRequest($"o{index}", "final", "ram", 0, 1, null)).ToArray() },
			"assertions" => request with { Assertions = Enumerable.Range(0, 257).Select(index => request.Assertions[0] with { Id = $"a{index}" }).ToArray() },
			"bytes" => request with { Observations = [new("large", "final", "ram", 0, 65536, null), new("extra", "final", "ram", 0, 1, null)], Assertions = [] },
			_ => throw new ArgumentOutOfRangeException(nameof(limit))
		};

		Assert.Equal("resource_limit", McpExperimentAssertions.Validate(request, Context).Error?.Code);
	}

	private static RunExperimentRequest ValidRequest() => new(
		"Nes",
		null,
		[new(3, [], "middle")],
		1000,
		[
			new("before", "initial", "ram", 0, 2, null),
			new("after", "middle", "ram", 0, 2, null)
		],
		[new("changed", "middle", "after", "changed", null, null, null, null, null, "before")],
		false,
		false);

	private static RunExperimentRequest ScalarRequest(McpDecodeRequest decode, string operation, long? expected) => new(
		"Nes",
		null,
		[new(1, [], null)],
		1000,
		[
			new("before", "initial", "ram", 0, decode.Width, decode),
			new("after", "final", "ram", 0, decode.Width, decode)
		],
		[new("test", "final", "after", operation, null, expected, null, null, null, operation is "changed" ? "before" : null)],
		false,
		false);
}
