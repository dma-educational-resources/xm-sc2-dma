import { useState } from "react";
import { loadPersisted, savePersisted } from "./ui/persist";
import type { Category, ColorMode } from "./radar";

/** Everything the viewer remembers between visits (localStorage, per browser). */
export type ViewSettings = {
  showTerrain: boolean;
  terrainAlpha: number;
  mapFrame: boolean;
  showCameras: boolean;
  ownCamera: boolean;
  camerasEnemiesOnly: boolean;
  /** Glide units and cameras between ticks instead of stepping. */
  smoothing: boolean;
  labels: boolean;
  /** Pixels per map unit at which unit names show. */
  labelZoom: number;
  markerScale: number;
  healthBars: "never" | "damaged" | "always";
  shields: boolean;
  progressRings: boolean;
  categories: Record<Category, boolean>;
  /** Player ids hidden from the map. */
  hiddenPlayers: number[];
  followSelf: boolean;
  showPlayersWidget: boolean;
  showEventsWidget: boolean;
  showClock: boolean;
  colorMode: ColorMode;
};

export const DEFAULT_SETTINGS: ViewSettings = {
  showTerrain: true,
  terrainAlpha: 0.85,
  mapFrame: true,
  showCameras: true,
  ownCamera: true,
  camerasEnemiesOnly: false,
  smoothing: true,
  labels: true,
  labelZoom: 9,
  markerScale: 1,
  healthBars: "damaged",
  shields: true,
  progressRings: true,
  categories: {
    Unit: true,
    Building: true,
    Worker: true,
    Hero: true,
    Critter: false,
    Scenery: false,
    Other: false,
    Effect: false,
    Resource: true,
  },
  hiddenPlayers: [],
  followSelf: false,
  showPlayersWidget: true,
  showEventsWidget: false,
  showClock: true,
  colorMode: "radar",
};

export function useViewSettings(): [ViewSettings, (patch: Partial<ViewSettings>) => void] {
  const [s, setS] = useState<ViewSettings>(() => {
    const loaded = loadPersisted("view", DEFAULT_SETTINGS);
    return { ...loaded, categories: { ...DEFAULT_SETTINGS.categories, ...loaded.categories } };
  });
  const update = (patch: Partial<ViewSettings>) =>
    setS((prev) => {
      const next = { ...prev, ...patch };
      savePersisted("view", next);
      return next;
    });
  return [s, update];
}
