using System;
using System.Collections.Generic;

namespace Mesen.Mcp;

public sealed record CreateMemorySnapshotRequest(string Space, uint Address, int Count);
public sealed record CompareMemorySnapshotsRequest(string FirstId, string SecondId, int Offset, int Limit, int SampleBytes);
public sealed record DeleteMemorySnapshotRequest(string Id);
public sealed record StartMemorySearchRequest(string Space, uint Address, int Count, int Width, bool Signed, string ByteOrder, int Stride, long? InitialValue);
public sealed record RefineMemorySearchRequest(string Id, string Comparison, long? Value, long? Delta);
public sealed record GetMemorySearchResultsRequest(string Id, int Offset, int Limit);
public sealed record UndoMemorySearchRequest(string Id);
public sealed record DeleteMemorySearchRequest(string Id);
public sealed record McpMemorySnapshotMetadata(string Id, string System, string Space, uint Address, int Count, long RomIdentity, long MutableStateGeneration, DateTimeOffset CreatedAt);
public sealed record CreateMemorySnapshotResult(McpMemorySnapshotMetadata Snapshot);
public sealed record McpChangedMemoryRun(uint Address, int Length, int[] Before, int[] After, bool SampleTruncated);
public sealed record CompareMemorySnapshotsResult(string FirstId, string SecondId, long FirstMutableStateGeneration, long SecondMutableStateGeneration, int ChangedBytes, int ChangedRuns, int Offset, int? NextOffset, IReadOnlyList<McpChangedMemoryRun> Runs);
public sealed record StartMemorySearchResult(string Id, string System, string Space, uint Address, int Count, int Width, bool Signed, string ByteOrder, int Stride, int CandidateCount, long RomIdentity, long MutableStateGeneration);
public sealed record RefineMemorySearchResult(string Id, string Comparison, int PreviousCandidateCount, int CandidateCount, long PreviousMutableStateGeneration, long MutableStateGeneration);
public sealed record McpMemorySearchCandidate(uint Address, long PreviousValue, long CurrentValue);
public sealed record GetMemorySearchResultsResult(string Id, int CandidateCount, int Offset, int? NextOffset, long PreviousMutableStateGeneration, long MutableStateGeneration, IReadOnlyList<McpMemorySearchCandidate> Candidates);
public sealed record UndoMemorySearchResult(string Id, int CandidateCount, long PreviousMutableStateGeneration, long MutableStateGeneration);
