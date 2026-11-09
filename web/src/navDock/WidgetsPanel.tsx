import { DockSection, DockToggleRow } from "../ui/navUi";
import { DockPanel, type DockPanelProps } from "./DockPanel";

/** Widgets: the floating info windows over the map. */
export function WidgetsPanel({ settings: s, onSettingsChange: set, ...p }: DockPanelProps) {
  return (
    <DockPanel id="widgets" title="Widgets" {...p}>
      <DockSection title="Floating Widgets">
        <DockToggleRow label="Players" checked={s.showPlayersWidget} onChange={(v) => set({ showPlayersWidget: v })} title="Minerals, gas, supply, workers and army per player." />
        <DockToggleRow label="Live Events" checked={s.showEventsWidget} onChange={(v) => set({ showEventsWidget: v })} title="Every player's commands, newest first (the radar's live event feed)." />
        <DockToggleRow label="Game Clock" checked={s.showClock} onChange={(v) => set({ showClock: v })} />
      </DockSection>
    </DockPanel>
  );
}
