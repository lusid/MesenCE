using System;

namespace Mesen.Mcp;

internal sealed class McpMemorySnapshotStore : McpResourceStore<McpMemorySnapshotResource>
{
	internal McpMemorySnapshotStore(IMcpMonotonicClock? clock = null)
		: base(
			clock ?? McpMonotonicClock.Instance,
			McpAutomationLimits.MaxMemorySnapshots,
			McpAutomationLimits.MaxMemorySnapshotBytes,
			McpAutomationLimits.MaxAggregateMemorySnapshotBytes,
			resource => [resource.Data]) { }

	internal McpServiceResult<McpMemorySnapshotMetadata> Create(
		string system,
		string space,
		uint address,
		byte[] data,
		McpStateIdentity identity,
		DateTimeOffset createdAt)
	{
		McpServiceResult<McpResourceCreation<McpMemorySnapshotResource>> result = AddResource(id =>
			new(
				new(id, system, space, address, data.Length, identity.RomIdentity, identity.MutableStateGeneration, createdAt),
				identity,
				data));
		return result.IsSuccess
			? McpServiceResult<McpMemorySnapshotMetadata>.Success(result.Value!.Value.Metadata)
			: ForwardFailure<McpMemorySnapshotMetadata, McpResourceCreation<McpMemorySnapshotResource>>(result);
	}

	internal McpServiceResult<McpPinnedResource<McpMemorySnapshotResource>> Pin(string id) => PinResource(id);

	internal McpServiceResult<McpDeleteResourceResult> Delete(string id) => DeleteResource(id);

	internal void InvalidateRom(long currentRomIdentity) =>
		InvalidateResources(resource => resource.Identity.RomIdentity != currentRomIdentity);

	internal void InvalidateTopology(Predicate<McpMemorySnapshotResource> isInvalid) => InvalidateResources(isInvalid);
}
