
namespace SC2Radar.SC2
{
    /// <summary>
    /// Transport cargo (roadmap D5). A transport's loaded units belong to its transport ability ("MedivacTransport",
    /// "WarpPrismTransport", "OverlordTransport", "HerculesTransport", "BunkerTransport", "CommandCenterTransport",
    /// "NydusCanalTransport", ...): count (u32) at ability + 0x68, slots at [ability + 0x78], 0x1C bytes each with the
    /// loaded unit's tag at +0 (index = tag &gt;&gt; 18, as the game's API) and its cargo size at +0xC. A unit record keeps
    /// its own tag at +0, so index -&gt; record comes from the records on the map - loaded units stay in the actor list, at
    /// the spot they boarded, which is why the radar hides them (Carried).
    /// </summary>
    public sealed partial class SC2Memory
    {
        const int CargoCountOff = 0x68, CargoListOff = 0x78, CargoSlot = 0x1C;

        /// <summary>A transport ability's cargo from +0x68: how many units are loaded, and the slot list.</summary>
        [StructLayout(LayoutKind.Explicit, Size = 0x18)]
        struct CargoHeader
        {
            [FieldOffset(0)]                             public uint  Count;
            [FieldOffset(CargoListOff - CargoCountOff)]  public ulong List;
        }

        volatile Dictionary<ulong, ulong[]> _cargo = [];
        volatile HashSet<ulong> _carried = [];
        DateTime _nextCargoScan;
        readonly Dictionary<string, bool> _typeHasTransport = new(StringComparer.OrdinalIgnoreCase);   // per unit type
        readonly Dictionary<ulong, ulong> _transportAbil = [];   // record -> its transport ability instance (0: none)

        /// <summary>Transport record -> the records of the units inside it.</summary>
        public IReadOnlyDictionary<ulong, ulong[]> Cargo => _cargo;

        /// <summary>Records of units inside a transport, bunker or Nydus right now.</summary>
        public IReadOnlySet<ulong> Carried => _carried;

        /// <summary>
        /// Scans worker, twice a second: every transport's cargo. Which types can carry is learnt once per type from
        /// their ability names; the transport ability of each unit is found once and kept.
        /// </summary>
        void ScanCargo()
        {
            if (DateTime.UtcNow < _nextCargoScan || _abilityK1Rva == 0) return;
            _nextCargoScan = DateTime.UtcNow.AddMilliseconds(500);
            var units = Snapshot.Units;
            var byIndex = new Dictionary<uint, ulong>();
            var transports = new List<ulong>();
            var seen = new HashSet<ulong>();
            foreach (var u in units)
            {
                if (u.Record == 0 || !seen.Add(u.Record) || u.Owner is 0 or >= 16) continue;
                if (u.Category is not (UnitCategory.Unit or UnitCategory.Building or UnitCategory.Worker or UnitCategory.Hero)) continue;
                if (u.TypeName.Length > 0 && !_typeHasTransport.TryGetValue(u.TypeName, out bool can))
                    _typeHasTransport[u.TypeName] = can = TransportAbility(u.Record) != 0;
                else can = u.TypeName.Length > 0 && _typeHasTransport[u.TypeName];
                if (can) transports.Add(u.Record);
            }
            if (transports.Count == 0) { if (_cargo.Count > 0) { _cargo = []; _carried = []; } return; }

            // index -> record: the tag at +0 of every record on the map (one batched read)
            var recs = seen.ToArray();
            var tb = new byte[recs.Length * 4]; var tok = new bool[recs.Length];
            if (!ReadValues(recs, 4, tb, tok)) return;
            for (int i = 0; i < recs.Length; i++)
                if (tok[i]) byIndex[BinaryPrimitives.ReadUInt32LittleEndian(tb.AsSpan(i * 4)) >> 18] = recs[i];

            // every transport's cargo header (count, list) in one batched read, then every non-empty list in another
            foreach (ulong t in transports)
                if (!_transportAbil.ContainsKey(t)) _transportAbil[t] = TransportAbility(t);
            var withAbil = transports.Where(t => _transportAbil[t] != 0).ToArray();
            if (!ReadStructs<CargoHeader>([.. withAbil.Select(t => _transportAbil[t] + CargoCountOff)], out var headers, out var hok)) return;
            var loaded = new List<(ulong Transport, int Count, ulong List)>();
            for (int i = 0; i < withAbil.Length; i++)
                if (hok[i] && headers[i].Count is > 0 and <= 255 && IsUserPtr(headers[i].List))
                    loaded.Add((withAbil[i], (int)headers[i].Count, headers[i].List));
            var slots = ReadRanges([.. loaded.Select(l => (l.List, l.Count * CargoSlot))]);

            var cargo = new Dictionary<ulong, ulong[]>();
            var carried = new HashSet<ulong>();
            for (int i = 0; i < loaded.Count; i++)
            {
                if (slots[i] is not { } sb) continue;
                var (t, n, _) = loaded[i];
                var inside = new List<ulong>(n);
                for (int k = 0; k < n; k++)
                    if (byIndex.TryGetValue(BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(k * CargoSlot)) >> 18, out ulong r) && r != t)
                    { inside.Add(r); carried.Add(r); }
                if (inside.Count > 0) cargo[t] = [.. inside];
            }
            if (_transportAbil.Count > 4096) _transportAbil.Clear();   // records of past matches
            (_cargo, _carried) = (cargo, carried);
        }

        /// <summary>The record's transport ability instance (an ability whose catalog name ends in "Transport"), or 0.</summary>
        ulong TransportAbility(ulong rec)
        {
            foreach (ulong a in UnitAbilities(rec))
                if (CatalogNameOf(a) is var n && n.StartsWith("Abil/", StringComparison.Ordinal) &&
                    n.AsSpan(5, n.IndexOf(' ') > 5 ? n.IndexOf(' ') - 5 : n.Length - 5).EndsWith("Transport", StringComparison.OrdinalIgnoreCase))
                    return a;
            return 0;
        }
        static readonly HashSet<string> TransportTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "Medivac", "WarpPrism", "WarpPrismPhasing", "Overlord", "OverlordTransport", "Hercules", "Bunker",
            "CommandCenter", "NydusNetwork", "NydusCanal",
        };

        // packed catalog link -> "Kind/Name", "" when it is no catalog link (scans worker). Links are per catalog entry, so
        // every unit's own ability instance of one type shares one: a new unit costs one batched read, not a name lookup.
        readonly Dictionary<ulong, string> _catalogLinkNames = [];

        /// <summary>
        /// <see cref="CatalogNameOf"/> for many objects at once: their first 0x80 bytes in one batched read, then the
        /// links not named yet resolved together (<see cref="ResolveCatalogLinks"/>), offset by offset as CatalogNameOf
        /// tries them.
        /// </summary>
        string[] CatalogNamesOf(IReadOnlyList<ulong> objs)
        {
            var result = new string[objs.Count];
            Array.Fill(result, "");
            var obs = ReadRanges([.. objs.Select(o => (o, IsUserPtr(o) ? 0x80 : 0))]);
            var cands = new List<(ulong Link, int Off)>[objs.Count];
            for (int i = 0; i < objs.Count; i++)
            {
                cands[i] = [];
                if (obs[i] is not { Length: 0x80 } ob) continue;
                for (int o = 0; o < 0x80; o += 8)
                    if (BinaryPrimitives.ReadUInt64LittleEndian(ob.AsSpan(o)) << 5 is var link && IsUserPtr(link)) cands[i].Add((link, o));
            }
            var done = new bool[objs.Count];
            for (int d = 0; d < 16; d++)
            {
                var open = Enumerable.Range(0, objs.Count).Where(i => !done[i] && cands[i].Count > d).ToArray();
                if (open.Length == 0) break;
                var resolved = ResolveCatalogLinks([.. open.Select(i => cands[i][d].Link).Where(l => !_catalogLinkNames.ContainsKey(l)).Distinct()]);
                foreach (int i in open)
                {
                    var (link, o) = cands[i][d];
                    if ((_catalogLinkNames.TryGetValue(link, out var n) || resolved.TryGetValue(link, out n)) && n.Length > 0)
                    { result[i] = $"{n} @+0x{o:X}"; done[i] = true; }
                }
            }
            if (_catalogLinkNames.Count > 8192) _catalogLinkNames.Clear();
            return result;
        }

        /// <summary>
        /// Names packed catalog links in one batched read per step (catalog, kind, index, id array, id record, name) and
        /// caches the definite answers; a name that couldn't be read comes back as "Kind/?" and is tried again next time.
        /// </summary>
        Dictionary<ulong, string> ResolveCatalogLinks(ulong[] links)
        {
            var result = new Dictionary<ulong, string>();
            if (links.Length == 0) return result;
            var cats = ReadPointers(links);
            var kindPtrs = ReadPointers([.. cats.Select(c => IsUserPtr(c) ? c + 8 : 0UL)]);
            var kinds = ReadRanges([.. kindPtrs.Select(p => (p, p > 0x10000 ? 24 : 0))]);
            var idxs = ReadRanges([.. links.Select(l => (l + 0xA, 2))]);
            var arrs = ReadPointers([.. cats.Select(c => IsUserPtr(c) ? c + 0xA0 : 0UL)]);
            var recs = ReadPointers([.. links.Select((_, i) => IsUserPtr(arrs[i]) && idxs[i] is { Length: 2 } ib
                                                         ? arrs[i] + BinaryPrimitives.ReadUInt16LittleEndian(ib) * 8UL : 0UL)]);
            var ses = ReadRanges([.. recs.Select(r => (r + 8, r > 0x10000 ? 16 : 0))]);
            var names = ReadRanges([.. ses.Select(se =>
            {
                if (se is not { Length: 16 }) return (0UL, 0);
                ulong s = BinaryPrimitives.ReadUInt64LittleEndian(se), e = BinaryPrimitives.ReadUInt64LittleEndian(se.AsSpan(8));
                return s > 0x10000 && e > s && e - s < 80 ? (s, (int)(e - s)) : (0UL, 0);
            })]);
            for (int i = 0; i < links.Length; i++)
            {
                int n = 0;
                if (kinds[i] is { } kb) while (n < kb.Length && kb[n] >= 0x20 && kb[n] < 0x7F) n++;
                string kind = n > 0 ? Encoding.ASCII.GetString(kinds[i]!, 0, n) : "";
                if (kind.Length < 3)
                {
                    // no catalog behind it: definite only when the catalog pointer itself was read (else tried again next time)
                    result[links[i]] = "";
                    if (IsUserPtr(cats[i])) _catalogLinkNames[links[i]] = "";
                    continue;
                }
                if (kind == "Abil") _abilCatalog = cats[i];   // for ability names by index (EventFeed)
                if (names[i] is { Length: > 0 } nb) result[links[i]] = _catalogLinkNames[links[i]] = $"{kind}/{Encoding.ASCII.GetString(nb)}";
                else result[links[i]] = $"{kind}/?";
            }
            return result;
        }

        /// <summary>
        /// A catalog object's name, found from a packed catalog link anywhere in its first 0x80 bytes: link = q &lt;&lt; 5,
        /// [link] = the catalog ([[catalog + 8]] its kind, "Abil" / "Behavior" / ...), index = u16 at link + 0xA,
        /// [[catalog + 0xA0] + index * 8] = the id record, name = [rec + 8] .. [rec + 0x10] (as BehaviourId). Returns
        /// "Kind/Name @+offset", or "" when nothing in it is such a link.
        /// </summary>
        string CatalogNameOf(ulong obj)
        {
            if (ReadBytes(obj, 0x80) is not { } ob) return "";
            for (int o = 0; o < 0x80; o += 8)
            {
                ulong link = BinaryPrimitives.ReadUInt64LittleEndian(ob.AsSpan(o)) << 5;
                if (!IsUserPtr(link)) continue;
                ulong cat = ReadU64(link);
                if (!IsUserPtr(cat)) continue;
                string kind = AsciiAt(ReadU64(cat + 8), 24);
                if (kind.Length < 3) continue;
                if (ReadBytes(link + 0xA, 2) is not { } ib) continue;
                ulong arr = ReadU64(cat + 0xA0), rec = IsUserPtr(arr) ? ReadU64(arr + BinaryPrimitives.ReadUInt16LittleEndian(ib) * 8UL) : 0;
                ulong s = rec > 0x10000 ? ReadU64(rec + 8) : 0, e = rec > 0x10000 ? ReadU64(rec + 0x10) : 0;
                string name = s > 0x10000 && e > s && e - s < 80 && ReadBytes(s, (int)(e - s)) is { } nb ? Encoding.ASCII.GetString(nb) : "?";
                if (kind == "Abil") _abilCatalog = cat;   // for ability names by index (EventFeed)
                return $"{kind}/{name} @+0x{o:X}";
            }
            return "";
        }

        /// <summary>Printable ASCII at <paramref name="p"/> up to the first non-printable byte, "" when unreadable.</summary>
        string AsciiAt(ulong p, int max)
        {
            if (p <= 0x10000 || ReadBytes(p, max) is not { } b) return "";
            int n = 0; while (n < b.Length && b[n] >= 0x20 && b[n] < 0x7F) n++;
            return Encoding.ASCII.GetString(b, 0, n);
        }

        /// <summary>A unit record's ability instances (the ability list at record + the resolved list offset).</summary>
        List<ulong> UnitAbilities(ulong rec)
        {
            var list = new List<ulong>();
            if (_abilityK1Rva == 0) return list;
            ulong L = ReadU64(rec + _abilityListOff) & ~3UL;
            if (!IsUserPtr(L) || ReadBytes(L, 0x38 + 8 * 32) is not { } lb) return list;
            int count = Math.Min(32, (int)lb[_abilCountOff]);
            uint mask = BinaryPrimitives.ReadUInt32LittleEndian(lb.AsSpan(_abilMaskOff));
            uint k1 = U32At(_abilityK1Rva), k2 = U32At(_abilityK2Rva);
            for (int i = 0; i < count; i++)
            {
                if ((mask >> i & 1) == 0) continue;
                ulong abil = ((ulong)(BinaryPrimitives.ReadUInt32LittleEndian(lb.AsSpan(0x3C + 8 * i)) - k2) << 32) | (~BinaryPrimitives.ReadUInt32LittleEndian(lb.AsSpan(0x38 + 8 * i)) + k1);
                if (IsUserPtr(abil)) list.Add(abil);
            }
            return list;
        }
    }
}
