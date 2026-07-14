using System;
using System.Collections.Generic;
using System.Threading;

namespace Mesen.Mcp;

internal sealed class McpPinnedResource<T> : IDisposable where T : class
{
	private Action? _release;

	internal McpPinnedResource(T value, Action release)
	{
		Value = value;
		_release = release;
	}

	internal T Value { get; }

	public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}

internal sealed record McpSaveStateResource(McpSaveStateMetadata Metadata, McpStateIdentity Identity, byte[] Data);

internal sealed record McpMemorySnapshotResource(McpMemorySnapshotMetadata Metadata, McpStateIdentity Identity, byte[] Data);

internal sealed record McpMemorySearchState(
	byte[] PreviousSnapshot,
	byte[] CurrentSnapshot,
	int[] CandidateOffsets,
	long PreviousMutableStateGeneration,
	long MutableStateGeneration);

internal sealed record McpMemorySearchResource(
	string System,
	string Space,
	uint Address,
	int Count,
	int Width,
	bool Signed,
	string ByteOrder,
	int Stride,
	McpStateIdentity Identity,
	McpMemorySearchState State,
	McpMemorySearchState? UndoState = null)
{
	internal long AllocationBytes
	{
		get
		{
			long bytes = 0;
			foreach(Array array in GetRetainedArrays()) {
				bytes += Buffer.ByteLength(array);
			}
			return bytes;
		}
	}

	internal Array[] GetRetainedArrays()
	{
		HashSet<Array> arrays = new(ReferenceEqualityComparer.Instance) {
			State.PreviousSnapshot,
			State.CurrentSnapshot,
			State.CandidateOffsets
		};
		if(UndoState is not null) {
			arrays.Add(UndoState.PreviousSnapshot);
			arrays.Add(UndoState.CurrentSnapshot);
			arrays.Add(UndoState.CandidateOffsets);
		}
		return [.. arrays];
	}

	internal McpMemorySearchResource CreateOwnedCopy()
	{
		Dictionary<Array, Array> copies = new(ReferenceEqualityComparer.Instance);

		byte[] CopyBytes(byte[] source)
		{
			if(!copies.TryGetValue(source, out Array? copy)) {
				copy = (byte[])source.Clone();
				copies.Add(source, copy);
			}
			return (byte[])copy;
		}

		int[] CopyOffsets(int[] source)
		{
			if(!copies.TryGetValue(source, out Array? copy)) {
				copy = (int[])source.Clone();
				copies.Add(source, copy);
			}
			return (int[])copy;
		}

		McpMemorySearchState CopyState(McpMemorySearchState state) => new(
			CopyBytes(state.PreviousSnapshot),
			CopyBytes(state.CurrentSnapshot),
			CopyOffsets(state.CandidateOffsets),
			state.PreviousMutableStateGeneration,
			state.MutableStateGeneration);

		return this with {
			State = CopyState(State),
			UndoState = UndoState is null ? null : CopyState(UndoState)
		};
	}
}

internal abstract class McpResourceStore<T> : IDisposable where T : class
{
	private readonly object _sync = new();
	private readonly IMcpMonotonicClock _clock;
	private readonly int _maxCount;
	private readonly long _maxItemBytes;
	private readonly long _maxAggregateBytes;
	private readonly Func<T, Array[]> _getAllocations;
	private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
	private readonly Dictionary<Array, int> _allocationReferences = new(ReferenceEqualityComparer.Instance);
	private long _retainedBytes;
	private int _retainedCount;
	private bool _disposed;

	protected McpResourceStore(
		IMcpMonotonicClock clock,
		int maxCount,
		long maxItemBytes,
		long maxAggregateBytes,
		Func<T, Array[]> getAllocations)
	{
		_clock = clock;
		_maxCount = maxCount;
		_maxItemBytes = maxItemBytes;
		_maxAggregateBytes = maxAggregateBytes;
		_getAllocations = getAllocations;
	}

	internal long RetainedBytes
	{
		get { lock(_sync) { return _retainedBytes; } }
	}

	protected McpServiceResult<bool> CheckAddResource(long size)
	{
		lock(_sync) {
			ThrowIfDisposed();
			Sweep(_clock.GetTimestamp());
			return _retainedCount >= _maxCount || size > _maxItemBytes || size > _maxAggregateBytes - _retainedBytes
				? LimitFailure<bool>()
				: McpServiceResult<bool>.Success(true);
		}
	}

	protected McpServiceResult<McpResourceCreation<T>> AddResource(Func<string, T> create)
	{
		lock(_sync) {
			ThrowIfDisposed();
			long now = _clock.GetTimestamp();
			Sweep(now);
			if(_retainedCount >= _maxCount) {
				return LimitFailure<McpResourceCreation<T>>();
			}

			string id = CreateId();
			T value = create(id);
			Array[] allocations = GetDistinctAllocations(value);
			long size = GetSize(allocations);
			long addedBytes = GetAddedBytes(allocations, null);
			if(size > _maxItemBytes || addedBytes > _maxAggregateBytes - _retainedBytes) {
				return LimitFailure<McpResourceCreation<T>>();
			}

			Entry entry = new(id, now);
			entry.Current = AddVersion(entry, value, allocations);
			_entries.Add(id, entry);
			_retainedCount++;
			return McpServiceResult<McpResourceCreation<T>>.Success(new(id, value));
		}
	}

	protected McpServiceResult<McpPinnedResource<T>> PinResource(string id)
	{
		lock(_sync) {
			ThrowIfDisposed();
			long now = _clock.GetTimestamp();
			Sweep(now);
			if(!_entries.TryGetValue(id, out Entry? entry)) {
				return NotFound<McpPinnedResource<T>>(id);
			}
			if(entry.IsStale || entry.Current is null) {
				return Stale<McpPinnedResource<T>>(id);
			}

			Version version = entry.Current;
			version.PinCount++;
			entry.LastUsed = now;
			return McpServiceResult<McpPinnedResource<T>>.Success(
				new(version.Value, () => Release(entry, version)));
		}
	}

	protected McpServiceResult<T> InspectResource(string id)
	{
		lock(_sync) {
			ThrowIfDisposed();
			long now = _clock.GetTimestamp();
			Sweep(now);
			if(!_entries.TryGetValue(id, out Entry? entry)) {
				return NotFound<T>(id);
			}
			if(entry.IsStale || entry.Current is null) {
				return Stale<T>(id);
			}

			entry.LastUsed = now;
			return McpServiceResult<T>.Success(entry.Current.Value);
		}
	}

	protected McpServiceResult<McpDeleteResourceResult> DeleteResource(string id)
	{
		lock(_sync) {
			ThrowIfDisposed();
			Sweep(_clock.GetTimestamp());
			if(!_entries.Remove(id, out Entry? entry)) {
				return NotFound<McpDeleteResourceResult>(id);
			}

			RemoveEntry(entry);
			return McpServiceResult<McpDeleteResourceResult>.Success(new(id, true));
		}
	}

	protected McpServiceResult<bool> ReplaceResource(string id, T replacement)
	{
		lock(_sync) {
			ThrowIfDisposed();
			long now = _clock.GetTimestamp();
			Sweep(now);
			if(!_entries.TryGetValue(id, out Entry? entry)) {
				return NotFound<bool>(id);
			}
			if(entry.IsStale || entry.Current is null) {
				return Stale<bool>(id);
			}

			Version current = entry.Current;
			Array[] allocations = GetDistinctAllocations(replacement);
			long replacementSize = GetSize(allocations);
			Array[]? releasedAllocations = current.PinCount == 0 ? current.Allocations : null;
			long releasedBytes = GetReleasedBytes(releasedAllocations);
			long addedBytes = GetAddedBytes(allocations, releasedAllocations);
			if(replacementSize > _maxItemBytes ||
				addedBytes > _maxAggregateBytes - (_retainedBytes - releasedBytes)) {
				return LimitFailure<bool>();
			}

			current.Retired = true;
			if(current.PinCount == 0) {
				CleanupVersion(entry, current);
			}
			entry.Current = AddVersion(entry, replacement, allocations);
			entry.LastUsed = now;
			return McpServiceResult<bool>.Success(true);
		}
	}

	protected void InvalidateResources(Predicate<T> shouldInvalidate)
	{
		lock(_sync) {
			ThrowIfDisposed();
			long now = _clock.GetTimestamp();
			Sweep(now);
			foreach(Entry entry in _entries.Values) {
				if(entry.IsStale || entry.Current is null || !shouldInvalidate(entry.Current.Value)) {
					continue;
				}

				entry.IsStale = true;
				entry.StaleSince = now;
				entry.Removed = true;
				Version current = entry.Current;
				entry.Current = null;
				current.Retired = true;
				if(current.PinCount == 0) {
					CleanupVersion(entry, current);
				}
			}
		}
	}

	public void Dispose()
	{
		lock(_sync) {
			if(_disposed) {
				return;
			}
			_disposed = true;
			foreach(Entry entry in _entries.Values) {
				RemoveEntry(entry);
			}
			_entries.Clear();
		}
	}

	private Version AddVersion(Entry entry, T value, Array[] allocations)
	{
		Version version = new(value, allocations);
		entry.RetainedVersions++;
		foreach(Array allocation in allocations) {
			if(_allocationReferences.TryGetValue(allocation, out int references)) {
				_allocationReferences[allocation] = references + 1;
			} else {
				_allocationReferences.Add(allocation, 1);
				_retainedBytes += Buffer.ByteLength(allocation);
			}
		}
		return version;
	}

	private void Release(Entry entry, Version version)
	{
		lock(_sync) {
			version.PinCount--;
			if(version.PinCount != 0) {
				return;
			}
			if(version.Retired) {
				CleanupVersion(entry, version);
				return;
			}

			long now = _clock.GetTimestamp();
			if(_clock.GetElapsedTime(entry.LastUsed, now) >= McpAutomationLimits.ResourceIdleExpiration &&
				_entries.Remove(entry.Id)) {
				RemoveEntry(entry);
			}
		}
	}

	private void Sweep(long now)
	{
		List<Entry>? expired = null;
		foreach(Entry entry in _entries.Values) {
			bool staleExpired = entry.IsStale &&
				_clock.GetElapsedTime(entry.StaleSince, now) >= McpAutomationLimits.ResourceIdleExpiration;
			bool resourceExpired = !entry.IsStale && entry.Current is { PinCount: 0 } &&
				_clock.GetElapsedTime(entry.LastUsed, now) >= McpAutomationLimits.ResourceIdleExpiration;
			if(staleExpired || resourceExpired) {
				(expired ??= []).Add(entry);
			}
		}

		if(expired is null) {
			return;
		}
		foreach(Entry entry in expired) {
			_entries.Remove(entry.Id);
			if(!entry.IsStale) {
				RemoveEntry(entry);
			}
		}
	}

	private void RemoveEntry(Entry entry)
	{
		entry.Removed = true;
		if(entry.Current is not null) {
			Version current = entry.Current;
			entry.Current = null;
			current.Retired = true;
			if(current.PinCount == 0) {
				CleanupVersion(entry, current);
			}
		}
		if(entry.RetainedVersions == 0) {
			ReleaseCount(entry);
		}
	}

	private void CleanupVersion(Entry entry, Version version)
	{
		if(version.Cleaned) {
			return;
		}
		version.Cleaned = true;
		foreach(Array allocation in version.Allocations) {
			int references = _allocationReferences[allocation] - 1;
			if(references == 0) {
				_allocationReferences.Remove(allocation);
				_retainedBytes -= Buffer.ByteLength(allocation);
			} else {
				_allocationReferences[allocation] = references;
			}
		}
		entry.RetainedVersions--;
		if(entry.Removed && entry.RetainedVersions == 0) {
			ReleaseCount(entry);
		}
	}

	private void ReleaseCount(Entry entry)
	{
		if(!entry.Counted) {
			return;
		}
		entry.Counted = false;
		_retainedCount--;
	}

	private string CreateId()
	{
		string id;
		do {
			id = Guid.NewGuid().ToString("N");
		} while(_entries.ContainsKey(id));
		return id;
	}

	private Array[] GetDistinctAllocations(T value)
	{
		HashSet<Array> allocations = new(_getAllocations(value), ReferenceEqualityComparer.Instance);
		return [.. allocations];
	}

	private static long GetSize(Array[] allocations)
	{
		long size = 0;
		foreach(Array allocation in allocations) {
			size += Buffer.ByteLength(allocation);
		}
		return size;
	}

	private long GetAddedBytes(Array[] allocations, Array[]? releasedAllocations)
	{
		HashSet<Array>? released = releasedAllocations is null
			? null
			: new(releasedAllocations, ReferenceEqualityComparer.Instance);
		long bytes = 0;
		foreach(Array allocation in allocations) {
			int references = _allocationReferences.GetValueOrDefault(allocation);
			if(released?.Contains(allocation) == true) {
				references--;
			}
			if(references == 0) {
				bytes += Buffer.ByteLength(allocation);
			}
		}
		return bytes;
	}

	private long GetReleasedBytes(Array[]? allocations)
	{
		if(allocations is null) {
			return 0;
		}
		long bytes = 0;
		foreach(Array allocation in allocations) {
			if(_allocationReferences[allocation] == 1) {
				bytes += Buffer.ByteLength(allocation);
			}
		}
		return bytes;
	}

	private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

	private static McpServiceResult<TResult> LimitFailure<TResult>() =>
		McpServiceResult<TResult>.Failure("resource_limit", "The resource quota would be exceeded.");

	private static McpServiceResult<TResult> NotFound<TResult>(string id) =>
		McpServiceResult<TResult>.Failure("resource_not_found", $"Resource '{id}' was not found.");

	private static McpServiceResult<TResult> Stale<TResult>(string id) =>
		McpServiceResult<TResult>.Failure("stale_resource", $"Resource '{id}' is no longer compatible with the active ROM or memory topology.");

	protected static McpServiceResult<TResult> ForwardFailure<TResult, TSource>(McpServiceResult<TSource> source) =>
		McpServiceResult<TResult>.Failure(source.Error!.Code, source.Error.Message);

	private sealed class Entry
	{
		internal Entry(string id, long lastUsed)
		{
			Id = id;
			LastUsed = lastUsed;
		}

		internal string Id { get; }
		internal long LastUsed { get; set; }
		internal long StaleSince { get; set; }
		internal Version? Current { get; set; }
		internal int RetainedVersions { get; set; }
		internal bool IsStale { get; set; }
		internal bool Removed { get; set; }
		internal bool Counted { get; set; } = true;
	}

	private sealed class Version
	{
		internal Version(T value, Array[] allocations)
		{
			Value = value;
			Allocations = allocations;
		}

		internal T Value { get; }
		internal Array[] Allocations { get; }
		internal int PinCount { get; set; }
		internal bool Retired { get; set; }
		internal bool Cleaned { get; set; }
	}
}

internal sealed record McpResourceCreation<T>(string Id, T Value) where T : class;
