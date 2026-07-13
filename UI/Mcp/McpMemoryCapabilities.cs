using System;
using Mesen.Interop;

namespace Mesen.Mcp;

internal static class McpMemoryCapabilities
{
	internal static bool CanWrite(MemoryType type)
	{
		return type switch {
			MemoryType.NecDspMemory or
			MemoryType.SnesPrgRom or
			MemoryType.SnesRegister or
			MemoryType.SpcRom or
			MemoryType.DspProgramRom or
			MemoryType.DspDataRom or
			MemoryType.St018PrgRom or
			MemoryType.St018DataRom or
			MemoryType.SufamiTurboFirmware or
			MemoryType.SufamiTurboSecondCart or
			MemoryType.GbPrgRom or
			MemoryType.GbBootRom or
			MemoryType.NesPrgRom or
			MemoryType.NesChrRom or
			MemoryType.PcePrgRom or
			MemoryType.SmsPrgRom or
			MemoryType.SmsBootRom or
			MemoryType.SmsPort or
			MemoryType.GbaPrgRom or
			MemoryType.GbaBootRom or
			MemoryType.WsPrgRom or
			MemoryType.WsBootRom or
			MemoryType.WsPort or
			MemoryType.None => false,

			MemoryType.SnesMemory or
			MemoryType.SpcMemory or
			MemoryType.Sa1Memory or
			MemoryType.GsuMemory or
			MemoryType.Cx4Memory or
			MemoryType.St018Memory or
			MemoryType.GameboyMemory or
			MemoryType.NesMemory or
			MemoryType.NesPpuMemory or
			MemoryType.PceMemory or
			MemoryType.SmsMemory or
			MemoryType.GbaMemory or
			MemoryType.WsMemory or
			MemoryType.SnesWorkRam or
			MemoryType.SnesSaveRam or
			MemoryType.SnesVideoRam or
			MemoryType.SnesSpriteRam or
			MemoryType.SnesCgRam or
			MemoryType.SpcRam or
			MemoryType.SpcDspRegisters or
			MemoryType.DspDataRam or
			MemoryType.Sa1InternalRam or
			MemoryType.GsuWorkRam or
			MemoryType.Cx4DataRam or
			MemoryType.BsxPsRam or
			MemoryType.BsxMemoryPack or
			MemoryType.St018WorkRam or
			MemoryType.SufamiTurboSecondCartRam or
			MemoryType.GbWorkRam or
			MemoryType.GbCartRam or
			MemoryType.GbHighRam or
			MemoryType.GbVideoRam or
			MemoryType.GbSpriteRam or
			MemoryType.GbBgPaletteRam or
			MemoryType.GbObjPaletteRam or
			MemoryType.NesInternalRam or
			MemoryType.NesWorkRam or
			MemoryType.NesSaveRam or
			MemoryType.NesNametableRam or
			MemoryType.NesMapperRam or
			MemoryType.NesSpriteRam or
			MemoryType.NesSecondarySpriteRam or
			MemoryType.NesPaletteRam or
			MemoryType.NesChrRam or
			MemoryType.PceWorkRam or
			MemoryType.PceSaveRam or
			MemoryType.PceCdromRam or
			MemoryType.PceCardRam or
			MemoryType.PceAdpcmRam or
			MemoryType.PceArcadeCardRam or
			MemoryType.PceVideoRam or
			MemoryType.PceVideoRamVdc2 or
			MemoryType.PceSpriteRam or
			MemoryType.PceSpriteRamVdc2 or
			MemoryType.PcePaletteRam or
			MemoryType.SmsWorkRam or
			MemoryType.SmsCartRam or
			MemoryType.SmsVideoRam or
			MemoryType.SmsPaletteRam or
			MemoryType.GbaSaveRam or
			MemoryType.GbaIntWorkRam or
			MemoryType.GbaExtWorkRam or
			MemoryType.GbaVideoRam or
			MemoryType.GbaSpriteRam or
			MemoryType.GbaPaletteRam or
			MemoryType.WsWorkRam or
			MemoryType.WsCartRam or
			MemoryType.WsCartEeprom or
			MemoryType.WsInternalEeprom => true,

			_ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown memory type.")
		};
	}
}
