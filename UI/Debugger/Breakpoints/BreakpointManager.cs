using Mesen.Debugger.Utilities;
using Mesen.Interop;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Mesen.Debugger
{
	public static class BreakpointManager
	{
		public static event EventHandler? BreakpointsChanged;

		private static readonly object _sync = new();
		private static List<Breakpoint> _breakpoints = new List<Breakpoint>();
		private static List<Breakpoint> _asserts = new List<Breakpoint>();
		private static List<Breakpoint> _temporaryBreakpoints = new List<Breakpoint>();
		private static HashSet<CpuType> _activeCpuTypes = new HashSet<CpuType>();
		private static readonly Dictionary<long, List<ExternalBreakpoint>> _externalBreakpoints = [];
		private static readonly Dictionary<int, Breakpoint> _uiByNativeId = [];
		private static readonly Dictionary<int, (long CollectionId, long StableId)> _externalByNativeId = [];
		private static long _nextExternalCollectionId;
		private static Action<InteropBreakpoint[]> _nativeBreakpointSetter = SetNativeBreakpoints;

		public static ReadOnlyCollection<Breakpoint> Breakpoints
		{
			get
			{
				lock(_sync) {
					return _breakpoints.ToList().AsReadOnly();
				}
			}
		}

		public static List<Breakpoint> Asserts
		{
			internal get
			{
				lock(_sync) {
					return _asserts.ToList();
				}
			}
			set
			{
				lock(_sync) {
					_asserts = value.ToList();
				}
			}
		}

		public readonly record struct ExternalBreakpoint(long StableId, Breakpoint Breakpoint);

		public sealed class ExternalBreakpointCollection : IDisposable
		{
			private readonly long _collectionId;
			private bool _disposed;

			internal ExternalBreakpointCollection(long collectionId) => _collectionId = collectionId;
			public void Replace(IReadOnlyList<ExternalBreakpoint> breakpoints) =>
				BreakpointManager.ReplaceExternal(_collectionId, breakpoints, ref _disposed);
			public bool TryGetStableId(int nativeBreakpointId, out long stableId) =>
				BreakpointManager.TryGetExternalStableId(_collectionId, nativeBreakpointId, out stableId);
			public void Dispose() => BreakpointManager.RemoveExternal(_collectionId, ref _disposed);
			internal void Detach() => BreakpointManager.DetachExternal(_collectionId, ref _disposed);
		}

		public static ExternalBreakpointCollection CreateExternalBreakpointCollection()
		{
			lock(_sync) {
				long id = ++_nextExternalCollectionId;
				_externalBreakpoints.Add(id, []);
				return new ExternalBreakpointCollection(id);
			}
		}

		public static List<Breakpoint> GetBreakpoints(CpuType cpuType)
		{
			lock(_sync) {
				List<Breakpoint> breakpoints = new List<Breakpoint>();
				foreach(Breakpoint bp in _breakpoints) {
					if(bp.CpuType == cpuType) {
						breakpoints.Add(bp);
					}
				}
				return breakpoints;
			}
		}

		public static void AddCpuType(CpuType cpuType)
		{
			lock(_sync) {
				_activeCpuTypes.Add(cpuType);
			}
			SetBreakpoints();
		}

		public static void RemoveCpuType(CpuType cpuType)
		{
			lock(_sync) {
				_activeCpuTypes.Remove(cpuType);
			}
			SetBreakpoints();
		}

		public static void RefreshBreakpoints(Breakpoint? bp = null)
		{
			BreakpointsChanged?.Invoke(bp, EventArgs.Empty);
			SetBreakpoints();
		}

		public static void ClearBreakpoints()
		{
			lock(_sync) {
				_breakpoints = new();
			}
			RefreshBreakpoints();
		}

		public static void AddBreakpoints(List<Breakpoint> breakpoints)
		{
			lock(_sync) {
				_breakpoints.AddRange(breakpoints);
			}
			RefreshBreakpoints();
		}

		public static void RemoveBreakpoint(Breakpoint bp)
		{
			bool removed;
			lock(_sync) {
				removed = _breakpoints.Remove(bp);
			}
			if(removed) {
				DebugWorkspaceManager.AutoSave();
			}
			RefreshBreakpoints(bp);
		}

		public static void RemoveBreakpoints(IEnumerable<Breakpoint> breakpoints)
		{
			lock(_sync) {
				foreach(Breakpoint bp in breakpoints) {
					_breakpoints.Remove(bp);
				}
			}
			RefreshBreakpoints(null);
		}

		public static void AddBreakpoint(Breakpoint bp)
		{
			bool added = false;
			lock(_sync) {
				if(!_breakpoints.Contains(bp)) {
					_breakpoints.Add(bp);
					added = true;
				}
			}
			if(added) {
				DebugWorkspaceManager.AutoSave();
			}
			RefreshBreakpoints(bp);
		}

		public static void AddBreakpoint(AddressInfo addr, CpuType cpuType)
		{
			if(BreakpointManager.GetMatchingBreakpoint(addr, cpuType) == null) {
				Breakpoint bp = new Breakpoint() {
					StartAddress = (uint)addr.Address,
					EndAddress = (uint)addr.Address,
					MemoryType = addr.Type,
					CpuType = cpuType,
					BreakOnExec = true,
					BreakOnWrite = true,
					BreakOnRead = true
				};

				BreakpointManager.AddBreakpoint(bp);
			}
		}

		public static void AddTemporaryBreakpoint(Breakpoint bp)
		{
			lock(_sync) {
				_temporaryBreakpoints.Add(bp);
			}
			SetBreakpoints();
		}

		public static void ClearTemporaryBreakpoints()
		{
			bool cleared = false;
			lock(_sync) {
				if(_temporaryBreakpoints.Count > 0) {
					_temporaryBreakpoints.Clear();
					cleared = true;
				}
			}
			if(cleared) {
				SetBreakpoints();
			}
		}

		private static Breakpoint? GetMatchingBreakpoint(AddressInfo info, CpuType cpuType, Func<Breakpoint, bool> predicate)
		{
			Breakpoint? bp = Breakpoints.Where((bp) => predicate(bp) && bp.Matches((UInt32)info.Address, info.Type, cpuType)).FirstOrDefault();

			if(bp == null) {
				AddressInfo altAddr;
				if(info.Type.IsRelativeMemory()) {
					altAddr = DebugApi.GetAbsoluteAddress(info);
				} else {
					altAddr = DebugApi.GetRelativeAddress(info, cpuType);
				}

				if(altAddr.Address >= 0) {
					bp = Breakpoints.Where((bp) => predicate(bp) && bp.Matches((UInt32)altAddr.Address, altAddr.Type, cpuType)).FirstOrDefault();
				}
			}

			return bp;
		}

		public static Breakpoint? GetMatchingBreakpoint(AddressInfo info, CpuType cpuType, bool ignoreRangedRwBp = false)
		{
			return GetMatchingBreakpoint(info, cpuType, (bp) => !ignoreRangedRwBp || bp.IsSingleAddress || bp.BreakOnExec);
		}

		public static Breakpoint? GetMatchingForbidBreakpoint(AddressInfo info, CpuType cpuType)
		{
			return GetMatchingBreakpoint(info, cpuType, (bp) => bp.Forbid);
		}

		public static Breakpoint? GetMatchingBreakpoint(UInt32 startAddress, UInt32 endAddress, MemoryType memoryType)
		{
			return Breakpoints.Where((bp) =>
					bp.MemoryType == memoryType &&
					bp.StartAddress == startAddress && bp.EndAddress == endAddress
				).FirstOrDefault();
		}

		public static bool EnableDisableBreakpoint(AddressInfo info, CpuType cpuType)
		{
			Breakpoint? breakpoint = BreakpointManager.GetMatchingBreakpoint(info, cpuType);
			if(breakpoint != null) {
				breakpoint.Enabled = !breakpoint.Enabled;
				DebugWorkspaceManager.AutoSave();
				RefreshBreakpoints();
				return true;
			}
			return false;
		}

		public static void ToggleBreakpoint(AddressInfo info, CpuType cpuType)
		{
			if(info.Address < 0) {
				return;
			}

			Breakpoint? breakpoint = BreakpointManager.GetMatchingForbidBreakpoint(info, cpuType) ?? BreakpointManager.GetMatchingBreakpoint(info, cpuType, true);
			if(breakpoint != null) {
				BreakpointManager.RemoveBreakpoint(breakpoint);
			} else {
				bool execBreakpoint = true;
				bool readWriteBreakpoint = !info.Type.IsRomMemory() || info.Type.IsRelativeMemory();
				if(info.Type.SupportsCdl()) {
					CdlFlags cdlData = DebugApi.GetCdlData((uint)info.Address, 1, info.Type)[0];
					bool isCode = cdlData.HasFlag(CdlFlags.Code);
					bool isData = cdlData.HasFlag(CdlFlags.Data);
					if(isCode || isData) {
						readWriteBreakpoint = !isCode;
						execBreakpoint = isCode;
					}
				}

				breakpoint = new Breakpoint() {
					CpuType = cpuType,
					Enabled = true,
					BreakOnExec = execBreakpoint,
					BreakOnRead = readWriteBreakpoint,
					BreakOnWrite = readWriteBreakpoint,
					StartAddress = (UInt32)info.Address,
					EndAddress = (UInt32)info.Address
				};

				breakpoint.MemoryType = info.Type;
				BreakpointManager.AddBreakpoint(breakpoint);
			}
		}

		public static void ToggleForbidBreakpoint(AddressInfo addr, CpuType cpuType)
		{
			if(addr.Address < 0) {
				return;
			}

			Breakpoint? breakpoint = GetMatchingForbidBreakpoint(addr, cpuType);
			if(breakpoint != null) {
				BreakpointManager.RemoveBreakpoint(breakpoint);
			} else {
				breakpoint = new Breakpoint() {
					CpuType = cpuType,
					Enabled = true,
					Forbid = true,
					StartAddress = (UInt32)addr.Address,
					EndAddress = (UInt32)addr.Address
				};
				breakpoint.MemoryType = addr.Type;
				BreakpointManager.AddBreakpoint(breakpoint);
			}
		}

		public static void SetBreakpoints()
		{
			lock(_sync) {
				SetBreakpointsLocked();
			}
		}

		private static void SetBreakpointsLocked()
		{
			List<InteropBreakpoint> breakpoints = new List<InteropBreakpoint>();
			_uiByNativeId.Clear();
			_externalByNativeId.Clear();

			int nativeId = 0;
			void addUiBreakpoints(IEnumerable<Breakpoint> bpList)
			{
				foreach(Breakpoint bp in bpList) {
					if(_activeCpuTypes.Contains(bp.CpuType)) {
						breakpoints.Add(bp.ToInteropBreakpoint(nativeId));
						_uiByNativeId.Add(nativeId, bp);
						nativeId++;
					}
				}
			}

			addUiBreakpoints(_breakpoints);
			addUiBreakpoints(_asserts);
			addUiBreakpoints(_temporaryBreakpoints);

			foreach(KeyValuePair<long, List<ExternalBreakpoint>> collection in _externalBreakpoints) {
				foreach(ExternalBreakpoint external in collection.Value) {
					breakpoints.Add(external.Breakpoint.ToInteropBreakpoint(nativeId));
					_externalByNativeId.Add(nativeId, (collection.Key, external.StableId));
					nativeId++;
				}
			}

			_nativeBreakpointSetter(breakpoints.ToArray());
		}

		public static Breakpoint? GetBreakpointById(int breakpointId)
		{
			lock(_sync) {
				return _uiByNativeId.GetValueOrDefault(breakpointId);
			}
		}

		private static void ReplaceExternal(long collectionId, IReadOnlyList<ExternalBreakpoint> breakpoints, ref bool disposed)
		{
			lock(_sync) {
				ObjectDisposedException.ThrowIf(disposed, typeof(ExternalBreakpointCollection));
				_externalBreakpoints[collectionId] = breakpoints.ToList();
				SetBreakpointsLocked();
			}
		}

		private static bool TryGetExternalStableId(long collectionId, int nativeBreakpointId, out long stableId)
		{
			lock(_sync) {
				if(_externalByNativeId.TryGetValue(nativeBreakpointId, out (long CollectionId, long StableId) external) && external.CollectionId == collectionId) {
					stableId = external.StableId;
					return true;
				}
				stableId = 0;
				return false;
			}
		}

		private static void RemoveExternal(long collectionId, ref bool disposed)
		{
			lock(_sync) {
				if(disposed) {
					return;
				}
				disposed = true;
				if(_externalBreakpoints.Remove(collectionId)) {
					SetBreakpointsLocked();
				}
			}
		}

		private static void DetachExternal(long collectionId, ref bool disposed)
		{
			lock(_sync) {
				if(disposed) {
					return;
				}
				disposed = true;
				_externalBreakpoints.Remove(collectionId);
				foreach(int nativeId in _externalByNativeId
					.Where(entry => entry.Value.CollectionId == collectionId)
					.Select(entry => entry.Key)
					.ToArray()) {
					_externalByNativeId.Remove(nativeId);
				}
			}
		}

		internal static IDisposable OverrideNativeBreakpointSetterForTests(Action<InteropBreakpoint[]> setter)
		{
			lock(_sync) {
				_nativeBreakpointSetter = setter;
			}
			return new NativeBreakpointSetterOverride();
		}

		internal static void ResetForTests()
		{
			lock(_sync) {
				_breakpoints = [];
				_asserts = [];
				_temporaryBreakpoints = [];
				_activeCpuTypes = [];
				_externalBreakpoints.Clear();
				_uiByNativeId.Clear();
				_externalByNativeId.Clear();
			}
		}

		private static void SetNativeBreakpoints(InteropBreakpoint[] breakpoints)
		{
			DebugApi.SetBreakpoints(breakpoints, (UInt32)breakpoints.Length);
		}

		private sealed class NativeBreakpointSetterOverride : IDisposable
		{
			private bool _disposed;

			public void Dispose()
			{
				lock(_sync) {
					if(!_disposed) {
						_nativeBreakpointSetter = SetNativeBreakpoints;
						_disposed = true;
					}
				}
			}
		}
	}
}
