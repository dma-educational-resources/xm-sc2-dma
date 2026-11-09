namespace SC2Radar.SC2
{
    public sealed partial class SC2Memory
    {
        /// <summary>
        /// The decoder a selector switch picks: case table (exe RVA of 16 dword RVAs) -> the case's code -> the first call
        /// in it ("lea rcx,[...]; call decoder"). 0 when it can't be read.
        /// </summary>
        ulong SwitchDecoder(uint caseTableRva)
        {
            uint sel = _slotSelectorRva != 0 ? U32At(_slotSelectorRva) : 99;
            if (sel > 15) return 0;
            uint caseRva = U32At(caseTableRva + sel * 4);
            if (caseRva == 0 || ReadBytes(_exeBase + caseRva, 24) is not { } b) return 0;
            for (int i = 0; i + 5 <= b.Length; i++)
                if (b[i] == 0xE8) return (ulong)((long)caseRva + i + 5 + BitConverter.ToInt32(b, i + 1));
            return 0;
        }

        /// <summary>Runs a two-argument decoder (rcx = output, rdx = encoded input address) on <paramref name="at"/>; the
        /// 8 decoded bytes, or null. <paramref name="known"/>: memory already read around it (the emulator reads it from there).</summary>
        ulong? Decode8(ulong decoder, ulong at, (ulong Addr, byte[] Data)? known = null)
        {
            try
            {
                var emu = new MiniEmu(this, null);
                if (known is { } k) emu.Preload(k.Addr, k.Data);
                return emu.Call(decoder, MiniEmu.OutBuf, at, 2000) ? emu.ReadOut(0, 8) : null;
            }
            catch { return null; }
        }
    }
}
