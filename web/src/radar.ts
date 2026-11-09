// Wire types for the SC2 radar's /api/radar tick (src/Web/WebRadarServer.cs WriteTick) and the client that polls it.

export type RadarPlayer = {
  id: number;
  name: string;
  /** Has a lobby name (computer and map players don't). */
  human: boolean;
  /** 0xRRGGBB in the radar's current colour mode. */
  color: number;
  team: "self" | "ally" | "enemy" | "";
  army: number;
  workers: number;
  armyValue: number;
  minerals?: number;
  gas?: number;
  supply?: number;
  supplyCap?: number;
  /** Camera view polygon, flat [x0, y0, x1, y1, ...] in map units. */
  camera?: number[];
};

export type RadarEvent = { ago: number; p: number; text: string };

export type RadarMap = {
  name: string;
  w?: number;
  h?: number;
  /** [minX, minY, maxX, maxY] */
  playable?: [number, number, number, number];
  /** Changes when the terrain picture does; the picture is /api/terrain.png?k=<this>. */
  terrain?: string;
};

/** [x, y, rgb, owner, category, name index, hp 0..1 (-1 unknown), shields 0..1 (-1 none), flags, progress 0..1 (-1 none), id] */
export type RawUnit = [number, number, number, number, number, number, number, number, number, number, number];

export type RadarTick = {
  v: number;
  tickMs: number;
  inGame: boolean;
  status: string;
  time?: number;
  local?: number;
  /** The desktop radar's Alliance Colours setting (the page's colour mode can follow it). */
  allianceColors?: boolean;
  map?: RadarMap;
  players?: RadarPlayer[];
  units?: RawUnit[];
  names?: string[];
  events?: RadarEvent[];
};

/** UnitCategory in src/SC2/UnitDatabase.cs, same order. */
export const CATEGORY = ["Unit", "Building", "Worker", "Hero", "Critter", "Scenery", "Other", "Effect", "Resource"] as const;
export type Category = (typeof CATEGORY)[number];

export const FLAG_BURROWED = 1;
export const FLAG_CLOAKED = 2;
export const FLAG_DETECTOR = 4;
export const FLAG_WARPING = 8;
export const FLAG_BUILDING = 16;

export const U_X = 0, U_Y = 1, U_RGB = 2, U_OWNER = 3, U_CAT = 4, U_NAME = 5, U_HP = 6, U_SH = 7, U_FLAGS = 8, U_PROG = 9, U_ID = 10;

export const rgbHex = (rgb: number) => "#" + (rgb & 0xffffff).toString(16).padStart(6, "0");

export function formatClock(seconds: number): string {
  const s = Math.max(0, Math.floor(seconds));
  const h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), sec = s % 60;
  return h > 0 ? `${h}:${String(m).padStart(2, "0")}:${String(sec).padStart(2, "0")}` : `${m}:${String(sec).padStart(2, "0")}`;
}

export type ConnectionState = "connecting" | "live" | "lost";

/**
 * Polls /api/radar - the same stateless read the EFT radar's local hosting uses. Keeps the last two ticks with their
 * arrival times so the viewer can glide between them. A poll starts as soon as the previous one ends, no faster than
 * the server rebuilds the data (its tickMs).
 */
export class RadarClient {
  latest: RadarTick | null = null;
  previous: RadarTick | null = null;
  latestAt = 0;
  previousAt = 0;
  state: ConnectionState = "connecting";
  private stopped = false;
  private listeners = new Set<() => void>();
  private failures = 0;

  private baseUrl: string;

  constructor(baseUrl = "") {
    this.baseUrl = baseUrl;
  }

  start() {
    this.stopped = false;
    void this.loop();
  }

  stop() {
    this.stopped = true;
  }

  subscribe(fn: () => void): () => void {
    this.listeners.add(fn);
    return () => this.listeners.delete(fn);
  }

  private async loop() {
    while (!this.stopped) {
      const started = performance.now();
      try {
        const res = await fetch(`${this.baseUrl}/api/radar`, { cache: "no-store" });
        if (!res.ok) throw new Error(String(res.status));
        const tick = (await res.json()) as RadarTick;
        if (!this.latest || tick.v !== this.latest.v) {
          this.previous = this.latest;
          this.previousAt = this.latestAt;
          this.latest = tick;
          this.latestAt = performance.now();
        }
        this.failures = 0;
        this.state = "live";
      } catch {
        this.failures++;
        if (this.failures >= 3) this.state = "lost";
      }
      this.listeners.forEach((l) => l());
      const interval = this.state === "live" ? Math.max(25, this.latest?.tickMs ?? 100) : 1000;
      const wait = interval - (performance.now() - started);
      if (wait > 0) await new Promise((r) => setTimeout(r, wait));
    }
  }
}

/** "radar" follows the desktop radar's Alliance Colours setting. */
export type ColorMode = "radar" | "player" | "alliance";

/** The desktop radar's alliance colours (PlayerColors.Self / Ally / Enemy): you green, allies yellow, enemies red. */
export const TEAM_COLORS = { self: 0x00bb00, ally: 0xffff00, enemy: 0xff0000 } as const;

/** Colours for one tick in a colour mode: each player's own, or green / yellow / red by alliance. Neutral units and
 * players whose alliance isn't known keep their own colour. */
export function makePalette(tick: RadarTick | null, mode: ColorMode) {
  const alliance = mode === "alliance" || (mode === "radar" && !!tick?.allianceColors);
  const byId = new Map<number, number>();
  for (const p of tick?.players ?? []) byId.set(p.id, alliance && p.team ? TEAM_COLORS[p.team] : p.color);
  return {
    alliance,
    player: (id: number, fallback = 0x8b8ba7) => byId.get(id) ?? fallback,
    unit: (u: RawUnit) => (alliance && u[U_OWNER] > 0 ? (byId.get(u[U_OWNER]) ?? u[U_RGB]) : u[U_RGB]),
  };
}
export type Palette = ReturnType<typeof makePalette>;
