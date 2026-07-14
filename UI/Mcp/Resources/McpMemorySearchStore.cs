using System;

namespace Mesen.Mcp;

internal sealed class McpMemorySearchStore : McpResourceStore<McpMemorySearchResource>
{
	internal McpMemorySearchStore(IMcpMonotonicClock? clock = null)
		: base(
			clock ?? McpMonotonicClock.Instance,
			McpAutomationLimits.MaxMemorySearches,
			McpAutomationLimits.MaxSearchAllocationBytes,
			McpAutomationLimits.MaxAggregateSearchAllocationBytes,
			resource => resource.GetRetainedArrays()) { }

	internal McpServiceResult<string> Create(McpMemorySearchResource resource)
	{
		if(resource.Count < 0 || resource.Count > McpAutomationLimits.MaxSearchRangeBytes) {
			return McpServiceResult<string>.Failure("resource_limit", "The memory search range quota would be exceeded.");
		}

		McpServiceResult<McpResourceCreation<McpMemorySearchResource>> result = AddResource(_ => resource);
		return result.IsSuccess
			? McpServiceResult<string>.Success(result.Value!.Id)
			: ForwardFailure<string, McpResourceCreation<McpMemorySearchResource>>(result);
	}

	internal McpServiceResult<McpPinnedResource<McpMemorySearchResource>> Pin(string id) => PinResource(id);

	internal McpServiceResult<bool> Replace(string id, McpMemorySearchResource replacement)
	{
		if(replacement.Count < 0 || replacement.Count > McpAutomationLimits.MaxSearchRangeBytes) {
			return McpServiceResult<bool>.Failure("resource_limit", "The memory search range quota would be exceeded.");
		}
		return ReplaceResource(id, replacement);
	}

	internal McpServiceResult<McpDeleteResourceResult> Delete(string id) => DeleteResource(id);

	internal void InvalidateRom(long currentRomIdentity) =>
		InvalidateResources(resource => resource.Identity.RomIdentity != currentRomIdentity);

	internal void InvalidateTopology(Predicate<McpMemorySearchResource> isInvalid) => InvalidateResources(isInvalid);
}
