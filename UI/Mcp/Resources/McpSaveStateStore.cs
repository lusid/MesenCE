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
		McpServiceResult<McpResourceCreation<McpSaveStateResource>> result = AddResource(id =>
			new(new(id, data.Length, identity.RomIdentity, identity.MutableStateGeneration, createdAt), identity, data));
		return result.IsSuccess
			? McpServiceResult<McpSaveStateMetadata>.Success(result.Value!.Value.Metadata)
			: ForwardFailure<McpSaveStateMetadata, McpResourceCreation<McpSaveStateResource>>(result);
	}

	internal McpServiceResult<McpPinnedResource<McpSaveStateResource>> Pin(string id) => PinResource(id);

	internal McpServiceResult<McpDeleteResourceResult> Delete(string id) => DeleteResource(id);

	internal void InvalidateRom(long currentRomIdentity) =>
		InvalidateResources(resource => resource.Identity.RomIdentity != currentRomIdentity);
}
