import { useState } from "react";
import type { RadarClient } from "../radar";
import type { ViewSettings } from "../settings";
import { loadPersisted, savePersisted } from "../ui/persist";
import { cascadePosition } from "../ui/useDraggablePanel";
import { MapPanel } from "./MapPanel";
import { UnitsPanel } from "./UnitsPanel";
import { PlayersPanel } from "./PlayersPanel";
import { WidgetsPanel } from "./WidgetsPanel";

const DOCK_WIDTH = 56;
// first-open placement, offset from the rail (each panel remembers where it was dragged after that)
const ANCHOR = { x: DOCK_WIDTH + 16, y: 16 };

// Named after the desktop radar's own panels and settings tabs, so someone who knows those finds the same thing here.
const PANELS = [
  { key: "map", label: "MAP", title: "Map" },
  { key: "units", label: "UNIT", title: "Units" },
  { key: "players", label: "PLR", title: "Players" },
  { key: "widgets", label: "WDG", title: "Widgets" },
] as const;
type PanelKey = (typeof PANELS)[number]["key"];

function usePersistedOpen(key: PanelKey, fallback: boolean): [boolean, (v: boolean) => void] {
  const [open, setOpenState] = useState(() => loadPersisted(`dockOpen_${key}`, fallback));
  const setOpen = (v: boolean) => {
    setOpenState(v);
    savePersisted(`dockOpen_${key}`, v);
  };
  return [open, setOpen];
}

/** The slim icon rail of the EFT web radar: each button toggles its own floating, draggable settings panel. */
export function NavDock(props: {
  client: RadarClient;
  settings: ViewSettings;
  onSettingsChange: (patch: Partial<ViewSettings>) => void;
  onResetView: () => void;
  onCenterOn: (x: number, y: number) => void;
}) {
  const open: Record<PanelKey, [boolean, (v: boolean) => void]> = {
    map: usePersistedOpen("map", true),
    units: usePersistedOpen("units", false),
    players: usePersistedOpen("players", false),
    widgets: usePersistedOpen("widgets", false),
  };
  const pos = (i: number) => cascadePosition(ANCHOR, i);
  const common = { settings: props.settings, onSettingsChange: props.onSettingsChange };

  return (
    <>
      <div
        className="bg-panel border-panel-border flex flex-shrink-0 flex-col items-center gap-1.5 border-r pt-3"
        style={{ width: DOCK_WIDTH, fontFamily: "'Inter', 'Segoe UI', system-ui, sans-serif" }}
      >
        <div className="border-panel-border mb-1.5 flex w-10 flex-shrink-0 flex-col items-center border-b pb-2">
          <div className="bg-accent w-full rounded-md py-1.5 text-center text-[10px] font-bold tracking-wide text-white">SC2</div>
        </div>
        {PANELS.map((p) => {
          const [isOpen, setOpen] = open[p.key];
          return (
            <button
              key={p.key}
              onClick={() => setOpen(!isOpen)}
              title={p.title}
              className={`relative flex h-10 w-10 flex-shrink-0 items-center justify-center rounded-md border-0 text-[10px] font-bold tracking-wide transition-colors ${
                isOpen ? "bg-accent/15 text-accent" : "bg-transparent text-text-dim hover:bg-white/5 hover:text-[#c7cad1]"
              }`}
            >
              {isOpen && <span className="bg-accent absolute inset-y-0 left-0 w-[3px] rounded-l-md" />}
              {p.label}
            </button>
          );
        })}
        <div className="flex-1" />
      </div>

      {open.map[0] && <MapPanel defaultPos={pos(0)} onClose={() => open.map[1](false)} onResetView={props.onResetView} {...common} />}
      {open.units[0] && <UnitsPanel defaultPos={pos(1)} onClose={() => open.units[1](false)} {...common} />}
      {open.players[0] && (
        <PlayersPanel defaultPos={pos(2)} onClose={() => open.players[1](false)} client={props.client} onCenterOn={props.onCenterOn} {...common} />
      )}
      {open.widgets[0] && <WidgetsPanel defaultPos={pos(3)} onClose={() => open.widgets[1](false)} {...common} />}
    </>
  );
}
