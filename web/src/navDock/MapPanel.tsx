import { DockRangeRow, DockSection, DockSmallButton, DockToggleRow } from "../ui/navUi";
import { DockPanel, type DockPanelProps } from "./DockPanel";

/** Map: the terrain picture, the playable-area frame, camera boxes and how the view moves. */
export function MapPanel({ settings: s, onSettingsChange: set, onResetView, ...p }: DockPanelProps & { onResetView: () => void }) {
  return (
    <DockPanel id="map" title="Map" {...p}>
      <DockSection title="View">
        <DockToggleRow label="Follow My Camera" checked={s.followSelf} onChange={(v) => set({ followSelf: v })} title="Keep the view centred on where you're looking in the game." />
        <DockToggleRow label="Smooth Movement" checked={s.smoothing} onChange={(v) => set({ smoothing: v })} title="Units and camera boxes glide between updates instead of stepping." />
        <DockSmallButton onClick={onResetView} full>
          Reset View
        </DockSmallButton>
        <div className="text-text-faint text-[11px]">Scroll to zoom, drag to pan, double-click to fit the map.</div>
      </DockSection>
      <DockSection title="Terrain">
        <DockToggleRow label="Show Terrain" checked={s.showTerrain} onChange={(v) => set({ showTerrain: v })} />
        <DockRangeRow
          label="Terrain Opacity"
          value={s.terrainAlpha}
          min={0.1}
          max={1}
          step={0.05}
          format={(v) => `${Math.round(v * 100)}%`}
          onChange={(v) => set({ terrainAlpha: v })}
          disabled={!s.showTerrain}
        />
        <DockToggleRow label="Map Frame" checked={s.mapFrame} onChange={(v) => set({ mapFrame: v })} title="Darken everything outside the playable area." />
      </DockSection>
      <DockSection title="Cameras">
        <DockToggleRow label="Show Cameras" checked={s.showCameras} onChange={(v) => set({ showCameras: v })} title="Where each player is looking: their screen, outlined in their colour." />
        <DockToggleRow label="My Own Camera" checked={s.ownCamera} onChange={(v) => set({ ownCamera: v })} disabled={!s.showCameras} />
        <DockToggleRow label="Enemies Only" checked={s.camerasEnemiesOnly} onChange={(v) => set({ camerasEnemiesOnly: v })} disabled={!s.showCameras} />
      </DockSection>
    </DockPanel>
  );
}
