import { useEffect, useRef, useState } from "react";

export type BoxSize = { w: number; h: number | null };

type Limits = { minW: number; maxW: number; minH: number; maxH: number };

function loadStoredSize(storageKey: string, fallback: BoxSize): BoxSize {
  try {
    const raw = localStorage.getItem(storageKey);
    if (!raw) return fallback;
    const parsed = JSON.parse(raw);
    if (typeof parsed?.w === "number") return { w: parsed.w, h: typeof parsed.h === "number" ? parsed.h : null };
  } catch {
    /* ignore -- fall back to default */
  }
  return fallback;
}

/** Drag-to-resize for floating panels, from a grip in the bottom-right corner - the resize
 * counterpart of useDraggablePanel. Width is always explicit; height starts `null` (the panel's own
 * content/maxHeight decides) and becomes explicit the first time the user drags it, so an untouched
 * widget looks exactly as it did before this existed. Clamped to the given limits and to the
 * viewport (measured from the panel's current top-left), persisted to localStorage on release.
 *
 * `square` keeps w === h (the Aim View canvas). `onCommit`, if given, is called with the final size
 * on release instead of (not as well as) persisting under `storageKey` - for a widget whose size
 * already lives in its own settings object. */
export function useResizableBox(
  storageKey: string,
  defaultSize: BoxSize,
  limits: Limits,
  opts: { square?: boolean; panelRef?: React.RefObject<HTMLElement | null>; controlled?: BoxSize; onCommit?: (s: BoxSize) => void } = {},
) {
  const [stored, setStored] = useState<BoxSize>(() => (opts.onCommit ? defaultSize : loadStoredSize(storageKey, defaultSize)));
  const [live, setLive] = useState<BoxSize | null>(null); // non-null only mid-drag
  const dragRef = useRef<{ startX: number; startY: number; origW: number; origH: number } | null>(null);
  const optsRef = useRef(opts);
  optsRef.current = opts;

  const base = opts.controlled ?? stored;
  const size = live ?? base;

  useEffect(() => {
    const clampTo = (w: number, h: number): BoxSize => {
      const rect = optsRef.current.panelRef?.current?.getBoundingClientRect();
      const roomW = rect ? window.innerWidth - rect.left - 4 : Infinity;
      const roomH = rect ? window.innerHeight - rect.top - 4 : Infinity;
      let nw = Math.round(Math.min(Math.max(limits.minW, w), limits.maxW, roomW));
      let nh = Math.round(Math.min(Math.max(limits.minH, h), limits.maxH, roomH));
      if (optsRef.current.square) nw = nh = Math.min(nw, nh);
      return { w: nw, h: nh };
    };
    const onMove = (e: PointerEvent) => {
      const d = dragRef.current;
      if (!d) return;
      const dx = e.clientX - d.startX;
      const dy = e.clientY - d.startY;
      // Square: follow whichever axis moved further, so a diagonal drag feels natural.
      const delta = Math.abs(dx) > Math.abs(dy) ? dx : dy;
      setLive(optsRef.current.square ? clampTo(d.origW + delta, d.origH + delta) : clampTo(d.origW + dx, d.origH + dy));
    };
    const onUp = () => {
      if (!dragRef.current) return;
      dragRef.current = null;
      setLive((final) => {
        if (final) {
          if (optsRef.current.onCommit) optsRef.current.onCommit(final);
          else {
            setStored(final);
            try {
              localStorage.setItem(storageKey, JSON.stringify(final));
            } catch {
              /* ignore -- size just won't persist across reloads */
            }
          }
        }
        return null;
      });
    };
    window.addEventListener("pointermove", onMove);
    window.addEventListener("pointerup", onUp);
    return () => {
      window.removeEventListener("pointermove", onMove);
      window.removeEventListener("pointerup", onUp);
    };
  }, [storageKey, limits.minW, limits.maxW, limits.minH, limits.maxH]);

  const onResizePointerDown = (e: React.PointerEvent) => {
    e.preventDefault();
    e.stopPropagation();
    const el = optsRef.current.panelRef?.current;
    const current = live ?? optsRef.current.controlled ?? stored;
    const measuredH = el ? el.getBoundingClientRect().height : current.h ?? 0;
    dragRef.current = { startX: e.clientX, startY: e.clientY, origW: current.w, origH: current.h ?? measuredH };
  };

  return { size, resizing: live !== null, onResizePointerDown };
}

/** The bottom-right grip. Place inside a `position: fixed/relative` panel. */
export function ResizeHandle({ onPointerDown }: { onPointerDown: (e: React.PointerEvent) => void }) {
  return (
    <div
      onPointerDown={onPointerDown}
      title="Drag to resize"
      style={{
        position: "absolute",
        right: 0,
        bottom: 0,
        width: 16,
        height: 16,
        cursor: "nwse-resize",
        touchAction: "none",
        zIndex: 2,
        // Three diagonal ticks, drawn with a gradient so there's no extra asset.
        background:
          "linear-gradient(135deg, transparent 0 55%, #4b4b66 55% 60%, transparent 60% 70%, #4b4b66 70% 75%, transparent 75% 85%, #4b4b66 85% 90%, transparent 90%)",
        borderBottomRightRadius: 12,
      }}
    />
  );
}
