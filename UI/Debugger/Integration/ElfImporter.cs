using ELFSharp.ELF.Sections;
using Mesen.Config;
using Mesen.Debugger.Labels;
using Mesen.Integration.Elf;
using Mesen.Interop;
using Mesen.Utilities;
using Mesen.Windows;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Mesen.Debugger.Integration;

public class ElfImporter : ISymbolProvider
{
	//NOT USED
	public DateTime SymbolFileStamp => throw new NotImplementedException();

	//NOT USED
	public string SymbolPath => throw new NotImplementedException();

	public List<SourceFileInfo> SourceFiles { get; private set; } = new List<SourceFileInfo>();
	private List<SourceSymbol> _symbols = new();
	private Dictionary<string, SourceSymbol> _symbolsByName = new();
	private Dictionary<string, AddressInfo> _addressByLine = new();
	private Dictionary<string, AddressInfo> _lastAddressByLine = new();
	private Dictionary<string, SourceCodeLocation> _linesByAddress = new();

	public void Import(string path, bool showResult, CpuType cpuType)
	{
		try {
			MemoryType memType = cpuType.ToMemoryType();

			DwarfSymbolProvider? dwarf = DwarfSymbolProvider.Load(path);
			if(dwarf == null) {
				return;
			}

			string baseFolder = Path.GetDirectoryName(path) ?? "";
			string[] fileList = Directory.GetFiles(baseFolder, "*", SearchOption.AllDirectories);

			Dictionary<string, DwarfFileInformation> files = new();
			foreach(DwarfFileInformation file in dwarf.LineMappings.SelectMany(x => x.Files)) {
				if(files.TryGetValue(file.Path, out DwarfFileInformation? existingFile)) {
					existingFile.Lines.AddRange(file.Lines);
				} else {
					if(file.Lines.Count > 0) {
						DwarfFileInformation fileInfo = new() { Path = file.Path, Name = file.Name, Lines = new(file.Lines) };
						files[file.Path] = fileInfo;
					}
				}
			}

			foreach(DwarfFileInformation file in files.Values) {
				string ext = Path.GetExtension(file.Path).ToLower();
				bool isAsm = ext != ".c" && ext != ".h";

				string? filePath;
				if(File.Exists(file.Path)) {
					filePath = file.Path;
				} else {
					filePath = fileList.Where(x => x.EndsWith(file.Name)).FirstOrDefault();
				}

				if(File.Exists(filePath)) {
					SourceFileInfo srcFileInfo = new(file.Name, isAsm, new FileInfo(filePath));
					SourceFiles.Add(srcFileInfo);

					for(int i = 0; i < file.Lines.Count; i++) {
						DwarfLineInformation line = file.Lines[i];
						if(line.Address < 0x2000000) {
							continue;
						}

						int orgLineNumber = (int)line.Line - 1;
						int lineNumber = (int)line.Line - 1;
						uint startAddress = line.Address;
						uint endAddress = line.Address;

						for(int j = i + 1; j < file.Lines.Count; j++) {
							line = file.Lines[i];
							lineNumber = (int)line.Line - 1;
							/*AddressInfo addr = DebugApi.GetAbsoluteAddress(new AddressInfo { Address = (int)line.Address, Type = MemoryType.GbaMemory });
							if(addr.Address >= 0) {
								_linesByAddress[addr.Type.ToString() + addr.Address.ToString()] = new SourceCodeLocation(srcFileInfo, lineNumber);
							}*/

							if(lineNumber != orgLineNumber) {
								i = j - 1;
								break;
							}
							endAddress = Math.Max(line.Address, endAddress);
						}

						AddressInfo startAddr = DebugApi.GetAbsoluteAddress(new AddressInfo { Address = (int)startAddress, Type = MemoryType.GbaMemory });
						AddressInfo endAddr = DebugApi.GetAbsoluteAddress(new AddressInfo { Address = (int)endAddress, Type = MemoryType.GbaMemory });

						string fileLineKey = file.Name + "_" + lineNumber.ToString();
						if(startAddr.Address >= 0) {
							if(!_addressByLine.TryGetValue(fileLineKey, out AddressInfo existingAddr)) {
								_addressByLine[fileLineKey] = startAddr;
								_linesByAddress[startAddr.Type.ToString() + startAddr.Address.ToString()] = new SourceCodeLocation(srcFileInfo, lineNumber);
							}

							if(!_lastAddressByLine.TryGetValue(fileLineKey, out existingAddr)) {
								_lastAddressByLine[fileLineKey] = endAddr;
							}
						}
					}
				}
			}

			Dictionary<AddressInfo, CodeLabel> labels = new();
			HashSet<string> usedLabels = new();

			foreach(SymbolEntry<uint> symbol in dwarf.Symbols) {
				if(!string.IsNullOrWhiteSpace(symbol.Name) && symbol.Type != SymbolType.File && symbol.Type != SymbolType.Section && symbol.Type != SymbolType.Object && symbol.PointedSection != null) {
					uint value = symbol.Value & ~(uint)0x01;
					AddressInfo relAddr = new AddressInfo() { Address = (int)value, Type = memType };
					AddressInfo absAddr = DebugApi.GetAbsoluteAddress(relAddr);
					AddressInfo labelAddr = absAddr.Type != MemoryType.None ? absAddr : relAddr;

					if(!ConfigManager.Config.Debug.Integration.IsMemoryTypeImportEnabled(labelAddr.Type)) {
						continue;
					}

					if(symbol.Name.StartsWith("$") && labels.ContainsKey(labelAddr)) {
						continue;
					}

					string name = symbol.Name switch {
						"$a" => "arm_" + relAddr.Address.ToString("X7"),
						"$d" => "data_" + relAddr.Address.ToString("X7"),
						"$t" => "thumb_" + relAddr.Address.ToString("X7"),
						_ => symbol.Name
					};

					//Demangle and replace any invalid characters with underscores
					name = LabelManager.InvalidLabelRegex.Replace(Demangle(name), "_");

					if(labels.Remove(labelAddr, out CodeLabel? existingLabel)) {
						usedLabels.Remove(existingLabel.Label);
					}

					int j = 0;
					string orgLabel = name;
					while(usedLabels.Contains(name)) {
						j++;
						name = orgLabel + j.ToString();
					}

					usedLabels.Add(name);

					labels[labelAddr] = new CodeLabel() {
						Label = name,
						Address = (uint)labelAddr.Address,
						MemoryType = labelAddr.Type,
						Length = symbol.Type != SymbolType.Function && symbol.Size > 1 ? symbol.Size : 1
					};

					SourceSymbol srcSymbol = new(name, labelAddr.Address, symbol);
					_symbols.Add(srcSymbol);
					_symbolsByName[name] = srcSymbol;
				}
			}

			if(labels.Count > 0) {
				LabelManager.SetLabels(labels.Values);
			}

			if(showResult) {
				MesenMsgBox.Show(null, "ImportLabels", MessageBoxButtons.OK, MessageBoxIcon.Info, labels.Count.ToString());
			}
		} catch(Exception ex) {
			if(showResult) {
				MesenMsgBox.ShowException(ex);
			}
		}
	}

	private static string Demangle(string name)
	{
		int index = name.IndexOf("_Z");
		if(index >= 0) {
			List<string> parts = new();
			int i = 0;

			while(i < name.Length && (name[i] < '0' || name[i] > '9')) {
				i++;
			}

			while(true) {
				bool hasLen = false;
				int start = i;
				while(i < name.Length && name[i] >= '0' && name[i] <= '9') {
					i++;
					hasLen = true;
				}

				if(hasLen) {
					int val = int.Parse(name.AsSpan(start, i - start));

					if(start + val <= name.Length) {
						string part = name.Substring(i, val);
						if(!string.IsNullOrWhiteSpace(part) && part != "_GLOBAL__N_1") {
							parts.Add(part);
						}
						i += val;
					} else {
						break;
					}
				} else {
					break;
				}
			}

			if(parts.Count > 0) {
				return string.Join('_', parts);
			}
		}

		return name;
	}

	public AddressInfo? GetLineAddress(SourceFileInfo file, int lineIndex)
	{
		if(_addressByLine.TryGetValue(file.Name.ToString() + "_" + lineIndex.ToString(), out AddressInfo address)) {
			return address;
		}
		return null;
	}

	public AddressInfo? GetLineEndAddress(SourceFileInfo file, int lineIndex)
	{
		/*if(_lastAddressByLine.TryGetValue(file.Name.ToString() + "_" + lineIndex.ToString(), out AddressInfo address)) {
			return address;
		}*/
		return null;
	}

	public string? GetSourceCodeLine(int prgRomAddress)
	{
		//not used!
		throw new NotImplementedException();
	}

	public SourceCodeLocation? GetSourceCodeLineInfo(AddressInfo address)
	{
		string key = address.Type.ToString() + address.Address.ToString();
		SourceCodeLocation location;
		if(_linesByAddress.TryGetValue(key, out location)) {
			return location;
		}
		return null;
	}

	public SourceSymbol? GetSymbol(string word, int scopeStart, int scopeEnd)
	{
		if(_symbolsByName.TryGetValue(word, out SourceSymbol symbol)) {
			return symbol;
		}
		return null;
	}

	public AddressInfo? GetSymbolAddressInfo(SourceSymbol symbol)
	{
		if(symbol.Address >= 0) {
			return DebugApi.GetAbsoluteAddress(new AddressInfo() { Address = (int)symbol.Address, Type = MemoryType.GbaMemory });
		}
		return null;
	}

	public SourceCodeLocation? GetSymbolDefinition(SourceSymbol symbol)
	{
		return null;
	}

	public List<SourceSymbol> GetSymbols()
	{
		return _symbols;
	}

	public int GetSymbolSize(SourceSymbol srcSymbol)
	{
		//TODO
		return 1;
	}

	private class FileInfo : IFileDataProvider
	{
		private string[]? _data = null;
		private string? _sourceFile = null;
		
		public string[] Data
		{
			get
			{
				if(_data != null) {
					return _data;
				} else if(_sourceFile == null || !File.Exists(_sourceFile)) {
					_data = Array.Empty<string>();
				} else {
					_data = File.ReadAllLines(_sourceFile);
				}

				return _data;
			}
		}

		public FileInfo(string sourceFile)
		{
			_sourceFile = sourceFile;
		}
	}
}
