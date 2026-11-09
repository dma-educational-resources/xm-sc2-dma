import { useState } from "react";
import { CATEGORY, type Category } from "../radar";
import { DockRangeRow, DockSelect, DockTabs, DockToggleRow } from "../ui/navUi";
import { DockPanel, type DockPanelProps } from "./DockPanel";
import { ColorModeSelect } from "./ColorModeSelect";

const CATEGORY_LABELS: Record<Category, string> = {
  Unit: "Army Units",
  Building: "Buildings",
  Worker: "Workers",
  Hero: "Heroes",
  Critter: "Critters",
  Scenery: "Scenery",
  Other: "Other Actors",
  Effect: "Effects",
  Resource: "Resources",
};

/** Units: which kinds are drawn, and how (markers, labels, health, progress). */
export function UnitsPanel({ settings: s, onSettingsChange: set, ...p }: DockPanelProps) {
  const [tab, setTab] = useState(0);
  return (
    <DockPanel id="units" title="Units" {...p}>
      <DockTabs tabs={["Show", "Markers"]} active={tab} onChange={setTab}>
        {tab === 0 &&
          CATEGORY.map((c) => (
            <DockToggleRow key={c} label={CATEGORY_LABELS[c]} checked={s.categories[c]} onChange={(v) => set({ categories: { ...s.categories, [c]: v } })} />
          ))}
        {tab === 1 && (
          <>
            <ColorModeSelect value={s.colorMode} onChange={(v) => set({ colorMode: v })} />
            <DockRangeRow label="Marker Scale" value={s.markerScale} min={0.5} max={2.5} step={0.05} format={(v) => `${v.toFixed(2)}×`} onChange={(v) => set({ markerScale: v })} />
            <DockToggleRow label="Unit Names" checked={s.labels} onChange={(v) => set({ labels: v })} />
            <DockRangeRow
              label="Names From Zoom"
              value={s.labelZoom}
              min={2}
              max={30}
              step={1}
              format={(v) => `${v} px/cell`}
              onChange={(v) => set({ labelZoom: v })}
              disabled={!s.labels}
              title="Names show once a map cell is at least this many pixels wide."
            />
            <div>
              <div className="mb-1 text-xs text-[#c7cad1]">Health Bars</div>
              <DockSelect
                value={s.healthBars}
                onChange={(v) => set({ healthBars: v as typeof s.healthBars })}
                options={[
                  { value: "never", label: "Never" },
                  { value: "damaged", label: "When Damaged" },
                  { value: "always", label: "Always" },
                ]}
              />
            </div>
            <DockToggleRow label="Shield Bars" checked={s.shields} onChange={(v) => set({ shields: v })} disabled={s.healthBars === "never"} />
            <DockToggleRow label="Progress Rings" checked={s.progressRings} onChange={(v) => set({ progressRings: v })} title="Morphs, production and research in progress." />
          </>
        )}
      </DockTabs>
    </DockPanel>
  );
}
