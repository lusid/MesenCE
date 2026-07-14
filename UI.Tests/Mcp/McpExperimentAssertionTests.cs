using Mesen.Mcp;

namespace UI.Tests.Mcp;

public sealed class McpExperimentAssertionTests
{
	[Theory]
	[InlineData(new byte[] { 0x7F }, "little", false, 127)]
	[InlineData(new byte[] { 0xFF }, "little", true, -1)]
	[InlineData(new byte[] { 0x34, 0x12 }, "little", false, 0x1234)]
	[InlineData(new byte[] { 0x12, 0x34 }, "big", false, 0x1234)]
	[InlineData(new byte[] { 0x00, 0x00, 0x00, 0x80 }, "little", true, int.MinValue)]
	[InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, "big", false, uint.MaxValue)]
	public void DecodeScalar_HandlesWidthsSignednessAndByteOrder(byte[] data, string byteOrder, bool signed, long expected)
	{
		Assert.Equal(expected, McpExperimentAssertions.DecodeScalar(data, signed, byteOrder));
	}

	[Theory]
	[InlineData("equal", new byte[] { 1, 2 }, new byte[] { 1, 2 }, true)]
	[InlineData("equal", new byte[] { 1, 2 }, new byte[] { 1, 3 }, false)]
	[InlineData("not_equal", new byte[] { 1, 2 }, new byte[] { 1, 3 }, true)]
	[InlineData("not_equal", new byte[] { 1, 2 }, new byte[] { 1, 2 }, false)]
	[InlineData("changed", new byte[] { 1, 2 }, new byte[] { 1, 3 }, true)]
	[InlineData("changed", new byte[] { 1, 2 }, new byte[] { 1, 2 }, false)]
	[InlineData("unchanged", new byte[] { 1, 2 }, new byte[] { 1, 2 }, true)]
	[InlineData("unchanged", new byte[] { 1, 2 }, new byte[] { 1, 3 }, false)]
	public void RawOperators_AreDeterministic(string operation, byte[] actual, byte[] comparison, bool expected)
	{
		Assert.Equal(expected, McpExperimentAssertions.EvaluateRaw(operation, actual, comparison));
	}

	[Theory]
	[InlineData("equal", 4, 4, 0, 0, true)]
	[InlineData("not_equal", 4, 5, 0, 0, true)]
	[InlineData("range", 4, 0, 3, 5, true)]
	[InlineData("range", 6, 0, 3, 5, false)]
	[InlineData("relative_equal", 4, 4, 0, 0, true)]
	[InlineData("relative_not_equal", 4, 5, 0, 0, true)]
	[InlineData("increased", 5, 4, 0, 0, true)]
	[InlineData("decreased", 3, 4, 0, 0, true)]
	[InlineData("changed", 3, 4, 0, 0, true)]
	[InlineData("unchanged", 4, 4, 0, 0, true)]
	public void ScalarOperators_AreDeterministic(string operation, long actual, long comparison, long minimum, long maximum, bool expected)
	{
		Assert.Equal(expected, McpExperimentAssertions.EvaluateScalar(operation, actual, comparison, minimum, maximum, 0));
	}

	[Fact]
	public void MaskedEquality_UsesWidthLimitedTwosComplementBits()
	{
		Assert.True(McpExperimentAssertions.EvaluateScalar("masked_equal", -1, 0x0F, 0, 0, 0x0F));
		Assert.False(McpExperimentAssertions.EvaluateScalar("masked_equal", -1, 0x07, 0, 0, 0x0F));
	}

	[Fact]
	public void ObservationResult_OwnsDataAndDecodesScalar()
	{
		McpValidatedObservation observation = new(0, "value", 1, "final", "ram", 3, 2, new(2, true, "big"));
		byte[] data = [0xFF, 0xFE];

		McpObservationResult result = McpExperimentAssertions.CreateObservationResult(observation, data);
		data[0] = 0;

		Assert.Equal([255, 254], result.Data);
		Assert.Equal("FFFE", result.Hex);
		Assert.Equal(-2, result.DecodedValue);
	}

	[Fact]
	public void CheckpointEvaluation_StopsAtFirstFailureAndSummarizesSkippedAssertions()
	{
		RunExperimentRequest request = CreateEvaluationRequest(failFast: true);
		McpValidatedExperiment experiment = Validate(request);
		McpObservationResult?[] observations = CreateResults(experiment, 1, 2);

		McpAssertionEvaluation evaluation = McpExperimentAssertions.EvaluateCheckpoint(experiment, 1, observations, []);

		McpAssertionResult failure = Assert.Single(evaluation.Results);
		Assert.False(failure.Passed);
		Assert.True(evaluation.StopRequested);
		Assert.Equal(new McpAssertionSummary(3, 0, 1, 2), evaluation.Summary);
	}

	[Fact]
	public void CheckpointEvaluation_ContinuesAfterFailuresWhenFailFastIsFalse()
	{
		McpValidatedExperiment experiment = Validate(CreateEvaluationRequest(failFast: false));
		McpObservationResult?[] observations = CreateResults(experiment, 1, 2);

		McpAssertionEvaluation evaluation = McpExperimentAssertions.EvaluateCheckpoint(experiment, 1, observations, []);

		Assert.Equal([false, true, true], evaluation.Results.Select(result => result.Passed));
		Assert.False(evaluation.StopRequested);
		Assert.Equal(new McpAssertionSummary(3, 2, 1, 0), evaluation.Summary);
	}

	[Fact]
	public void CheckpointEvaluation_EvaluatesRangeWithoutSingleComparisonOperand()
	{
		RunExperimentRequest request = CreateEvaluationRequest(failFast: false) with {
			Assertions = [new("range", "final", "after", "range", null, null, 1, 3, null, null)]
		};
		McpValidatedExperiment experiment = Validate(request);
		McpObservationResult?[] observations = CreateResults(experiment, 1, 2);

		McpAssertionEvaluation evaluation = McpExperimentAssertions.EvaluateCheckpoint(experiment, 1, observations, []);

		Assert.True(Assert.Single(evaluation.Results).Passed);
		Assert.Equal(new McpAssertionSummary(1, 1, 0, 0), evaluation.Summary);
	}

	private static RunExperimentRequest CreateEvaluationRequest(bool failFast) => new(
		"Nes",
		null,
		[new(1, [], null)],
		1000,
		[
			new("before", "initial", "ram", 0, 1, new(1, false, "little")),
			new("after", "final", "ram", 0, 1, new(1, false, "little"))
		],
		[
			new("same", "final", "after", "relative_equal", null, null, null, null, null, "before"),
			new("greater", "final", "after", "increased", null, null, null, null, null, "before"),
			new("exact", "final", "after", "equal", null, 2, null, null, null, null)
		],
		false,
		failFast);

	private static McpValidatedExperiment Validate(RunExperimentRequest request)
	{
		McpServiceResult<McpValidatedExperiment> result = McpExperimentAssertions.Validate(
			request,
			new("Nes", [new("ram", "RAM", 256, true, true)]));
		return Assert.IsType<McpValidatedExperiment>(result.Value);
	}

	private static McpObservationResult?[] CreateResults(McpValidatedExperiment experiment, byte before, byte after)
	{
		McpObservationResult?[] results = new McpObservationResult?[experiment.Observations.Count];
		results[0] = McpExperimentAssertions.CreateObservationResult(experiment.Observations[0], [before]);
		results[1] = McpExperimentAssertions.CreateObservationResult(experiment.Observations[1], [after]);
		return results;
	}
}
