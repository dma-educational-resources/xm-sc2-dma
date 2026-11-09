import { forwardRef, useEffect, useImperativeHandle, useRef, useState } from "react";
import {
  CATEGORY,
  FLAG_BURROWED,
  FLAG_CLOAKED,
  type RadarClient,
  type RawUnit,
  makePalette,
  rgbHex,
  U_CAT,
  U_FLAGS,
  U_HP,
  U_ID,
  U_NAME,
  U_OWNER,
  U_PROG,
  U_SH,
  U_X,
  U_Y,
} from "./radar";
import type { ViewSettings } from "./settings";

export type Viewer2DHandle = {
  /** Fit the playable area (else the whole map) into the canvas. */
  resetView: () => void;
  /** Centre the view on a map point, keeping the zoom. */
  centerOn: (x: number, y: number) => void;
};

type View = { cx: number; cy: number; scale: number };
type Hover = { x: number; y: number; lines: { text: string; color?: string }[] } | null;

const GLIDE_SECONDS = 0.06; // units and cameras ease to each new tick, most of the way within one
const JUMP = 8; // map units: further than this in one tick is a teleport (warp, load / unload), not a glide
const HIT_PX = 10;

/**
 * The 2D map: the radar's terrain picture, the playable-area frame, every player's camera box and the units - drawn
 * on a canvas every animation frame from the client's latest tick, with each unit gliding to where the tick puts it.
 * Wheel zooms around the cursor, drag pans, hover shows what's under the cursor.
 */
export const Viewer2D = forwardRef<Viewer2DHandle, { client: RadarClient; settings: ViewSettings }>(function Viewer2D({ client, settings }, ref) {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const wrapRef = useRef<HTMLDivElement>(null);
  const viewRef = useRef<View>({ cx: 128, cy: 128, scale: 3 });
  const settingsRef = useRef(settings);
  settingsRef.current = settings;
  const [hover, setHover] = useState<Hover>(null);
  const mouseRef = useRef<{ x: number; y: number } | null>(null);
  const fittedMapRef = useRef<string | null>(null);

  // terrain picture, reloaded when its key changes
  const terrainRef = useRef<{ key: string; img: HTMLImageElement; ready: boolean } | null>(null);

  const fit = () => {
    const tick = client.latest;
    const canvas = canvasRef.current;
    if (!tick?.map || !canvas) return;
    const m = tick.map;
    const [x0, y0, x1, y1] = m.playable ?? [0, 0, m.w ?? 256, m.h ?? 256];
    const w = canvas.clientWidth, h = canvas.clientHeight;
    viewRef.current = { cx: (x0 + x1) / 2, cy: (y0 + y1) / 2, scale: Math.max(0.5, Math.min(w / (x1 - x0), h / (y1 - y0)) * 0.95) };
  };

  useImperativeHandle(ref, () => ({
    resetView: fit,
    centerOn: (x, y) => {
      viewRef.current = { ...viewRef.current, cx: x, cy: y };
    },
  }));

  useEffect(() => {
    const canvas = canvasRef.current!;
    const ctx = canvas.getContext("2d")!;
    let raf = 0;
    let last = performance.now();
    const shown = new Map<number, { x: number; y: number }>();
    const camShown = new Map<number, number[]>();
    let hits: { sx: number; sy: number; u: RawUnit }[] = [];

    const frame = (now: number) => {
      raf = requestAnimationFrame(frame);
      const dt = Math.min(0.1, (now - last) / 1000);
      last = now;
      const s = settingsRef.current;
      const dpr = window.devicePixelRatio || 1;
      const W = canvas.clientWidth, H = canvas.clientHeight;
      if (canvas.width !== Math.round(W * dpr) || canvas.height !== Math.round(H * dpr)) {
        canvas.width = Math.round(W * dpr);
        canvas.height = Math.round(H * dpr);
      }
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      ctx.fillStyle = "#101116";
      ctx.fillRect(0, 0, W, H);

      const tick = client.latest;
      if (!tick || !tick.inGame || !tick.map) {
        shown.clear();
        camShown.clear();
        hits = [];
        return;
      }
      const map = tick.map;
      if (fittedMapRef.current !== map.name + (map.terrain ?? "")) {
        fittedMapRef.current = map.name + (map.terrain ?? "");
        fit();
        shown.clear();
      }

      // follow your own camera
      const local = tick.players?.find((p) => p.team === "self" || p.id === tick.local);
      if (s.followSelf && local?.camera) {
        const c = centroid(local.camera);
        const v = viewRef.current;
        const k = 1 - Math.exp(-dt / 0.15);
        v.cx += (c[0] - v.cx) * k;
        v.cy += (c[1] - v.cy) * k;
      }

      const v = viewRef.current;
      const sx = (x: number) => W / 2 + (x - v.cx) * v.scale;
      const sy = (y: number) => H / 2 - (y - v.cy) * v.scale;
      const glide = s.smoothing ? 1 - Math.exp(-dt / GLIDE_SECONDS) : 1;

      // terrain
      if (map.terrain && terrainRef.current?.key !== map.terrain) {
        const img = new Image();
        const entry = { key: map.terrain, img, ready: false };
        img.onload = () => (entry.ready = true);
        img.src = `./api/terrain.png?k=${encodeURIComponent(map.terrain)}`;
        terrainRef.current = entry;
      }
      if (s.showTerrain && map.w && map.h && terrainRef.current?.ready) {
        ctx.globalAlpha = s.terrainAlpha;
        ctx.imageSmoothingEnabled = v.scale < 4;
        ctx.drawImage(terrainRef.current.img, sx(0), sy(map.h), map.w * v.scale, map.h * v.scale);
        ctx.globalAlpha = 1;
      }
      if (map.w && map.h) {
        ctx.strokeStyle = "rgba(255,255,255,.12)";
        ctx.lineWidth = 1;
        ctx.strokeRect(sx(0), sy(map.h), map.w * v.scale, map.h * v.scale);
      }

      // playable-area frame: dark outside, a thin border and corner ticks
      if (s.mapFrame && map.playable) {
        const [x0, y0, x1, y1] = map.playable;
        const l = sx(x0), t = sy(y1), r = sx(x1), b = sy(y0);
        ctx.fillStyle = "rgba(0,0,0,.6)";
        ctx.fillRect(0, 0, W, Math.max(0, t));
        ctx.fillRect(0, b, W, H - b);
        ctx.fillRect(0, t, Math.max(0, l), b - t);
        ctx.fillRect(r, t, W - r, b - t);
        ctx.strokeStyle = "rgba(255,255,255,.45)";
        ctx.strokeRect(l + 0.5, t + 0.5, r - l, b - t);
        const tk = Math.min(16, (r - l) * 0.25, (b - t) * 0.25);
        ctx.strokeStyle = "rgba(255,255,255,.8)";
        ctx.lineWidth = 2;
        ctx.beginPath();
        for (const [px, py, dx, dy] of [[l, t, 1, 1], [r, t, -1, 1], [l, b, 1, -1], [r, b, -1, -1]]) {
          ctx.moveTo(px + dx * tk, py);
          ctx.lineTo(px, py);
          ctx.lineTo(px, py + dy * tk);
        }
        ctx.stroke();
        ctx.lineWidth = 1;
      }

      // camera boxes
      const players = tick.players ?? [];
      const pal = makePalette(tick, s.colorMode);
      const hidden = new Set(s.hiddenPlayers);
      if (s.showCameras) {
        ctx.font = "600 11px Inter, 'Segoe UI', system-ui, sans-serif";
        for (const p of players) {
          if (!p.camera || hidden.has(p.id)) continue;
          if (p.team === "self" && !s.ownCamera) continue;
          if (s.camerasEnemiesOnly && (p.team === "ally" || p.team === "self")) continue;
          let pts = p.camera;
          const prev = camShown.get(p.id);
          if (prev && prev.length === pts.length && s.smoothing && Math.hypot(prev[0] - pts[0], prev[1] - pts[1]) < 30)
            pts = prev.map((q, i) => q + (p.camera![i] - q) * glide);
          camShown.set(p.id, pts);
          const col = rgbHex(pal.player(p.id, p.color));
          ctx.beginPath();
          for (let i = 0; i < pts.length; i += 2) (i === 0 ? ctx.moveTo : ctx.lineTo).call(ctx, sx(pts[i]), sy(pts[i + 1]));
          ctx.closePath();
          ctx.fillStyle = col + "18";
          ctx.fill();
          ctx.strokeStyle = col + "c0";
          ctx.lineWidth = 1.5;
          ctx.stroke();
          ctx.lineWidth = 1;
          let top = Infinity, minX = Infinity, maxX = -Infinity;
          for (let i = 0; i < pts.length; i += 2) {
            top = Math.min(top, sy(pts[i + 1]));
            minX = Math.min(minX, sx(pts[i]));
            maxX = Math.max(maxX, sx(pts[i]));
          }
          outlinedText(ctx, p.name, (minX + maxX) / 2, top - 4, col, "center");
        }
      }

      // units
      const units = tick.units ?? [];
      const names = tick.names ?? [];
      const seen = new Set<number>();
      const markers: { x: number; y: number; r: number; u: RawUnit }[] = [];
      const sizeMul = s.markerScale * Math.max(0.6, Math.min(1.6, v.scale / 5));
      for (const u of units) {
        const cat = CATEGORY[u[U_CAT]] ?? "Other";
        if (!s.categories[cat]) continue;
        if (u[U_OWNER] > 0 && hidden.has(u[U_OWNER])) continue;
        const id = u[U_ID];
        seen.add(id);
        let pos = shown.get(id);
        if (!pos || Math.hypot(pos.x - u[U_X], pos.y - u[U_Y]) > JUMP) pos = { x: u[U_X], y: u[U_Y] };
        else {
          pos.x += (u[U_X] - pos.x) * glide;
          pos.y += (u[U_Y] - pos.y) * glide;
        }
        shown.set(id, pos);
        const x = sx(pos.x), y = sy(pos.y);
        if (x < -30 || y < -30 || x > W + 30 || y > H + 30) continue;
        markers.push({ x, y, r: baseRadius(cat) * sizeMul, u });
      }
      for (const id of shown.keys()) if (!seen.has(id)) shown.delete(id);

      // big things first, so units sit on top of buildings and resources
      markers.sort((a, b) => order(b.u[U_CAT]) - order(a.u[U_CAT]));
      for (const m of markers) drawMarker(ctx, m.x, m.y, m.r, m.u, rgbHex(pal.unit(m.u)), s, v.scale);

      // labels, once zoomed in: one per spot (a coarse grid keeps them from piling up)
      if (s.labels && v.scale >= s.labelZoom) {
        ctx.font = "500 11px Inter, 'Segoe UI', system-ui, sans-serif";
        const taken = new Set<string>();
        for (let i = markers.length - 1; i >= 0; i--) {
          const m = markers[i];
          const cat = CATEGORY[m.u[U_CAT]];
          if (cat === "Resource" || cat === "Critter" || cat === "Scenery" || cat === "Effect") continue;
          const key = `${Math.round(m.x / 60)}:${Math.round(m.y / 16)}`;
          if (taken.has(key)) continue;
          taken.add(key);
          const flags = m.u[U_FLAGS];
          const name = (names[m.u[U_NAME]] ?? "") + (flags & FLAG_BURROWED ? " (burrowed)" : flags & FLAG_CLOAKED ? " (cloaked)" : "");
          outlinedText(ctx, name, m.x, m.y - m.r - 5, rgbHex(pal.unit(m.u)), "center");
        }
      }

      hits = markers.map((m) => ({ sx: m.x, sy: m.y, u: m.u }));

      // hover: what's under the cursor, grouped by name and owner
      const mouse = mouseRef.current;
      if (mouse) {
        const under = hits.filter((h) => (h.sx - mouse.x) ** 2 + (h.sy - mouse.y) ** 2 <= HIT_PX * HIT_PX);
        if (under.length > 0) {
          const groups = new Map<string, { u: RawUnit; n: number }>();
          for (const h of under) {
            const key = `${h.u[U_NAME]}:${h.u[U_OWNER]}`;
            const g = groups.get(key);
            if (g) g.n++;
            else groups.set(key, { u: h.u, n: 1 });
          }
          const lines = [...groups.values()].slice(0, 8).map(({ u, n }) => {
            const owner = players.find((p) => p.id === u[U_OWNER]);
            const hp = u[U_HP] >= 0 ? ` · ${Math.round(u[U_HP] * 100)}% HP` : "";
            const sh = u[U_SH] >= 0 ? ` · ${Math.round(u[U_SH] * 100)}% shields` : "";
            return {
              text: `${n > 1 ? `${n}× ` : ""}${names[u[U_NAME]] ?? "?"}${owner ? ` — ${owner.name}` : ""}${n === 1 ? hp + sh : ""}`,
              color: rgbHex(pal.unit(u)),
            };
          });
          setHoverIfChanged({ x: mouse.x, y: mouse.y, lines });
        } else setHoverIfChanged(null);
      }
    };

    let lastHoverKey = "";
    const setHoverIfChanged = (h: Hover) => {
      const key = h ? `${Math.round(h.x)},${Math.round(h.y)}|${h.lines.map((l) => l.text).join("|")}` : "";
      if (key === lastHoverKey) return;
      lastHoverKey = key;
      setHover(h);
    };

    raf = requestAnimationFrame(frame);
    return () => cancelAnimationFrame(raf);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [client]);

  // pan (drag) and zoom (wheel, around the cursor)
  useEffect(() => {
    const canvas = canvasRef.current!;
    let drag: { x: number; y: number; cx: number; cy: number } | null = null;
    const onDown = (e: PointerEvent) => {
      if (e.button !== 0) return;
      canvas.setPointerCapture(e.pointerId);
      drag = { x: e.clientX, y: e.clientY, cx: viewRef.current.cx, cy: viewRef.current.cy };
    };
    const onMove = (e: PointerEvent) => {
      const r = canvas.getBoundingClientRect();
      mouseRef.current = { x: e.clientX - r.left, y: e.clientY - r.top };
      if (!drag) return;
      const v = viewRef.current;
      v.cx = drag.cx - (e.clientX - drag.x) / v.scale;
      v.cy = drag.cy + (e.clientY - drag.y) / v.scale;
    };
    const onUp = () => (drag = null);
    const onLeave = () => {
      mouseRef.current = null;
      setHover(null);
    };
    const onWheel = (e: WheelEvent) => {
      e.preventDefault();
      const r = canvas.getBoundingClientRect();
      const mx = e.clientX - r.left, my = e.clientY - r.top;
      const v = viewRef.current;
      const W = canvas.clientWidth, H = canvas.clientHeight;
      const wx = v.cx + (mx - W / 2) / v.scale, wy = v.cy - (my - H / 2) / v.scale;
      const scale = Math.max(0.5, Math.min(60, v.scale * Math.exp(-e.deltaY * 0.0015)));
      v.scale = scale;
      v.cx = wx - (mx - W / 2) / scale;
      v.cy = wy + (my - H / 2) / scale;
    };
    canvas.addEventListener("pointerdown", onDown);
    canvas.addEventListener("pointermove", onMove);
    canvas.addEventListener("pointerup", onUp);
    canvas.addEventListener("pointerleave", onLeave);
    canvas.addEventListener("wheel", onWheel, { passive: false });
    canvas.addEventListener("dblclick", fit);
    return () => {
      canvas.removeEventListener("pointerdown", onDown);
      canvas.removeEventListener("pointermove", onMove);
      canvas.removeEventListener("pointerup", onUp);
      canvas.removeEventListener("pointerleave", onLeave);
      canvas.removeEventListener("wheel", onWheel);
      canvas.removeEventListener("dblclick", fit);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  return (
    <div ref={wrapRef} style={{ position: "relative", flex: 1, minWidth: 0, height: "100%", overflow: "hidden" }}>
      <canvas ref={canvasRef} style={{ width: "100%", height: "100%", display: "block", cursor: "grab", touchAction: "none" }} />
      {hover && (
        <div
          style={{
            position: "absolute",
            left: Math.min(hover.x + 14, (wrapRef.current?.clientWidth ?? 9999) - 260),
            top: hover.y + 14,
            pointerEvents: "none",
            background: "#15161dee",
            border: "1px solid #2a2c3a",
            borderRadius: 9,
            padding: "6px 9px",
            fontFamily: "'Inter', 'Segoe UI', system-ui, sans-serif",
            fontSize: 11.5,
            color: "#e7e9ee",
            boxShadow: "0 10px 30px rgba(0,0,0,.5)",
            maxWidth: 260,
            zIndex: 10,
          }}
        >
          {hover.lines.map((l, i) => (
            <div key={i} style={{ display: "flex", alignItems: "center", gap: 6, whiteSpace: "nowrap", overflow: "hidden", textOverflow: "ellipsis" }}>
              <span style={{ width: 7, height: 7, borderRadius: 999, background: l.color, flexShrink: 0 }} />
              {l.text}
            </div>
          ))}
        </div>
      )}
    </div>
  );
});

function centroid(pts: number[]): [number, number] {
  let x = 0, y = 0;
  const n = pts.length / 2;
  for (let i = 0; i < pts.length; i += 2) {
    x += pts[i];
    y += pts[i + 1];
  }
  return [x / n, y / n];
}

/** Marker radius in pixels at scale 5 by category. */
function baseRadius(cat: string): number {
  switch (cat) {
    case "Building": return 5.5;
    case "Hero": return 5;
    case "Unit": return 3.6;
    case "Worker": return 2.8;
    case "Resource": return 3;
    default: return 2;
  }
}

/** Draw order: higher first (under). */
function order(cat: number): number {
  switch (CATEGORY[cat]) {
    case "Resource": return 5;
    case "Building": return 4;
    case "Scenery": case "Other": case "Effect": case "Critter": return 3;
    case "Worker": return 2;
    case "Hero": return 0;
    default: return 1;
  }
}

function drawMarker(ctx: CanvasRenderingContext2D, x: number, y: number, r: number, u: RawUnit, col: string, s: ViewSettings, scale: number) {
  const cat = CATEGORY[u[U_CAT]];
  const flags = u[U_FLAGS];
  const fancy = scale >= 4;
  ctx.beginPath();
  if (cat === "Building") ctx.rect(x - r, y - r, r * 2, r * 2);
  else if (cat === "Hero" || cat === "Resource") {
    ctx.moveTo(x, y - r * 1.2);
    ctx.lineTo(x + r * 1.2, y);
    ctx.lineTo(x, y + r * 1.2);
    ctx.lineTo(x - r * 1.2, y);
    ctx.closePath();
  } else ctx.arc(x, y, r, 0, Math.PI * 2);

  if (flags & FLAG_BURROWED) {
    ctx.strokeStyle = col;
    ctx.lineWidth = 1.6;
    ctx.stroke();
    ctx.lineWidth = 1;
  } else {
    ctx.globalAlpha = flags & FLAG_CLOAKED ? 0.45 : 1;
    ctx.fillStyle = col;
    ctx.fill();
    ctx.globalAlpha = 1;
    if (fancy && cat !== "Resource") {
      ctx.strokeStyle = "rgba(0,0,0,.7)";
      ctx.stroke();
    }
    if (flags & FLAG_CLOAKED) {
      ctx.beginPath();
      ctx.arc(x, y, r + 2.5, 0, Math.PI * 2);
      ctx.strokeStyle = "rgba(255,255,255,.8)";
      ctx.stroke();
    }
  }

  // health and shields under the marker
  if (fancy && cat !== "Resource") {
    const hp = u[U_HP], sh = u[U_SH];
    const hurt = (hp >= 0 && hp < 0.995) || (sh >= 0 && sh < 0.995);
    const want = s.healthBars === "always" || (s.healthBars === "damaged" && hurt);
    if (want) {
      const bw = Math.max(10, r * 2.6), bh = Math.max(2, r * 0.42);
      let by = y + r + 3;
      if (s.shields && sh >= 0) {
        bar(ctx, x - bw / 2, by, bw, bh, sh, "#50a0ff");
        by += bh + 2;
      }
      if (hp >= 0) bar(ctx, x - bw / 2, by, bw, bh, hp, hp > 0.6 ? "#60d040" : hp > 0.3 ? "#e0c830" : "#e04040");
    }
  }

  // morph / production in progress: a ring filling clockwise from the top
  const prog = u[U_PROG];
  if (s.progressRings && prog >= 0 && fancy) {
    const pr = r * (cat === "Building" ? 1.3 : 1) + 4;
    ctx.beginPath();
    ctx.arc(x, y, pr, 0, Math.PI * 2);
    ctx.strokeStyle = "rgba(0,0,0,.4)";
    ctx.lineWidth = 3.5;
    ctx.stroke();
    ctx.beginPath();
    ctx.arc(x, y, pr, -Math.PI / 2, -Math.PI / 2 + Math.PI * 2 * Math.min(1, prog));
    ctx.strokeStyle = "#40d0ff";
    ctx.lineWidth = 2.5;
    ctx.stroke();
    ctx.lineWidth = 1;
  }
}

function bar(ctx: CanvasRenderingContext2D, x: number, y: number, w: number, h: number, frac: number, color: string) {
  ctx.fillStyle = "rgba(0,0,0,.65)";
  ctx.fillRect(x - 1, y - 1, w + 2, h + 2);
  ctx.fillStyle = color;
  ctx.fillRect(x, y, w * Math.max(0, Math.min(1, frac)), h);
}

function outlinedText(ctx: CanvasRenderingContext2D, text: string, x: number, y: number, color: string, align: CanvasTextAlign) {
  if (!text) return;
  ctx.textAlign = align;
  ctx.textBaseline = "bottom";
  ctx.lineWidth = 3;
  ctx.strokeStyle = "rgba(0,0,0,.85)";
  ctx.strokeText(text, x, y);
  ctx.fillStyle = color;
  ctx.fillText(text, x, y);
  ctx.lineWidth = 1;
}
