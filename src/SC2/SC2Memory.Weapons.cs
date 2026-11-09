using System.Buffers.Binary;

namespace SC2Radar.SC2
{
    /// <summary>
    /// Weapon ranges, as UnitWeaponRange [exe+0x9BDA40] computes them (5.0.x addresses in brackets, offsets by signature):
    ///   the unit's attack ability: index byte L[0x30] into its ability list L (see Progress; must be &lt; L[0x33] and set in
    ///   the mask dword L[0x20]), decoded like any live ability. Weapons: count byte @+0xA0, array @ *(+0xB0)
    ///   [exe+0xA607F0 / exe+0x882210]. A weapon's catalog entry from the packed link @+8 (same scheme as units).
    ///   Range [exe+0xA4C6D0] = catalog int @+0x4C + ability bonus int @+0x70 + (catalog flag bit 2 @+0x10 ? @+0x78 : @+0x7C),
    ///   fixed 4096, edge to edge. The largest of the unit's weapons is kept.
    /// </summary>
    public sealed partial class SC2Memory
    {
        const string AttackAbilityPattern = "44 0F B6 42 ? 44 3A 42 ? 73 ? 8B 42 ? 41 0F B6 C8 0F A3 C8 73 ? 41 0F B6 C0 48 8D 0C C2 8B 44 C2 ? F7 D0";
        const string WeaponCountPattern   = "44 3A 89 ? ? ? ? 73 ? 4D 8B C2 41 0F B6 D1 E9";
        const string WeaponArrayPattern   = "48 8B 81 ? ? ? ? 49 8B F0 0F B6 D2 48 8B D9 48 8B 3C D0";
        const string WeaponRangePattern   = "49 8B 42 08 45 8B 40 ? 48 8B C8 48 C1 E0 05 48 C1 E9 3B 83 E1 1F 48 8B 40 18 48 8B 0C C8 44 03 41 ? 45 89 01";
        const string WeaponBonusPattern   = "8B 48 ? 41 03 C8 41 89 09 41 83 FB 01 75";

        int  _attackIndexOff = -1, _abilCountOff, _abilMaskOff;
        uint _weaponCountOff, _weaponArrayOff, _rangeBonusOff, _rangeBonus2Off, _weaponRangeOff;
        // per record (and type: the game reuses record memory): its attack ability and the catalog entries of its weapons (stable for the unit's life)
        readonly Dictionary<(ulong Rec, string Type), (ulong Ability, (ulong Cat, int Base, bool Flag)[] Weapons)> _weapons = [];
        // per record: its range with the upgrade bonuses as last read (-1: none), read again at Next - upgrades are rare
        readonly Dictionary<(ulong Rec, string Type), (float Range, long Next)> _rangeOf = [];
        public bool HasWeaponRanges => _attackIndexOff >= 0;
        string _weaponDiag = "";

        string ResolveWeapons(List<(uint Rva, byte[] Data)> code)
        {
            _attackIndexOff = -1;
            _weapons.Clear(); _rangeOf.Clear();
            if (code.Count > 0)
                _weaponDiag = $"hits {FindPattern(code, AttackAbilityPattern, 4).Count}/{FindPattern(code, WeaponCountPattern, 4).Count}/{FindPattern(code, WeaponArrayPattern, 4).Count}/{FindPattern(code, WeaponRangePattern, 4).Count}/{FindPattern(code, WeaponBonusPattern, 4).Count}, " +
                              $"count {SigImm32Agree(code, WeaponCountPattern, 3)}, array {SigImm32Agree(code, WeaponArrayPattern, 3)}" +
                              $", attack {string.Join(" ", FindPattern(code, AttackAbilityPattern, 8).Select(h => CodeBytes(code, h, 40) is { } x ? $"{x[4]:X}/{x[8]:X}/{x[13]:X}" : "?"))}";
            // several abilities share the lookup (the attack, move, ... slots): take the one right before the weapon count check
            if (code.Count > 0 && FindPattern(code, WeaponCountPattern, 2) is [var wc] &&
                FindPattern(code, AttackAbilityPattern, 64).Where(h => wc > h && wc - h < 0x80).Select(h => CodeBytes(code, h, 40) is { } x ? (x[4], x[8], x[13]) : default).Distinct().ToList() is [var ab] && ab != default &&
                SigImm32Agree(code, WeaponCountPattern, 3) is { } count && SigImm32Agree(code, WeaponArrayPattern, 3) is { } arr &&
                FindPattern(code, WeaponRangePattern, 2) is [var r] && CodeBytes(code, r, 40) is { } rb &&
                FindPattern(code, WeaponBonusPattern, 4) is { Count: > 0 } bonus && CodeBytes(code, bonus[0], 3) is { } bb)
            {
                (_attackIndexOff, _abilCountOff, _abilMaskOff) = ab;
                (_weaponCountOff, _weaponArrayOff) = (count, arr);
                (_rangeBonusOff, _weaponRangeOff, _rangeBonus2Off) = (rb[7], rb[33], bb[2]);
                if (_weaponCountOff >= 0x400 || _weaponArrayOff >= 0x400) _attackIndexOff = -1;
            }
            if (!HasWeaponRanges) XMLogging.WriteLine($"[Weapons] signature 'weapon range' missed ({_weaponDiag}) - no range rings");
            return $"weapons {(HasWeaponRanges ? "sig" : "missed")}";
        }

        /// <summary>A packed catalog link (units, weapons): entry = *(*((q &lt;&lt; 5) + 0x18) + ((q &gt;&gt; 59) &amp; 31) * 8).</summary>
        ulong CatalogEntry(ulong q)
        {
            ulong table = ReadU64((q << 5) + 0x18);
            return IsUserPtr(table) ? ReadU64(table + ((q >> 59) & 31) * 8) : 0;
        }

        /// <summary>
        /// <see cref="ResolveUnitWeapons"/> for many records, one batched read per step for all of them (ability list
        /// pointers, the lists, the attack abilities, their weapon arrays, the weapons' catalog links, the catalog tables,
        /// the entries, the entries' ranges) instead of a few serial reads per unit.
        /// </summary>
        (ulong, (ulong, int, bool)[])[] ResolveUnitWeapons(ulong[] recs)
        {
            var result = new (ulong, (ulong, int, bool)[])[recs.Length];
            Array.Fill(result, (0UL, Array.Empty<(ulong, int, bool)>()));
            // ability lists
            var lists = ReadPointers([.. recs.Select(r => r + _abilityListOff)]);
            var withList = Enumerable.Range(0, recs.Length).Where(i => IsUserPtr(lists[i] & ~3UL)).ToArray();
            var lbs = ReadRanges([.. withList.Select(i => (lists[i] & ~3UL, AbilityListLen))]);
            // the attack ability of each
            uint k1 = U32At(_abilityK1Rva), k2 = U32At(_abilityK2Rva);
            var abils = new List<(int Rec, ulong Abil)>();
            for (int k = 0; k < withList.Length; k++)
            {
                if (lbs[k] is not { } lb) continue;
                int idx = lb[_attackIndexOff];
                if (idx >= lb[_abilCountOff] || idx >= 32 || (BinaryPrimitives.ReadUInt32LittleEndian(lb.AsSpan(_abilMaskOff)) >> idx & 1) == 0) continue;
                ulong abil = ((ulong)(BinaryPrimitives.ReadUInt32LittleEndian(lb.AsSpan(0x3C + 8 * idx)) - k2) << 32) | (~BinaryPrimitives.ReadUInt32LittleEndian(lb.AsSpan(0x38 + 8 * idx)) + k1);
                if (IsUserPtr(abil)) abils.Add((withList[k], abil));
            }
            // weapon count and array of each ability, then the arrays
            uint lo = Math.Min(_weaponCountOff, _weaponArrayOff);
            int hlen = (int)(Math.Max(_weaponCountOff + 1, _weaponArrayOff + 8) - lo);
            var heads = ReadRanges([.. abils.Select(a => (a.Abil + lo, hlen))]);
            var withArr = new List<(int Rec, ulong Abil, int N, ulong Arr)>();
            for (int k = 0; k < abils.Count; k++)
            {
                if (heads[k] is not { } h) continue;
                int n = h[(int)(_weaponCountOff - lo)];
                ulong arr = BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan((int)(_weaponArrayOff - lo)));
                if (n is > 0 and <= 8 && IsUserPtr(arr)) withArr.Add((abils[k].Rec, abils[k].Abil, n, arr));
            }
            var arrs = ReadRanges([.. withArr.Select(a => (a.Arr, a.N * 8))]);
            // every weapon: its catalog link, the link's table, the entry, the entry's range and flags
            var weapons = new List<(int Unit, ulong W)>();
            for (int k = 0; k < withArr.Count; k++)
                if (arrs[k] is { } wb)
                    for (int i = 0; i < withArr[k].N; i++)
                        if (BinaryPrimitives.ReadUInt64LittleEndian(wb.AsSpan(i * 8)) is var w && IsUserPtr(w)) weapons.Add((k, w));
            var links = ReadPointers([.. weapons.Select(x => x.W + 8)]);
            var tables = ReadPointers([.. links.Select(q => IsUserPtr((q << 5) + 0x18) ? (q << 5) + 0x18 : 0UL)]);
            var cats = ReadPointers([.. weapons.Select((x, i) => IsUserPtr(tables[i]) ? tables[i] + ((links[i] >> 59) & 31) * 8 : 0UL)]);
            var cbs = ReadRanges([.. cats.Select(c => (c, IsUserPtr(c) ? (int)_weaponRangeOff + 4 : 0))]);
            var found = new List<(ulong, int, bool)>[withArr.Count];
            for (int i = 0; i < weapons.Count; i++)
            {
                if (!IsUserPtr(cats[i]) || cbs[i] is not { Length: > 0 } cb) continue;
                (found[weapons[i].Unit] ??= []).Add((cats[i], BinaryPrimitives.ReadInt32LittleEndian(cb.AsSpan((int)_weaponRangeOff)),
                                                    (BinaryPrimitives.ReadUInt32LittleEndian(cb.AsSpan(0x10)) >> 2 & 1) != 0));
            }
            for (int k = 0; k < withArr.Count; k++)
                if (found[k] is { Count: > 0 } f) result[withArr[k].Rec] = (withArr[k].Abil, [.. f]);
            return result;
        }

        /// <summary>The record's attack ability and weapons, or (0, []) for a unit without weapons.</summary>
        (ulong, (ulong, int, bool)[]) ResolveUnitWeapons(ulong rec)
        {
            ulong L = ReadU64(rec + _abilityListOff) & ~3UL;
            var lb = IsUserPtr(L) ? ReadBytes(L, 0x38 + 8 * 32) : null;
            if (lb == null) return (0, []);
            int idx = lb[_attackIndexOff];
            if (idx >= lb[_abilCountOff] || idx >= 32 || (BinaryPrimitives.ReadUInt32LittleEndian(lb.AsSpan(_abilMaskOff)) >> idx & 1) == 0) return (0, []);
            uint k1 = U32At(_abilityK1Rva), k2 = U32At(_abilityK2Rva);
            ulong abil = ((ulong)(BinaryPrimitives.ReadUInt32LittleEndian(lb.AsSpan(0x3C + 8 * idx)) - k2) << 32) | (~BinaryPrimitives.ReadUInt32LittleEndian(lb.AsSpan(0x38 + 8 * idx)) + k1);
            if (!IsUserPtr(abil) || ReadBytes(abil + _weaponCountOff, 1) is not [var n] || n is 0 or > 8) return (0, []);
            ulong arr = ReadU64(abil + _weaponArrayOff);
            if (!IsUserPtr(arr) || ReadBytes(arr, n * 8) is not { } wb) return (0, []);
            var weapons = new List<(ulong, int, bool)>();
            for (int i = 0; i < n; i++)
            {
                ulong w = BinaryPrimitives.ReadUInt64LittleEndian(wb.AsSpan(i * 8));
                ulong cat = IsUserPtr(w) ? CatalogEntry(ReadU64(w + 8)) : 0;
                if (!IsUserPtr(cat) || ReadBytes(cat, (int)_weaponRangeOff + 4) is not { } cb) continue;
                weapons.Add((cat, BinaryPrimitives.ReadInt32LittleEndian(cb.AsSpan((int)_weaponRangeOff)),
                             (BinaryPrimitives.ReadUInt32LittleEndian(cb.AsSpan(0x10)) >> 2 & 1) != 0));
            }
            return weapons.Count > 0 ? (abil, [.. weapons]) : (0, []);
        }

        /// <summary>Per record: the longest weapon range in map units (records without weapons are absent). New records are
        /// resolved in one batched pass; the upgrade bonuses are read every pass in one batch.</summary>
        Dictionary<ulong, float> ReadWeaponRanges((ulong Rec, string Type)[] records)
        {
            var result = new Dictionary<ulong, float>();
            if (!HasWeaponRanges || _abilityK1Rva == 0) return result;
            // new records, all at once (batched); a few hundred per pass at most
            var fresh = records.Where(k => !_weapons.ContainsKey(k)).Take(400).ToArray();
            if (fresh.Length > 0)
            {
                var resolved = ResolveUnitWeapons([.. fresh.Select(k => k.Rec)]);
                for (int i = 0; i < fresh.Length; i++) _weapons[fresh[i]] = resolved[i];
            }
            // the bonuses of each armed unit about once a second; the rest from the last read
            long now = Environment.TickCount64;
            var armed = records.Where(r => _weapons.TryGetValue(r, out var w) && w.Ability != 0).ToArray();
            foreach (var r in armed)
                if (_rangeOf.TryGetValue(r, out var c) && now < c.Next && c.Range > 0f) result[r.Rec] = c.Range;
            armed = [.. armed.Where(r => !(_rangeOf.TryGetValue(r, out var c) && now < c.Next))];
            if (armed.Length == 0) return result;
            uint lo = _rangeBonusOff;
            int len = (int)(Math.Max(_rangeBonusOff, Math.Max(_rangeBonus2Off, _rangeBonus2Off - 4)) - lo + 4);
            var buf = new byte[armed.Length * len]; var ok = new bool[armed.Length];
            if (!ReadValues(armed.Select(k => _weapons[k].Ability + lo).ToArray(), len, buf, ok)) return result;
            for (int i = 0; i < armed.Length; i++)
            {
                if (!ok[i]) continue;
                var b = buf.AsSpan(i * len, len);
                int bonus = BinaryPrimitives.ReadInt32LittleEndian(b), flagged = BinaryPrimitives.ReadInt32LittleEndian(b[(int)(_rangeBonus2Off - 4 - lo)..]),
                    plain = BinaryPrimitives.ReadInt32LittleEndian(b[(int)(_rangeBonus2Off - lo)..]);
                int best = _weapons[armed[i]].Weapons.Max(w => w.Base + bonus + (w.Flag ? flagged : plain));
                if (best > 0) result[armed[i].Rec] = best / 4096f;
                _rangeOf[armed[i]] = (best > 0 ? best / 4096f : -1f, now + RecheckMs(armed[i].Rec));
            }
            return result;
        }
        /// <summary>Forget units that went away.</summary>
        void PruneWeapons(HashSet<(ulong, string)> live)
        {
            foreach (var k in _weapons.Keys.Where(k => !live.Contains(k)).ToList()) _weapons.Remove(k);
            foreach (var k in _rangeOf.Keys.Where(k => !live.Contains(k)).ToList()) _rangeOf.Remove(k);
        }
    }
}
