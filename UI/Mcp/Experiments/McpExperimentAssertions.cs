using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;

namespace Mesen.Mcp;

internal sealed record McpExperimentValidationContext(string ActiveCpu, IReadOnlyList<MemorySpace> MemorySpaces);

internal sealed record McpValidatedSegment(int Index, int Frames, IReadOnlyList<McpControllerInput> Controllers, int? CheckpointIndex);

internal sealed record McpValidatedCheckpoint(
	int Index,
	string Name,
	int? SegmentIndex,
	IReadOnlyList<int> ObservationIndices,
	IReadOnlyList<int> AssertionIndices);

internal sealed record McpValidatedObservation(
	int Index,
	string Id,
	int CheckpointIndex,
	string Checkpoint,
	string Space,
	uint Address,
	int Count,
	McpDecodeRequest? Decode);

internal sealed record McpValidatedAssertion(
	int Index,
	string Id,
	int CheckpointIndex,
	string Checkpoint,
	int ObservationIndex,
	string ObservationId,
	string Operator,
	IReadOnlyList<int>? ExpectedBytes,
	long? ExpectedValue,
	long? MinimumValue,
	long? MaximumValue,
	ulong? Mask,
	int? ReferenceObservationIndex,
	string? ReferenceObservationId);

internal sealed record McpValidatedExperiment(
	string Cpu,
	string? SaveStateId,
	IReadOnlyList<McpValidatedSegment> Segments,
	int TimeoutMs,
	IReadOnlyList<McpValidatedCheckpoint> Checkpoints,
	IReadOnlyList<McpValidatedObservation> Observations,
	IReadOnlyList<McpValidatedAssertion> Assertions,
	bool CaptureFinalScreenshot,
	bool FailFast,
	int TotalFrames,
	int TotalObservedBytes);

internal sealed record McpAssertionEvaluation(
	IReadOnlyList<McpAssertionResult> Results,
	McpAssertionSummary Summary,
	bool StopRequested);

internal static class McpExperimentAssertions
{
	private static readonly HashSet<string> RawValueOperators = new(StringComparer.Ordinal) { "equal", "not_equal" };
	private static readonly HashSet<string> RelativeOperators = new(StringComparer.Ordinal) {
		"relative_equal", "relative_not_equal", "increased", "decreased", "changed", "unchanged"
	};
	private static readonly HashSet<string> RawRelativeOperators = new(StringComparer.Ordinal) { "changed", "unchanged" };

	internal static McpServiceResult<McpValidatedExperiment> Validate(
		RunExperimentRequest request,
		McpExperimentValidationContext context)
	{
		if(request is null || context is null) {
			return Invalid("Experiment request and validation context are required.");
		}
		if(request.Cpu != context.ActiveCpu) {
			return Invalid($"CPU must be exactly '{context.ActiveCpu}'.");
		}
		if(request.TimeoutMs is < McpAutomationLimits.MinExperimentTimeoutMs or > McpAutomationLimits.MaxExperimentTimeoutMs) {
			return Invalid($"Timeout must be from {McpAutomationLimits.MinExperimentTimeoutMs} through {McpAutomationLimits.MaxExperimentTimeoutMs} milliseconds.");
		}
		if(request.Segments is null || request.Segments.Count is < 1 or > McpAutomationLimits.MaxSegments) {
			return request.Segments?.Count > McpAutomationLimits.MaxSegments
				? Limit("Segment count exceeds the experiment limit.")
				: Invalid("At least one input segment is required.");
		}
		if(request.Observations is null || request.Assertions is null) {
			return Invalid("Observation and assertion collections are required.");
		}
		if(request.Observations.Count > McpAutomationLimits.MaxObservations) {
			return Limit("Observation count exceeds the experiment limit.");
		}
		if(request.Assertions.Count > McpAutomationLimits.MaxAssertions) {
			return Limit("Assertion count exceeds the experiment limit.");
		}

		List<string> checkpointNames = ["initial"];
		List<int?> checkpointSegments = [null];
		Dictionary<string, int> checkpointIndices = new(StringComparer.Ordinal) { ["initial"] = 0 };
		List<McpValidatedSegment> segments = new(request.Segments.Count);
		int totalFrames = 0;
		for(int index = 0; index < request.Segments.Count; index++) {
			McpInputSegment? segment = request.Segments[index];
			if(segment is null || segment.Frames <= 0 || segment.Controllers is null
				|| segment.Controllers.Any(controller => controller is null || controller.Buttons is null)) {
				return Invalid($"Segment {index} must have a positive frame count and a controller collection.");
			}
			try {
				totalFrames = checked(totalFrames + segment.Frames);
			} catch(OverflowException) {
				return Limit("Total segment frames exceed the experiment limit.");
			}
			if(totalFrames > McpAutomationLimits.MaxExperimentFrames) {
				return Limit("Total segment frames exceed the experiment limit.");
			}

			int? checkpointIndex = null;
			if(segment.Checkpoint is not null) {
				if(string.IsNullOrWhiteSpace(segment.Checkpoint)
					|| segment.Checkpoint is "initial" or "final"
					|| !checkpointIndices.TryAdd(segment.Checkpoint, checkpointNames.Count)) {
					return InvalidCheckpoint($"Segment checkpoint '{segment.Checkpoint}' is reserved, empty, or duplicated.");
				}
				checkpointIndex = checkpointNames.Count;
				checkpointNames.Add(segment.Checkpoint);
				checkpointSegments.Add(index);
			}
			segments.Add(new(index, segment.Frames, CopyControllers(segment.Controllers), checkpointIndex));
		}
		checkpointIndices.Add("final", checkpointNames.Count);
		checkpointNames.Add("final");
		checkpointSegments.Add(null);

		Dictionary<string, MemorySpace> spaces = new(StringComparer.Ordinal);
		if(context.MemorySpaces is null) {
			return Invalid("Memory-space capabilities are required.");
		}
		foreach(MemorySpace? space in context.MemorySpaces) {
			if(space is not null) {
				spaces.TryAdd(space.Id, space);
			}
		}

		List<McpValidatedObservation> observations = new(request.Observations.Count);
		Dictionary<string, int> observationIndices = new(StringComparer.Ordinal);
		int totalObservedBytes = 0;
		for(int index = 0; index < request.Observations.Count; index++) {
			McpMemoryObservationRequest? observation = request.Observations[index];
			if(observation is null || string.IsNullOrWhiteSpace(observation.Id) || !observationIndices.TryAdd(observation.Id, index)) {
				return Invalid("Observation IDs must be non-empty and unique.");
			}
			if(string.IsNullOrWhiteSpace(observation.Checkpoint)
				|| !checkpointIndices.TryGetValue(observation.Checkpoint, out int checkpointIndex)) {
				return InvalidCheckpoint($"Observation '{observation.Id}' names an unknown checkpoint.");
			}
			if(string.IsNullOrWhiteSpace(observation.Space)
				|| !spaces.TryGetValue(observation.Space, out MemorySpace? space)
				|| !space.CanRead
				|| space.Size < 0) {
				return Invalid($"Observation '{observation.Id}' names an unavailable readable memory space.");
			}
			if(!IsValidRange(observation.Address, observation.Count, space.Size)) {
				return RangeFailure($"Observation '{observation.Id}' is outside its memory space.");
			}
			if(observation.Decode is not null
				&& (observation.Decode.Width is not (1 or 2 or 4)
					|| observation.Decode.ByteOrder is not ("little" or "big")
					|| observation.Count != observation.Decode.Width)) {
				return Invalid($"Observation '{observation.Id}' has an invalid decode definition.");
			}
			try {
				totalObservedBytes = checked(totalObservedBytes + observation.Count);
			} catch(OverflowException) {
				return Limit("Total observed bytes exceed the experiment limit.");
			}
			if(totalObservedBytes > McpAutomationLimits.MaxObservedBytes) {
				return Limit("Total observed bytes exceed the experiment limit.");
			}
			observations.Add(new(
				index,
				observation.Id,
				checkpointIndex,
				checkpointNames[checkpointIndex],
				observation.Space,
				observation.Address,
				observation.Count,
				observation.Decode is null ? null : new(observation.Decode.Width, observation.Decode.Signed, observation.Decode.ByteOrder)));
		}

		List<McpValidatedAssertion> assertions = new(request.Assertions.Count);
		HashSet<string> assertionIds = new(StringComparer.Ordinal);
		for(int index = 0; index < request.Assertions.Count; index++) {
			McpAssertionRequest? assertion = request.Assertions[index];
			if(assertion is null || string.IsNullOrWhiteSpace(assertion.Id) || !assertionIds.Add(assertion.Id)) {
				return Invalid("Assertion IDs must be non-empty and unique.");
			}
			if(string.IsNullOrWhiteSpace(assertion.ObservationId)
				|| !observationIndices.TryGetValue(assertion.ObservationId, out int observationIndex)) {
				return Invalid($"Assertion '{assertion.Id}' names an unknown observation.");
			}
			McpValidatedObservation observation = observations[observationIndex];
			if(string.IsNullOrWhiteSpace(assertion.Checkpoint)
				|| !checkpointIndices.TryGetValue(assertion.Checkpoint, out int checkpointIndex)
				|| checkpointIndex != observation.CheckpointIndex) {
				return InvalidCheckpoint($"Assertion '{assertion.Id}' must use its observation's checkpoint.");
			}

			int? referenceIndex = null;
			if(assertion.ReferenceObservationId is not null) {
				if(!observationIndices.TryGetValue(assertion.ReferenceObservationId, out int resolvedReference)) {
					return Invalid($"Assertion '{assertion.Id}' names an unknown reference observation.");
				}
				McpValidatedObservation reference = observations[resolvedReference];
				if(reference.CheckpointIndex >= checkpointIndex) {
					return InvalidCheckpoint($"Assertion '{assertion.Id}' must reference an earlier checkpoint.");
				}
				if(!Compatible(observation, reference)) {
					return Invalid($"Assertion '{assertion.Id}' uses incompatible observations.");
				}
				referenceIndex = resolvedReference;
			}

			McpServiceError? operandError = ValidateOperands(assertion, observation, referenceIndex.HasValue);
			if(operandError is not null) {
				return McpServiceResult<McpValidatedExperiment>.Failure(operandError.Code, operandError.Message);
			}
			assertions.Add(new(
				index,
				assertion.Id,
				checkpointIndex,
				checkpointNames[checkpointIndex],
				observationIndex,
				observation.Id,
				assertion.Operator,
				assertion.ExpectedBytes is null ? null : Freeze(assertion.ExpectedBytes.ToArray()),
				assertion.ExpectedValue,
				assertion.MinimumValue,
				assertion.MaximumValue,
				assertion.Mask,
				referenceIndex,
				assertion.ReferenceObservationId));
		}

		List<McpValidatedCheckpoint> checkpoints = new(checkpointNames.Count);
		for(int index = 0; index < checkpointNames.Count; index++) {
			checkpoints.Add(new(
				index,
				checkpointNames[index],
				checkpointSegments[index],
				Freeze(observations.Where(item => item.CheckpointIndex == index).Select(item => item.Index).ToArray()),
				Freeze(assertions.Where(item => item.CheckpointIndex == index).Select(item => item.Index).ToArray())));
		}

		return McpServiceResult<McpValidatedExperiment>.Success(new(
			request.Cpu,
			request.SaveStateId,
			Freeze(segments.ToArray()),
			request.TimeoutMs,
			Freeze(checkpoints.ToArray()),
			Freeze(observations.ToArray()),
			Freeze(assertions.ToArray()),
			request.CaptureFinalScreenshot,
			request.FailFast,
			totalFrames,
			totalObservedBytes));
	}

	internal static ulong DecodeUnsigned(ReadOnlySpan<byte> data, string byteOrder)
	{
		ulong value = 0;
		if(byteOrder == "little") {
			for(int i = data.Length - 1; i >= 0; i--) {
				value = (value << 8) | data[i];
			}
		} else if(byteOrder == "big") {
			foreach(byte item in data) {
				value = (value << 8) | item;
			}
		} else {
			throw new ArgumentOutOfRangeException(nameof(byteOrder));
		}
		return value;
	}

	internal static long DecodeScalar(ReadOnlySpan<byte> data, bool signed, string byteOrder)
	{
		if(data.Length is not (1 or 2 or 4)) {
			throw new ArgumentOutOfRangeException(nameof(data));
		}
		ulong value = DecodeUnsigned(data, byteOrder);
		if(!signed) {
			return checked((long)value);
		}
		int bits = data.Length * 8;
		ulong sign = 1UL << (bits - 1);
		return (value & sign) == 0 ? (long)value : unchecked((long)(value | (ulong.MaxValue << bits)));
	}

	internal static McpObservationResult CreateObservationResult(McpValidatedObservation observation, ReadOnlySpan<byte> data)
	{
		if(data.Length != observation.Count) {
			throw new ArgumentException("Observation data length does not match the validated count.", nameof(data));
		}
		byte[] ownedData = data.ToArray();
		return new(
			observation.Id,
			observation.Checkpoint,
			observation.Space,
			observation.Address,
			observation.Count,
			ownedData.Select(item => (int)item).ToArray(),
			Convert.ToHexString(ownedData),
			observation.Decode is null ? null : DecodeScalar(ownedData, observation.Decode.Signed, observation.Decode.ByteOrder));
	}

	internal static bool EvaluateRaw(string operation, ReadOnlySpan<byte> actual, ReadOnlySpan<byte> comparison)
	{
		bool equal = actual.SequenceEqual(comparison);
		return operation switch {
			"equal" or "unchanged" => equal,
			"not_equal" or "changed" => !equal,
			_ => throw new ArgumentOutOfRangeException(nameof(operation))
		};
	}

	internal static bool EvaluateScalar(
		string operation,
		long actual,
		long comparison,
		long minimum,
		long maximum,
		ulong mask)
	{
		return operation switch {
			"equal" or "relative_equal" or "unchanged" => actual == comparison,
			"not_equal" or "relative_not_equal" or "changed" => actual != comparison,
			"range" => actual >= minimum && actual <= maximum,
			"masked_equal" => (unchecked((ulong)actual) & mask) == (unchecked((ulong)comparison) & mask),
			"increased" => actual > comparison,
			"decreased" => actual < comparison,
			_ => throw new ArgumentOutOfRangeException(nameof(operation))
		};
	}

	internal static McpAssertionEvaluation EvaluateCheckpoint(
		McpValidatedExperiment experiment,
		int checkpointIndex,
		IReadOnlyList<McpObservationResult?> observations,
		IReadOnlyList<McpAssertionResult> previousResults)
	{
		if(checkpointIndex < 0 || checkpointIndex >= experiment.Checkpoints.Count) {
			throw new ArgumentOutOfRangeException(nameof(checkpointIndex));
		}
		if(observations.Count != experiment.Observations.Count) {
			throw new ArgumentException("Observation result slots do not match the validated experiment.", nameof(observations));
		}

		List<McpAssertionResult> results = new();
		bool stop = false;
		foreach(int assertionIndex in experiment.Checkpoints[checkpointIndex].AssertionIndices) {
			McpValidatedAssertion assertion = experiment.Assertions[assertionIndex];
			McpObservationResult actual = observations[assertion.ObservationIndex]
				?? throw new ArgumentException($"Observation '{assertion.ObservationId}' has not been captured.", nameof(observations));
			McpObservationResult? reference = assertion.ReferenceObservationIndex.HasValue
				? observations[assertion.ReferenceObservationIndex.Value]
					?? throw new ArgumentException($"Reference observation '{assertion.ReferenceObservationId}' has not been captured.", nameof(observations))
				: null;
			McpAssertionResult result = EvaluateAssertion(assertion, actual, reference);
			results.Add(result);
			if(!result.Passed && experiment.FailFast) {
				stop = true;
				break;
			}
		}

		int passed = previousResults.Count(item => item.Passed) + results.Count(item => item.Passed);
		int failed = previousResults.Count(item => !item.Passed) + results.Count(item => !item.Passed);
		return new(
			Freeze(results.ToArray()),
			new(experiment.Assertions.Count, passed, failed, experiment.Assertions.Count - passed - failed),
			stop);
	}

	private static McpAssertionResult EvaluateAssertion(
		McpValidatedAssertion assertion,
		McpObservationResult actual,
		McpObservationResult? reference)
	{
		bool passed;
		string actualText;
		string expectedText;
		if(actual.DecodedValue.HasValue) {
			long comparison = assertion.ExpectedValue ?? reference?.DecodedValue ?? 0;
			passed = EvaluateScalar(
				assertion.Operator,
				actual.DecodedValue.Value,
				comparison,
				assertion.MinimumValue ?? 0,
				assertion.MaximumValue ?? 0,
				assertion.Mask ?? 0);
			actualText = actual.DecodedValue.Value.ToString(CultureInfo.InvariantCulture);
			expectedText = assertion.Operator switch {
				"range" => $"[{assertion.MinimumValue}, {assertion.MaximumValue}]",
				"masked_equal" => $"{assertion.ExpectedValue} masked by 0x{assertion.Mask:X}",
				_ when reference is not null => $"{assertion.Operator} {reference.DecodedValue}",
				_ => comparison.ToString(CultureInfo.InvariantCulture)
			};
		} else {
			ReadOnlySpan<int> comparison = assertion.ExpectedBytes is not null
				? assertion.ExpectedBytes.ToArray()
				: reference?.Data ?? throw new InvalidOperationException("A validated raw assertion has no comparison bytes.");
			passed = EvaluateRawInts(assertion.Operator, actual.Data, comparison);
			actualText = actual.Hex;
			expectedText = assertion.ExpectedBytes is not null
				? Convert.ToHexString(assertion.ExpectedBytes.Select(item => (byte)item).ToArray())
				: $"{assertion.Operator} {reference!.Hex}";
		}
		return new(assertion.Id, assertion.Checkpoint, assertion.ObservationId, assertion.Operator, passed, actualText, expectedText);
	}

	private static bool EvaluateRawInts(string operation, IReadOnlyList<int> actual, ReadOnlySpan<int> comparison)
	{
		bool equal = actual.Count == comparison.Length;
		for(int index = 0; equal && index < actual.Count; index++) {
			equal = actual[index] == comparison[index];
		}
		return operation switch {
			"equal" or "unchanged" => equal,
			"not_equal" or "changed" => !equal,
			_ => throw new ArgumentOutOfRangeException(nameof(operation))
		};
	}

	private static McpServiceError? ValidateOperands(
		McpAssertionRequest assertion,
		McpValidatedObservation observation,
		bool hasReference)
	{
		bool hasBytes = assertion.ExpectedBytes is not null;
		bool hasExpected = assertion.ExpectedValue.HasValue;
		bool hasMinimum = assertion.MinimumValue.HasValue;
		bool hasMaximum = assertion.MaximumValue.HasValue;
		bool hasMask = assertion.Mask.HasValue;
		bool hasAnyScalarRange = hasExpected || hasMinimum || hasMaximum || hasMask;

		if(observation.Decode is null) {
			if(RawValueOperators.Contains(assertion.Operator)) {
				if(!hasBytes || hasReference || hasAnyScalarRange
					|| assertion.ExpectedBytes!.Length != observation.Count
					|| assertion.ExpectedBytes.Any(item => item is < byte.MinValue or > byte.MaxValue)) {
					return new("invalid_request", $"Raw assertion '{assertion.Id}' has invalid operands.");
				}
				return null;
			}
			if(RawRelativeOperators.Contains(assertion.Operator) && hasReference && !hasBytes && !hasAnyScalarRange) {
				return null;
			}
			return new("invalid_request", $"Operator '{assertion.Operator}' is not valid for raw assertion '{assertion.Id}'.");
		}

		if(hasBytes) {
			return new("invalid_request", $"Decoded assertion '{assertion.Id}' cannot use expected bytes.");
		}
		(long minimum, long maximum) = ScalarRange(observation.Decode);
		bool expectedInRange = !hasExpected || assertion.ExpectedValue >= minimum && assertion.ExpectedValue <= maximum;
		bool minimumInRange = !hasMinimum || assertion.MinimumValue >= minimum && assertion.MinimumValue <= maximum;
		bool maximumInRange = !hasMaximum || assertion.MaximumValue >= minimum && assertion.MaximumValue <= maximum;
		ulong maxMask = (1UL << (observation.Decode.Width * 8)) - 1;
		bool maskInRange = !hasMask || assertion.Mask <= maxMask;

		bool valid = assertion.Operator switch {
			"equal" or "not_equal" => hasExpected && expectedInRange && !hasReference && !hasMinimum && !hasMaximum && !hasMask,
			"range" => !hasExpected && !hasReference && hasMinimum && hasMaximum && !hasMask
				&& minimumInRange && maximumInRange && assertion.MinimumValue <= assertion.MaximumValue,
			"masked_equal" => hasExpected && expectedInRange && !hasReference && !hasMinimum && !hasMaximum && hasMask && maskInRange,
			_ when RelativeOperators.Contains(assertion.Operator) => hasReference && !hasAnyScalarRange,
			_ => false
		};
		return valid ? null : new("invalid_request", $"Decoded assertion '{assertion.Id}' has an invalid operator or operands.");
	}

	private static (long Minimum, long Maximum) ScalarRange(McpDecodeRequest decode)
	{
		int bits = decode.Width * 8;
		if(!decode.Signed) {
			return (0, (long)((1UL << bits) - 1));
		}
		long maximum = (1L << (bits - 1)) - 1;
		return (-maximum - 1, maximum);
	}

	private static bool Compatible(McpValidatedObservation observation, McpValidatedObservation reference)
	{
		if(observation.Decode is null || reference.Decode is null) {
			return observation.Decode is null && reference.Decode is null && observation.Count == reference.Count;
		}
		return observation.Decode.Width == reference.Decode.Width
			&& observation.Decode.Signed == reference.Decode.Signed
			&& observation.Decode.ByteOrder == reference.Decode.ByteOrder;
	}

	private static bool IsValidRange(uint address, int count, int size)
	{
		if(count <= 0 || size < 0) {
			return false;
		}
		try {
			uint endExclusive = checked(address + checked((uint)count));
			return endExclusive <= (ulong)size;
		} catch(OverflowException) {
			return false;
		}
	}

	private static IReadOnlyList<McpControllerInput> CopyControllers(IReadOnlyList<McpControllerInput> controllers)
	{
		McpControllerInput[] copy = new McpControllerInput[controllers.Count];
		for(int index = 0; index < controllers.Count; index++) {
			McpControllerInput? controller = controllers[index];
			if(controller is null || controller.Buttons is null) {
				throw new ArgumentException("Controller and button collections are required.", nameof(controllers));
			}
			copy[index] = new(controller.Port, Freeze(controller.Buttons.ToArray()));
		}
		return Freeze(copy);
	}

	private static ReadOnlyCollection<T> Freeze<T>(T[] values) => Array.AsReadOnly(values);

	private static McpServiceResult<McpValidatedExperiment> Invalid(string message) =>
		McpServiceResult<McpValidatedExperiment>.Failure("invalid_request", message);

	private static McpServiceResult<McpValidatedExperiment> InvalidCheckpoint(string message) =>
		McpServiceResult<McpValidatedExperiment>.Failure("invalid_checkpoint", message);

	private static McpServiceResult<McpValidatedExperiment> RangeFailure(string message) =>
		McpServiceResult<McpValidatedExperiment>.Failure("invalid_range", message);

	private static McpServiceResult<McpValidatedExperiment> Limit(string message) =>
		McpServiceResult<McpValidatedExperiment>.Failure("resource_limit", message);
}
