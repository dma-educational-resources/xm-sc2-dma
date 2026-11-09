import { makePalette, type RadarClient, rgbHex } from "../radar";
import { ColorModeSelect } from "./ColorModeSelect";
import { DockCheckbox, DockSection } from "../ui/navUi";
import { useTick } from "../useTick";
import { DockPanel, type DockPanelProps } from "./DockPanel";

const TEAM_LABEL = { self: "You", ally: "Ally", enemy: "Enemy", "": "" } as const;

/** Players: everyone in the match - show or hide each one's units and camera, and jump to where they're looking. */
export function PlayersPanel({ settings: s, onSettingsChange: set, client, onCenterOn, ...p }: DockPanelProps & { client: RadarClient; onCenterOn: (x: number, y: number) => void }) {
  const tick = useTick(client, 500);
  const players = tick?.players ?? [];
  const pal = makePalette(tick, s.colorMode);
  const hidden = new Set(s.hiddenPlayers);
  const toggle = (id: number, show: boolean) =>
    set({ hiddenPlayers: show ? s.hiddenPlayers.filter((h) => h !== id) : [...s.hiddenPlayers.filter((h) => h !== id), id] });

  return (
    <DockPanel id="players" title="Players" {...p}>
      <DockSection title="Colours">
        <ColorModeSelect value={s.colorMode} onChange={(v) => set({ colorMode: v })} />
      </DockSection>
      <DockSection title="Shown On The Map">
        {players.length === 0 && <div className="text-text-faint text-[12px]">Shown during a match.</div>}
        {players.map((pl) => (
          <div key={pl.id} className="flex items-center gap-2.5 text-[12.5px]">
            <DockCheckbox checked={!hidden.has(pl.id)} onChange={(v) => toggle(pl.id, v)} />
            <span className="h-2 w-2 flex-shrink-0 rounded-full" style={{ background: rgbHex(pal.player(pl.id, pl.color)) }} />
            <span className="min-w-0 flex-1 overflow-hidden text-ellipsis whitespace-nowrap">{pl.name}</span>
            {pl.team && <span className="text-text-faint text-[11px]">{TEAM_LABEL[pl.team]}</span>}
            <button
              type="button"
              disabled={!pl.camera}
              onClick={() => {
                if (!pl.camera) return;
                let x = 0, y = 0;
                for (let i = 0; i < pl.camera.length; i += 2) {
                  x += pl.camera[i];
                  y += pl.camera[i + 1];
                }
                onCenterOn(x / (pl.camera.length / 2), y / (pl.camera.length / 2));
              }}
              title="Centre the map on where this player is looking"
              className="border-panel-border bg-input-bg rounded-[7px] border px-2 py-[3px] text-[11px] text-white hover:bg-white/[0.06] disabled:opacity-30"
            >
              View
            </button>
          </div>
        ))}
      </DockSection>
    </DockPanel>
  );
}
