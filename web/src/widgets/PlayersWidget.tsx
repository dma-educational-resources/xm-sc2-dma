import { useState } from "react";
import { type ColorMode, makePalette, type RadarClient, type RadarPlayer, rgbHex } from "../radar";
import { useTick } from "../useTick";
import { FloatingWidget } from "./FloatingWidget";

type SortKey = "name" | "minerals" | "gas" | "supply" | "workers" | "army" | "armyValue";
type SortState = { key: SortKey; dir: "asc" | "desc" } | null;

const COLUMNS: { key: SortKey; label: string; width: number; title: string }[] = [
  { key: "minerals", label: "Min", width: 46, title: "Minerals" },
  { key: "gas", label: "Gas", width: 42, title: "Vespene gas" },
  { key: "supply", label: "Supply", width: 58, title: "Supply used / cap" },
  { key: "workers", label: "Wrk", width: 34, title: "Workers" },
  { key: "army", label: "Army", width: 38, title: "Army units" },
  { key: "armyValue", label: "Value", width: 50, title: "Army value (minerals + gas)" },
];

/** Players: minerals, gas, supply, workers and army per player, as the desktop radar's Resources widget. Click a
 * column to sort. */
export function PlayersWidget({ client, colorMode, onClose }: { client: RadarClient; colorMode: ColorMode; onClose: () => void }) {
  const tick = useTick(client, 500);
  const pal = makePalette(tick, colorMode);
  const [sort, setSort] = useState<SortState>(null);
  let players = [...(tick?.players ?? [])];
  if (sort) {
    const dir = sort.dir === "asc" ? 1 : -1;
    const val = (p: RadarPlayer) => (sort.key === "name" ? p.name : (p[sort.key] ?? -1));
    players.sort((a, b) => {
      const va = val(a), vb = val(b);
      return typeof va === "string" || typeof vb === "string" ? dir * String(va).localeCompare(String(vb)) : dir * (va - vb);
    });
  }
  const toggleSort = (key: SortKey) => setSort((prev) => (prev?.key === key ? { key, dir: prev.dir === "asc" ? "desc" : "asc" } : { key, dir: "desc" }));
  const head = (label: string, key: SortKey, title?: string) => (
    <span onClick={() => toggleSort(key)} title={title ?? "Click to sort"} style={{ cursor: "pointer", color: sort?.key === key ? "#e5e7eb" : "#8b8ba7", userSelect: "none" }}>
      {label}
      {sort?.key === key && (sort.dir === "asc" ? " ▲" : " ▼")}
    </span>
  );

  return (
    <FloatingWidget id="playersWidget" title="Players" count={players.length} defaultPos={{ x: Math.max(72, window.innerWidth - 416), y: 56 }} width={400} onClose={onClose}>
      <div style={{ display: "flex", gap: 8, padding: "4px 12px", fontSize: 10.5, borderBottom: "1px solid #1e1e2c" }}>
        <span style={{ flex: 1 }}>{head("Name", "name")}</span>
        {COLUMNS.map((c) => (
          <span key={c.key} style={{ width: c.width, textAlign: "right" }}>
            {head(c.label, c.key, c.title)}
          </span>
        ))}
      </div>
      {players.length === 0 && <div style={{ color: "#6b6b85", fontSize: 12, padding: "10px 12px" }}>No players</div>}
      {players.map((p) => {
        const capped = p.supply != null && p.supplyCap != null && p.supplyCap > 0 && p.supplyCap < 200 && p.supply >= p.supplyCap;
        return (
          <div key={p.id} style={{ display: "flex", alignItems: "center", gap: 8, padding: "6px 12px", borderTop: "1px solid #1e1e2c", fontSize: 11 }}>
            <span style={{ width: 8, height: 8, borderRadius: "50%", background: rgbHex(pal.player(p.id, p.color)), flexShrink: 0 }} />
            <div style={{ flex: 1, overflow: "hidden" }}>
              <div style={{ fontSize: 12, overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>{p.name}</div>
              <div style={{ fontSize: 10.5, color: "#8b8ba7" }}>{p.team === "self" ? "You" : p.team === "ally" ? "Ally" : p.team === "enemy" ? "Enemy" : p.human ? "Player" : "Computer"}</div>
            </div>
            <Num w={46} v={p.minerals} color="#7fb6ff" />
            <Num w={42} v={p.gas} color="#5fd38a" />
            <span style={{ width: 58, textAlign: "right", color: capped ? "#f2555a" : "#8b8ba7", fontVariantNumeric: "tabular-nums" }}>
              {p.supply != null ? `${Math.floor(p.supply)}/${Math.floor(p.supplyCap ?? 0)}` : "-"}
            </span>
            <Num w={34} v={p.workers} />
            <Num w={38} v={p.army} />
            <Num w={50} v={p.armyValue} />
          </div>
        );
      })}
    </FloatingWidget>
  );
}

function Num({ w, v, color = "#8b8ba7" }: { w: number; v?: number; color?: string }) {
  return <span style={{ width: w, textAlign: "right", color, fontVariantNumeric: "tabular-nums" }}>{v != null && v >= 0 ? v.toLocaleString() : "-"}</span>;
}
