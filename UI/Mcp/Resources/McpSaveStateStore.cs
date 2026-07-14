using System;

namespace Mesen.Mcp;

internal sealed class McpSaveStateStore : McpResourceStore<McpSaveStateResource>
{
	internal McpSaveStateStore(IMcpMonotonicClock? clock = null)
		: base(
			clock ?? McpMonotonicClock.Instance,
			McpAutomationLimits.MaxSaveStates,
			McpAutomationLimits.MaxSaveStateBytes,
			McpAutomationLimits.MaxAggregateSaveStateBytes,
			resource => [resource.Data]) { }

	internal McpServiceResult<McpSaveStateMetadata> Create(byte[] data, McpStateIdentity identity, DateTimeOffset createdAt)
	{
		byte[] ownedData = (byte[])data.Clone();
		McpServiceResult<McpResourceCreation<McpSaveStateResource>> result = AddResource(id =>
			new(new(id, ownedData.Length, identity.RomIdentity, identity.MutableStateGeneration, createdAt), identity, ownedData));
		return result.IsSuccess
			? McpServiceResult<McpSaveStateMetadata>.Success(result.Value!.Value.Metadata)
			: ForwardFailure<McpSaveStateMetadata, McpResourceCreation<McpSaveStateResource>>(result);
	}

	internal McpServiceResult<McpPinnedResource<McpSaveStateResource>> Pin(string id) => PinResource(id);
	internal McpServiceResult<McpSaveStateResource> Inspect(string id) => InspectResource(id);

	internal McpServiceResult<McpDeleteResourceResult> Delete(string id) => DeleteResource(id);

	internal void InvalidateRom(long currentRomIdentity) =>
		InvalidateResources(resource => resource.Identity.RomIdentity != currentRomIdentity);
}
