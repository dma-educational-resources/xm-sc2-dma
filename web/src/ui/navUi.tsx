import { type CSSProperties, type PointerEvent as ReactPointerEvent, type ReactNode, type RefObject, useEffect, useRef, useState } from "react";

/** Shared visual primitives for the sidebar (Navbar.tsx) and its settings-block consumers
 * (EntityVisualSettings.tsx, WatchlistPanel.tsx, BattleModePanel.tsx) -- pulled out once these
 * needed to look consistent across files that each already had their own local `inputStyle`/
 * color constants rather than force those files onto one shared palette (a bigger, separate
 * change from what this file is actually fixing: native-look combo boxes and the checkbox-then-
 * a-whole-separate-labeled-color-row pattern the C# app's own Colors tab doesn't have). */

// Inline SVG chevron (down caret) as a data-URI background-image -- the standard way to replace a
// <select>'s native OS-drawn arrow (which `appearance: none` removes entirely) with one that
// actually matches the rest of this dark theme instead of whatever the OS/browser draws.
const CHEVRON_SVG =
  "url(\"data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='10' height='6' viewBox='0 0 10 6'%3E%3Cpath d='M1 1l4 4 4-4' stroke='%238b8ba7' stroke-width='1.6' fill='none' stroke-linecap='round' stroke-linejoin='round'/%3E%3C/svg%3E\")";

/** Spread onto a <select>'s own base input style (e.g. `{ ...inputStyle, ...SELECT_CHEVRON }`) --
 * never used standalone since a <select> still needs its base background/border/padding from
 * whichever file's own `inputStyle` it already had. */
export const SELECT_CHEVRON: CSSProperties = {
  appearance: "none",
  WebkitAppearance: "none",
  MozAppearance: "none",
  backgroundImage: CHEVRON_SVG,
  backgroundRepeat: "no-repeat",
  backgroundPosition: "right 10px center",
  paddingRight: 28,
  cursor: "pointer",
};

/** One row: checkbox + label + an inline color swatch at the far right -- replaces the old
 * pattern of a Toggle row immediately followed by a whole separate ColorField row underneath
 * repeating the same name ("meds" then "meds color"), which the C# app's own Colors tab doesn't
 * do either (there it's one row per category: enabled checkbox, label, color, done). The color
 * swatch is a sibling of the <label>, not nested inside it -- a native <label> wrapping two
 * separate form controls would risk the browser's "click passes through to the first control"
 * behavior firing on a color-swatch click too, so this sidesteps that entirely rather than
 * relying on browsers handling it the way testing suggested they would. */
export function ToggleColorRow({
  label,
  checked,
  onChange,
  color,
  onColorChange,
  disabled,
}: {
  label: string;
  checked: boolean;
  onChange: (v: boolean) => void;
  color: string;
  onColorChange: (v: string) => void;
  disabled?: boolean;
}) {
  return (
    <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
      <label
        style={{
          display: "flex",
          alignItems: "center",
          gap: 8,
          flex: 1,
          minWidth: 0,
          fontSize: 12.5,
          color: "#e5e7eb",
          cursor: disabled ? "default" : "pointer",
          opacity: disabled ? 0.45 : 1,
        }}
      >
        <DockCheckbox checked={checked} onChange={onChange} disabled={disabled} />
        <span style={{ overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>{label}</span>
      </label>
      <input
        type="color"
        value={color}
        disabled={disabled}
        onChange={(e) => onColorChange(e.target.value)}
        style={{
          width: 24,
          height: 24,
          padding: 0,
          border: "1px solid #2c2c42",
          borderRadius: 5,
          background: "none",
          cursor: disabled ? "default" : "pointer",
          flexShrink: 0,
          opacity: disabled ? 0.45 : 1,
        }}
      />
    </div>
  );
}

/* ── Tailwind-class primitives for the navDock/ panel redesign ──────────────────────────────
 * Below here: a second, Tailwind-based generation of the same ideas as the plain-CSSProperties
 * primitives above, built specifically for the new pop-out panels (src/navDock/*). Deliberately
 * NOT retrofitted onto the pre-existing consumers of this file (EntityVisualSettings.tsx,
 * WatchlistPanel.tsx, TextSlotChipEditor.tsx) -- those keep using SELECT_CHEVRON/ToggleColorRow
 * above unchanged, this is presentation-layer-only for the new dock. More breathing room
 * (bigger gaps/padding) than the old Navbar.tsx sections is the whole point -- "squished
 * together" was the user's core complaint about the pre-Tailwind sidebar. */

const DOCK_SECTION_FOLD_PREFIX = "xmsc2web-dock-section-open-";
function loadDockSectionOpen(key: string, fallback: boolean): boolean {
  try {
    const raw = localStorage.getItem(DOCK_SECTION_FOLD_PREFIX + key);
    return raw === null ? fallback : raw === "true";
  } catch {
    return fallback;
  }
}

/* ── Shared popover positioning + single-open-at-a-time ──────────────────────────────────────
 * Every panel body renders inside HudPanel's own `overflow-y: auto` scroll region (see
 * HudPanel.tsx's maxHeight prop) -- a popover positioned with ordinary `position: absolute`
 * relative to its trigger gets silently clipped at that scroll region's edge the moment the
 * trigger is anywhere near the bottom of a tall panel. Confirmed as a real bug in an early
 * design pass (a color picker's own contents overflowing its own card). `position: fixed`,
 * computed from the trigger's live getBoundingClientRect() and re-measured on scroll/resize,
 * sidesteps that entirely since it's positioned against the viewport, not the scroll ancestor --
 * safe here because nothing between a trigger and <body> sets a CSS `transform` (which would
 * otherwise create a new containing block for fixed descendants). */
function usePopoverPosition(open: boolean, anchorRef: RefObject<HTMLElement | null>, opts: { width?: number; height: number }) {
  const [pos, setPos] = useState({ top: 0, left: 0, width: opts.width ?? 200 });
  useEffect(() => {
    if (!open || !anchorRef.current) return;
    const update = () => {
      const r = anchorRef.current!.getBoundingClientRect();
      const w = opts.width ?? r.width;
      setPos({
        top: Math.min(window.innerHeight - opts.height - 8, r.bottom + 6),
        left: Math.min(window.innerWidth - w - 8, Math.max(8, r.left)),
        width: w,
      });
    };
    update();
    window.addEventListener("scroll", update, true);
    window.addEventListener("resize", update);
    return () => {
      window.removeEventListener("scroll", update, true);
      window.removeEventListener("resize", update);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, opts.width, opts.height]);
  return pos;
}

// Only one color/select popover open at once, app-wide -- a plain module-level singleton (not
// context/state) since it's a pure "close whoever else is open" side channel, not real app state.
let closeActivePopover: (() => void) | null = null;
function claimPopover(close: () => void) {
  if (closeActivePopover && closeActivePopover !== close) closeActivePopover();
  closeActivePopover = close;
}
function releasePopover(close: () => void) {
  if (closeActivePopover === close) closeActivePopover = null;
}

/** Closes `close` on any pointerdown outside `refs`, or on Escape, while `open` is true. Shared
 * by DockColorRow's picker and DockSelect's menu. */
function useCloseOnOutside(open: boolean, refs: RefObject<HTMLElement | null>[], close: () => void) {
  useEffect(() => {
    if (!open) return;
    const onDown = (e: PointerEvent) => {
      const t = e.target as Node;
      if (refs.some((r) => r.current?.contains(t))) return;
      close();
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") close();
    };
    document.addEventListener("pointerdown", onDown);
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("pointerdown", onDown);
      document.removeEventListener("keydown", onKey);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);
}

/** Small chevron, shared by DockSection and DockSelect -- same curve as SELECT_CHEVRON's data-URI
 * above (the native-select fallback still used on the handful of 3D-only selects), so custom and
 * native controls read as one consistent visual language rather than two different arrow styles.
 * Deliberately takes no `open`/rotation logic of its own -- DockSection (points sideways when
 * collapsed, rotating to point down when expanded) and DockSelect (points down when closed,
 * rotating to point UP when open, the opposite convention) each mean something different by
 * "open", so each applies its own `className` rotation on the wrapping element instead. */
function DockChevron({ className = "" }: { className?: string }) {
  return (
    <svg viewBox="0 0 10 6" className={`h-[6px] w-[10px] transition-transform duration-150 ${className}`} fill="none">
      <path d="M1 1l4 4 4-4" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  );
}

/** Collapsible card section for panel bodies -- same fold/persist idea as Navbar.tsx's old
 * `Section`, restyled with Tailwind utility classes and roomier spacing. `persistKey` only
 * needed when the same title appears in more than one panel (e.g. "Marker Style"). */
export function DockSection({
  title,
  children,
  defaultOpen = true,
  persistKey,
}: {
  title: string;
  children: ReactNode;
  defaultOpen?: boolean;
  persistKey?: string;
}) {
  const key = persistKey ?? title;
  const [open, setOpenState] = useState(() => loadDockSectionOpen(key, defaultOpen));
  const setOpen = (updater: (o: boolean) => boolean) => {
    setOpenState((o) => {
      const next = updater(o);
      try {
        localStorage.setItem(DOCK_SECTION_FOLD_PREFIX + key, String(next));
      } catch {
        /* ignore -- fold state just won't persist across reloads */
      }
      return next;
    });
  };
  return (
    <div className="border-panel-border bg-panel-elevated overflow-hidden rounded-[10px] border shadow-[0_1px_0_0_rgba(255,255,255,.04)_inset,0_6px_16px_rgba(0,0,0,.22)]">
      <div
        onClick={() => setOpen((o) => !o)}
        className={`flex cursor-pointer select-none items-center justify-between gap-2 px-3.5 py-2.5 transition-colors hover:bg-white/[0.04] ${
          open ? "border-panel-border/70 border-b" : ""
        }`}
      >
        <div className="overflow-hidden text-ellipsis whitespace-nowrap text-[10.5px] font-bold uppercase tracking-wide text-[#d7d9e2]">{title}</div>
        <span className="text-text-faint flex-shrink-0">
          <DockChevron className={open ? "" : "-rotate-90"} />
        </span>
      </div>
      {open && <div className="flex flex-col gap-3 px-3.5 pb-3.5 pt-3">{children}</div>}
    </div>
  );
}

/** Horizontal tab bar for panel bodies -- same interaction pattern as the reference desktop
 * client's own ChromePanel.DrawTabHeader (LootPanel.cs "General/Tiers/Containers/Priority",
 * QuestPanel.cs, GeneralPanel.cs, etc. all use this to switch between several settings groups
 * inside ONE window) instead of DockSection's accordion stack -- used where a panel's own
 * top-level sections are a small fixed set (2-4), same as every desktop panel that reaches for
 * tabs. Deliberately NOT used for a dynamic, user-grown list (e.g. PoiPanel's one-section-per-POI-
 * group) -- tabs assume a known set of options, not an arbitrary-length list, which is also
 * exactly why desktop's own POIPanel.cs uses a list/detail split instead of tabs. Active tab is
 * plain caller state, not persisted -- matches desktop's own `_tab` field, which resets to the
 * first tab on every app restart rather than being saved to Config. */
export function DockTabs({
  tabs,
  active,
  onChange,
  children,
}: {
  tabs: readonly string[];
  active: number;
  onChange: (i: number) => void;
  children: ReactNode;
}) {
  return (
    <div className="border-panel-border bg-panel-elevated overflow-hidden rounded-[10px] border shadow-[0_1px_0_0_rgba(255,255,255,.04)_inset,0_6px_16px_rgba(0,0,0,.22)]">
      <div className="border-panel-border/70 flex border-b">
        {tabs.map((t, i) => (
          <button
            key={t}
            type="button"
            onClick={() => onChange(i)}
            className={`relative flex-1 overflow-hidden text-ellipsis whitespace-nowrap border-0 bg-transparent px-2 py-2.5 text-[10.5px] font-bold uppercase tracking-wide transition-colors ${
              i === active ? "text-accent bg-white/[0.03]" : "text-text-faint hover:bg-white/[0.03] hover:text-text-dim"
            }`}
          >
            {t}
            {i === active && <span className="bg-accent absolute -bottom-px left-0 right-0 h-[2px]" />}
          </button>
        ))}
      </div>
      <div className="flex flex-col gap-3 px-3.5 pb-3.5 pt-3">{children}</div>
    </div>
  );
}

export function DockFieldLabel({ children, value }: { children: ReactNode; value?: string }) {
  if (value === undefined) return <div className="mb-1 text-xs text-[#c7cad1]">{children}</div>;
  return (
    <div className="mb-1.5 flex items-center justify-between gap-2 text-xs text-[#c7cad1]">
      <span className="min-w-0 overflow-hidden text-ellipsis whitespace-nowrap">{children}</span>
      <span className="text-text-dim flex-shrink-0 whitespace-nowrap font-mono [font-variant-numeric:tabular-nums]">{value}</span>
    </div>
  );
}

/** Fully custom-drawn checkbox (a real, invisible `<input>` for click/keyboard/focus handling,
 * overlaid with a decorative box driven straight off the `checked` prop in JS) instead of a bare
 * `accent-color`'d native checkbox -- `accent-color` only tints the browser's OWN checkbox
 * rendering, which stays whatever flat, unrounded, inconsistent-across-browsers shape the OS
 * draws, clashing with this app's otherwise fully custom dark UI. Modeled on the reference C#
 * desktop client's own custom ImGui checkbox (`UI.Toggle` in xm-eft-dma-radar-private's
 * UIControls.cs): a small rounded square, accent-filled with a white tick when checked, dark
 * frame-bg when not. Used by every checkbox in navDock/, not just DockToggleRow, so raw
 * `<input type="checkbox">` usages in panel files were swapped over too. */
export function DockCheckbox({
  checked,
  onChange,
  disabled,
}: {
  checked: boolean;
  onChange: (v: boolean) => void;
  disabled?: boolean;
}) {
  return (
    <span className="relative inline-flex h-[15px] w-[15px] flex-shrink-0 items-center justify-center">
      <input
        type="checkbox"
        checked={checked}
        disabled={disabled}
        onChange={(e) => onChange(e.target.checked)}
        className="peer absolute inset-0 h-full w-full cursor-pointer appearance-none border-0 bg-transparent opacity-0 disabled:cursor-default"
      />
      <span
        aria-hidden
        className={`pointer-events-none flex h-full w-full items-center justify-center rounded-[3px] border transition-colors peer-focus-visible:ring-1 peer-focus-visible:ring-accent ${
          checked ? "border-accent bg-accent" : "border-panel-border bg-input-bg"
        } ${disabled ? "opacity-40" : ""}`}
      >
        {checked && (
          <svg viewBox="0 0 16 16" className="h-[10px] w-[10px]">
            <path d="M3 8.3 6.5 12 13 4.5" stroke="white" strokeWidth="2.2" fill="none" strokeLinecap="round" strokeLinejoin="round" />
          </svg>
        )}
      </span>
    </span>
  );
}

export function DockToggleRow({
  label,
  checked,
  onChange,
  disabled,
  title,
}: {
  label: string;
  checked: boolean;
  onChange: (v: boolean) => void;
  disabled?: boolean;
  title?: string;
}) {
  return (
    <label title={title} className={`flex items-center gap-2.5 text-[12.5px] ${disabled ? "cursor-default opacity-40" : "cursor-pointer"}`}>
      <DockCheckbox checked={checked} onChange={onChange} disabled={disabled} />
      {label}
    </label>
  );
}

const clamp = (v: number, min: number, max: number) => Math.max(min, Math.min(max, v));
const clamp01 = (v: number) => clamp(v, 0, 1);

/** Fully custom slider -- a div-drawn track/fill/thumb driven by pointer events, replacing the
 * native `<input type=range>` (see this file's own docstring on why: native controls read as
 * unstyled/OS-drawn against the rest of this dark UI). Same label/value/min/max/step/onChange/
 * format/disabled/title API as before, so no call site needs to change. */
/** Bare slider track + thumb, no label -- the reusable core DockRangeRow wraps for the common
 * "label + value line above the track" layout. Exported separately for spots that need just the
 * slider inline among other controls in the same row (e.g. EntityVisualSettings' per-tier
 * checkbox/slider/value/color row), where DockRangeRow's own label line wouldn't fit. */
export function DockSlider({
  value,
  min,
  max,
  step,
  onChange,
  disabled,
  className = "",
}: {
  value: number;
  min: number;
  max: number;
  step: number;
  onChange: (v: number) => void;
  disabled?: boolean;
  className?: string;
}) {
  const trackRef = useRef<HTMLDivElement>(null);
  const [dragging, setDragging] = useState(false);
  const pct = ((clamp(value, min, max) - min) / (max - min)) * 100;

  const setFromClientX = (clientX: number) => {
    const r = trackRef.current;
    if (!r) return;
    const rect = r.getBoundingClientRect();
    const p = clamp01((clientX - rect.left) / rect.width);
    const raw = min + p * (max - min);
    onChange(clamp(Math.round(raw / step) * step, min, max));
  };

  return (
    <div
      ref={trackRef}
      role="slider"
      aria-valuemin={min}
      aria-valuemax={max}
      aria-valuenow={value}
      aria-disabled={disabled}
      tabIndex={disabled ? -1 : 0}
      onPointerDown={(e) => {
        if (disabled) return;
        (e.currentTarget as Element).setPointerCapture(e.pointerId);
        setDragging(true);
        setFromClientX(e.clientX);
      }}
      onPointerMove={(e) => dragging && !disabled && setFromClientX(e.clientX)}
      onPointerUp={() => setDragging(false)}
      onKeyDown={(e) => {
        if (disabled) return;
        if (e.key === "ArrowRight" || e.key === "ArrowUp") onChange(clamp(value + step, min, max));
        if (e.key === "ArrowLeft" || e.key === "ArrowDown") onChange(clamp(value - step, min, max));
      }}
      className={`relative flex h-5 touch-none items-center ${disabled ? "cursor-default" : "cursor-pointer"} ${className}`}
    >
      <div className="border-panel-border/60 bg-input-bg relative h-1 box-border w-full rounded-full border">
        <div className="from-accent/70 to-accent absolute inset-y-0 left-0 rounded-full bg-gradient-to-r" style={{ width: `${pct}%` }} />
      </div>
      <div
        className="border-accent absolute top-1/2 h-3.5 w-3.5 -translate-x-1/2 -translate-y-1/2 rounded-full border-2 bg-white shadow-[0_2px_6px_rgba(0,0,0,.5)] transition-shadow"
        style={{ left: `${pct}%`, boxShadow: dragging ? "0 0 0 6px rgba(79,140,255,.18)" : undefined }}
      />
    </div>
  );
}

export function DockRangeRow({
  label,
  value,
  min,
  max,
  step,
  onChange,
  format,
  disabled,
  title,
}: {
  label: string;
  value: number;
  min: number;
  max: number;
  step: number;
  onChange: (v: number) => void;
  format?: (v: number) => string;
  disabled?: boolean;
  title?: string;
}) {
  return (
    <div title={title} className={disabled ? "opacity-40" : undefined}>
      <DockFieldLabel value={format ? format(value) : String(value)}>{label}</DockFieldLabel>
      <DockSlider value={value} min={min} max={max} step={step} onChange={onChange} disabled={disabled} />
    </div>
  );
}

const COLOR_PRESETS = ["#4f8cff", "#34d399", "#a78bfa", "#f2555a", "#f5a524", "#22d3ee", "#f472b6", "#e5e7eb"];

function hexToRgb(hex: string): [number, number, number] {
  const m = hex.replace("#", "");
  const n = parseInt(m.length === 3 ? m.split("").map((c) => c + c).join("") : m, 16);
  return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
}
function rgbToHex(r: number, g: number, b: number): string {
  return "#" + [r, g, b].map((x) => clamp(Math.round(x), 0, 255).toString(16).padStart(2, "0")).join("");
}
function rgbToHsv(r: number, g: number, b: number): [number, number, number] {
  r /= 255; g /= 255; b /= 255;
  const max = Math.max(r, g, b), min = Math.min(r, g, b), d = max - min;
  let h = 0;
  if (d !== 0) {
    if (max === r) h = ((g - b) / d) % 6;
    else if (max === g) h = (b - r) / d + 2;
    else h = (r - g) / d + 4;
    h *= 60;
    if (h < 0) h += 360;
  }
  const s = max === 0 ? 0 : d / max;
  return [h, s * 100, max * 100];
}
function hsvToRgb(h: number, s: number, v: number): [number, number, number] {
  s /= 100; v /= 100;
  const c = v * s, x = c * (1 - Math.abs(((h / 60) % 2) - 1)), m = v - c;
  let r = 0, g = 0, b = 0;
  if (h < 60) [r, g, b] = [c, x, 0];
  else if (h < 120) [r, g, b] = [x, c, 0];
  else if (h < 180) [r, g, b] = [0, c, x];
  else if (h < 240) [r, g, b] = [0, x, c];
  else if (h < 300) [r, g, b] = [x, 0, c];
  else [r, g, b] = [c, 0, x];
  return [(r + m) * 255, (g + m) * 255, (b + m) * 255];
}

/** Custom color swatch + popover (SV square, hue strip, hex field, presets) replacing the native
 * `<input type=color>` OS dialog, which looks jarring against this app's own dark UI and can't be
 * styled at all. HSV is kept as the popover's OWN local state, seeded from `color` only when it
 * opens (not continuously re-derived from the prop while open) -- re-deriving on every keystroke
 * would snap hue back to an arbitrary value for fully desaturated colors and fight the drag
 * gesture mid-interaction. */
/** Bare swatch-button + HSV popover, no label row -- the reusable core DockColorRow wraps for the
 * common "label on the left" layout. Exported separately for spots that need just the swatch
 * inline among other controls in the same row (e.g. PoiPanel's per-entry checkbox/color/name/
 * delete row), where DockColorRow's own label would be redundant or wouldn't fit. */
export function DockColorSwatch({ color, onColorChange, disabled, size = 26 }: { color: string; onColorChange: (v: string) => void; disabled?: boolean; size?: number }) {
  const [open, setOpenState] = useState(false);
  const [hsv, setHsv] = useState<[number, number, number]>(() => rgbToHsv(...hexToRgb(color || "#000000")));
  const btnRef = useRef<HTMLButtonElement>(null);
  const popRef = useRef<HTMLDivElement>(null);
  const closeRef = useRef(() => setOpenState(false));
  const pos = usePopoverPosition(open, btnRef, { width: 216, height: 272 });

  const setOpen = (v: boolean) => {
    if (v) {
      setHsv(rgbToHsv(...hexToRgb(color || "#000000")));
      claimPopover(closeRef.current);
    } else {
      releasePopover(closeRef.current);
    }
    setOpenState(v);
  };
  useEffect(() => () => releasePopover(closeRef.current), []);
  useCloseOnOutside(open, [btnRef, popRef], () => setOpen(false));

  const [h, s, v] = hsv;
  const hex = rgbToHex(...hsvToRgb(h, s, v));
  const commit = (next: [number, number, number]) => {
    setHsv(next);
    onColorChange(rgbToHex(...hsvToRgb(next[0], next[1], next[2])));
  };

  const svRef = useRef<HTMLDivElement>(null);
  const hueRef = useRef<HTMLDivElement>(null);
  const dragOn = (ref: RefObject<HTMLDivElement | null>, apply: (px: number, py: number) => void) => (e: ReactPointerEvent) => {
    const move = (clientX: number, clientY: number) => {
      const r = ref.current;
      if (!r) return;
      const rect = r.getBoundingClientRect();
      apply(clamp01((clientX - rect.left) / rect.width), clamp01((clientY - rect.top) / rect.height));
    };
    move(e.clientX, e.clientY);
    const onMove = (ev: PointerEvent) => move(ev.clientX, ev.clientY);
    const onUp = () => {
      window.removeEventListener("pointermove", onMove);
      window.removeEventListener("pointerup", onUp);
    };
    window.addEventListener("pointermove", onMove);
    window.addEventListener("pointerup", onUp);
  };

  return (
    <>
      <button
        ref={btnRef}
        type="button"
        disabled={disabled}
        onClick={() => !disabled && setOpen(!open)}
        className="border-panel-border flex-shrink-0 cursor-pointer rounded-[9px] border shadow-[inset_0_0_0_2px_rgba(0,0,0,.25)] disabled:cursor-default disabled:opacity-40"
        style={{ background: color, width: size, height: size }}
      />
      {open && (
        <div
          ref={popRef}
          className="border-panel-border bg-panel-elevated fixed z-[200] overflow-hidden rounded-[9px] border p-3 shadow-[0_16px_40px_rgba(0,0,0,.55)]"
          style={{ top: pos.top, left: pos.left, width: pos.width }}
        >
          <div
            ref={svRef}
            onPointerDown={dragOn(svRef, (px, py) => commit([h, px * 100, 100 - py * 100]))}
            className="border-panel-border/60 relative mb-2.5 h-[120px] box-border w-full cursor-crosshair rounded-[9px] border"
            style={{ background: `linear-gradient(to top, #000, transparent), linear-gradient(to right, #fff, transparent), hsl(${h}, 100%, 50%)` }}
          >
            <div
              className="pointer-events-none absolute h-3 w-3 -translate-x-1/2 -translate-y-1/2 rounded-full border-2 border-white shadow-[0_0_0_1px_rgba(0,0,0,.6),0_1px_4px_rgba(0,0,0,.5)]"
              style={{ left: `${s}%`, top: `${100 - v}%` }}
            />
          </div>
          <div
            ref={hueRef}
            onPointerDown={dragOn(hueRef, (px) => commit([px * 360, s, v]))}
            className="border-panel-border/60 relative mb-2.5 h-3 box-border w-full cursor-pointer rounded-full border"
            style={{ background: "linear-gradient(to right, #f00 0%, #ff0 17%, #0f0 33%, #0ff 50%, #00f 67%, #f0f 83%, #f00 100%)" }}
          >
            <div
              className="pointer-events-none absolute top-1/2 h-3.5 w-3.5 -translate-x-1/2 -translate-y-1/2 rounded-full border-2 border-black/35 bg-white shadow-[0_1px_4px_rgba(0,0,0,.5)]"
              style={{ left: `${(h / 360) * 100}%` }}
            />
          </div>
          <div className="mb-2.5 flex items-center gap-2">
            <div className="border-panel-border h-[26px] w-[26px] flex-shrink-0 rounded-[9px] border" style={{ background: hex }} />
            <input
              value={hex}
              onChange={(e) => {
                const val = e.target.value;
                if (/^#?[0-9a-fA-F]{6}$/.test(val)) commit(rgbToHsv(...hexToRgb(val.startsWith("#") ? val : `#${val}`)));
              }}
              spellCheck={false}
              maxLength={7}
              className="border-panel-border bg-input-bg focus:border-accent min-w-0 flex-1 rounded-[9px] border px-2 py-1.5 font-mono text-xs text-white focus:outline-none"
            />
          </div>
          <div className="grid grid-cols-8 gap-[5px]">
            {COLOR_PRESETS.map((c) => (
              <button
                key={c}
                type="button"
                onClick={() => commit(rgbToHsv(...hexToRgb(c)))}
                className="hover:ring-accent hover:ring-offset-panel-elevated aspect-square box-border w-full rounded-[5px] border border-white/10 hover:ring-2 hover:ring-offset-2"
                style={{ background: c }}
              />
            ))}
          </div>
        </div>
      )}
    </>
  );
}

export function DockColorRow({ label, color, onColorChange, disabled }: { label: string; color: string; onColorChange: (v: string) => void; disabled?: boolean }) {
  return (
    <div className="flex items-center justify-between gap-2">
      <span className="text-[12.5px]">{label}</span>
      <DockColorSwatch color={color} onColorChange={onColorChange} disabled={disabled} />
    </div>
  );
}

/** `disabled` options show greyed out and can't be picked; `hint` is their hover tooltip. */
export type DockSelectOption = { value: string; label: string; disabled?: boolean; hint?: string };
export type DockSelectGroup = { group: string; options: DockSelectOption[] };
function isSelectGroup(o: DockSelectOption | DockSelectGroup): o is DockSelectGroup {
  return "group" in o;
}

/** Custom dropdown replacing the native `<select>` -- the menu is `position: fixed` (see
 * usePopoverPosition's docstring), left-aligned and width-matched to the trigger button so it
 * never needs its own horizontal clamping. Supports flat options or `{group, options}` sections
 * (e.g. Aim View's Players/Entities type picker). */
export function DockSelect({
  value,
  onChange,
  options,
  disabled,
  placeholder,
}: {
  value: string;
  onChange: (v: string) => void;
  options: (DockSelectOption | DockSelectGroup)[];
  disabled?: boolean;
  placeholder?: string;
}) {
  const [open, setOpenState] = useState(false);
  const btnRef = useRef<HTMLButtonElement>(null);
  const menuRef = useRef<HTMLDivElement>(null);
  const closeRef = useRef(() => setOpenState(false));
  const pos = usePopoverPosition(open, btnRef, { height: 240 });

  const setOpen = (v: boolean) => {
    if (v) claimPopover(closeRef.current);
    else releasePopover(closeRef.current);
    setOpenState(v);
  };
  useEffect(() => () => releasePopover(closeRef.current), []);
  useCloseOnOutside(open, [btnRef, menuRef], () => setOpen(false));

  const flat = options.flatMap((o) => (isSelectGroup(o) ? o.options : [o]));
  const current = flat.find((o) => o.value === value);

  const pick = (v: string) => {
    onChange(v);
    setOpen(false);
  };

  return (
    <div className="relative">
      <button
        ref={btnRef}
        type="button"
        disabled={disabled}
        onClick={() => !disabled && setOpen(!open)}
        className="border-panel-border bg-input-bg flex box-border w-full items-center justify-between gap-2 rounded-[9px] border px-2.5 py-1.5 text-left text-[12.5px] text-white disabled:cursor-default disabled:opacity-40"
      >
        <span className="min-w-0 flex-1 overflow-hidden text-ellipsis whitespace-nowrap">{current?.label ?? placeholder ?? value}</span>
        <span className="text-text-faint flex-shrink-0">
          <DockChevron className={open ? "rotate-180" : ""} />
        </span>
      </button>
      {open && (
        <div
          ref={menuRef}
          className="border-panel-border bg-panel-elevated fixed z-[200] max-h-[240px] overflow-y-auto rounded-[9px] border p-1 shadow-[0_12px_28px_rgba(0,0,0,.5)]"
          style={{ top: pos.top, left: pos.left, width: pos.width }}
        >
          {options.map((o, i) =>
            isSelectGroup(o) ? (
              <div key={`${o.group}-${i}`}>
                <div className="text-text-faint px-2.5 pb-1 pt-2 text-[10px] font-bold uppercase tracking-wide">{o.group}</div>
                {o.options.map((opt) => (
                  <DockSelectOpt key={opt.value} opt={opt} selected={opt.value === value} onPick={pick} />
                ))}
              </div>
            ) : (
              <DockSelectOpt key={o.value} opt={o} selected={o.value === value} onPick={pick} />
            ),
          )}
        </div>
      )}
    </div>
  );
}
function DockSelectOpt({ opt, selected, onPick }: { opt: DockSelectOption; selected: boolean; onPick: (v: string) => void }) {
  return (
    <div
      onClick={() => !opt.disabled && onPick(opt.value)}
      title={opt.hint}
      aria-disabled={opt.disabled || undefined}
      className={`overflow-hidden text-ellipsis whitespace-nowrap rounded px-2.5 py-1.5 text-[12px] ${
        opt.disabled
          ? "text-text-faint cursor-not-allowed opacity-60"
          : selected
            ? "text-accent cursor-pointer font-semibold"
            : "text-text-dim cursor-pointer hover:bg-white/5 hover:text-white"
      }`}
    >
      {opt.label}
    </div>
  );
}

// box-border: there's no global border-box reset, so w-full + padding + border made every input
// ~22px wider than its container, clipped on the right inside overflow-hidden panels.
export const dockInputClass =
  "border-panel-border bg-input-bg box-border w-full min-w-0 rounded-[9px] border px-2.5 py-1.5 text-[12.5px] text-white placeholder:text-text-dim";
/** Deprecated -- native `<select>` styling, kept only until any straggler call sites are found;
 * new code should use DockSelect. */
export const dockSelectClass =
  "border-panel-border bg-input-bg box-border w-full min-w-0 appearance-none rounded-[9px] border px-2.5 py-1.5 pr-7 text-[12.5px] text-white cursor-pointer";
export const dockSelectStyle: CSSProperties = { ...SELECT_CHEVRON };

export function DockSmallButton({
  children,
  onClick,
  active,
  full,
}: {
  children: ReactNode;
  onClick: () => void;
  active?: boolean;
  full?: boolean;
}) {
  return (
    <button
      onClick={onClick}
      className={`rounded-[9px] border px-3 py-[7px] text-[12.5px] font-medium transition-colors ${full ? "w-full" : ""} ${
        active ? "border-accent bg-accent text-white" : "border-panel-border bg-input-bg text-white hover:bg-white/[0.06] hover:border-text-faint"
      }`}
    >
      {children}
    </button>
  );
}
