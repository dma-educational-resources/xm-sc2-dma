import { useState } from "react";
import { type ColorMode, makePalette, type RadarClient, rgbHex } from "../radar";
import { DockSelect } from "../ui/navUi";
import { useTick } from "../useTick";
import { FloatingWidget } from "./FloatingWidget";

/** Live Events: every player's commands, newest first - the desktop radar's Live Events widget, from the same feed
 * (the replay the game client writes as it plays, so about a second behind). Right-clicks are left out. */
export function EventsWidget({ client, colorMode, onClose }: { client: RadarClient; colorMode: ColorMode; onClose: () => void }) {
  const tick = useTick(client, 500);
  const pal = makePalette(tick, colorMode);
  const [player, setPlayer] = useState("0");
  const players = tick?.players ?? [];
  const byId = new Map(players.map((p) => [p.id, p]));
  const events = (tick?.events ?? []).filter((e) => player === "0" || e.p === Number(player));

  return (
    <FloatingWidget id="eventsWidget" title="Live Events" defaultPos={{ x: Math.max(72, window.innerWidth - 396), y: Math.max(56, window.innerHeight - 360) }} width={380} onClose={onClose}>
      <div style={{ padding: "8px 12px", borderBottom: "1px solid #1e1e2c" }}>
        <DockSelect value={player} onChange={setPlayer} options={[{ value: "0", label: "Everyone" }, ...players.map((p) => ({ value: String(p.id), label: p.name }))]} />
      </div>
      {tick && !tick.events && <div style={{ color: "#6b6b85", fontSize: 12, padding: "10px 12px" }}>The live event feed isn't running (Track Events in the radar's settings).</div>}
      {tick?.events && events.length === 0 && <div style={{ color: "#6b6b85", fontSize: 12, padding: "10px 12px" }}>No commands yet.</div>}
      {events.map((e, i) => {
        const p = byId.get(e.p);
        return (
          <div key={i} style={{ display: "flex", gap: 8, padding: "4px 12px", fontSize: 11.5, borderTop: "1px solid #1e1e2c" }}>
            <span style={{ width: 30, color: "#5b5e6e", textAlign: "right", flexShrink: 0, fontVariantNumeric: "tabular-nums" }}>{e.ago < 60 ? `${Math.round(e.ago)}s` : `${Math.round(e.ago / 60)}m`}</span>
            <span style={{ color: p ? rgbHex(pal.player(p.id, p.color)) : "#8b8ba7", flexShrink: 0, maxWidth: 110, overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>{p?.name ?? `Player ${e.p}`}</span>
            <span style={{ color: "#c7cad1", minWidth: 0 }}>{e.text}</span>
          </div>
        );
      })}
    </FloatingWidget>
  );
}
