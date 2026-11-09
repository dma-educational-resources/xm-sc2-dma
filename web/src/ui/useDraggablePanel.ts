import { useEffect, useRef, useState } from "react";

export type DragPos = { x: number; y: number };

// Shared across every floating panel in the app (all 5 widgets + HudPanel + the new navDock
// panels) so "click a panel, it comes to front" works globally instead of per-component. Before
// this, every panel hardcoded the same zIndex: 80000 with no raise-on-focus -- fine at 1-2 panels
// open, not once the dock can have up to 8 new + 5 existing panels open simultaneously.
let sharedTopZIndex = 80000;
export function nextZIndex(): number {
  sharedTopZIndex += 1;
  return sharedTopZIndex;
}

/** First-open placement for panels with no stored position yet -- offsets diagonally from a
 * shared anchor (e.g. the dock's own bounding rect) by `index` steps, so panels opened one after
 * another cascade instead of spawning stacked exactly on top of each other. Wraps back toward the
 * anchor before marching off-screen on shorter viewports. */
export function cascadePosition(anchor: DragPos, index: number, step = 28, wrapAfter = 10): DragPos {
  const wrapped = index % wrapAfter;
  return { x: anchor.x + wrapped * step, y: anchor.y + wrapped * step };
}

function loadStoredPos(storageKey: string, defaultPos: DragPos): DragPos {
  try {
    const raw = localStorage.getItem(storageKey);
    if (!raw) return defaultPos;
    const parsed = JSON.parse(raw);
    if (typeof parsed?.x === "number" && typeof parsed?.y === "number") return parsed;
  } catch {
    /* ignore -- fall back to default */
  }
  return defaultPos;
}

/** Shared drag-to-move logic for floating panels -- extracted from ModPanel.tsx (the "Session
 * Admin" popup), which had this hand-rolled inline before LootWidget/PlayersWidget needed the
 * exact same behavior a second and third time: pointer-drag from a header, viewport-clamped,
 * position persisted to localStorage. Excludes BUTTON/INPUT header children from starting a drag
 * so a widget's own header controls (minimize button, search box) stay clickable. */
export function useDraggablePanel(storageKey: string, defaultPos: DragPos) {
  const [pos, setPos] = useState<DragPos>(() => loadStoredPos(storageKey, defaultPos));
  const [zIndex, setZIndex] = useState<number>(() => nextZIndex());
  const [dragging, setDragging] = useState(false);
  const panelRef = useRef<HTMLDivElement>(null);
  const dragRef = useRef<{ dragging: boolean; startX: number; startY: number; origX: number; origY: number }>({
    dragging: false,
    startX: 0,
    startY: 0,
    origX: 0,
    origY: 0,
  });

  useEffect(() => {
    const onMove = (e: PointerEvent) => {
      const d = dragRef.current;
      if (!d.dragging) return;
      const panelWidth = panelRef.current?.offsetWidth ?? 260;
      const panelHeight = panelRef.current?.offsetHeight ?? 44;
      const nextX = Math.min(Math.max(0, d.origX + (e.clientX - d.startX)), window.innerWidth - panelWidth);
      const nextY = Math.min(Math.max(0, d.origY + (e.clientY - d.startY)), window.innerHeight - panelHeight);
      setPos({ x: nextX, y: nextY });
    };
    const onUp = () => {
      if (!dragRef.current.dragging) return;
      dragRef.current.dragging = false;
      setDragging(false);
      setPos((p) => {
        try {
          localStorage.setItem(storageKey, JSON.stringify(p));
        } catch {
          /* ignore -- position just won't persist across reloads */
        }
        return p;
      });
    };
    window.addEventListener("pointermove", onMove);
    window.addEventListener("pointerup", onUp);
    return () => {
      window.removeEventListener("pointermove", onMove);
      window.removeEventListener("pointerup", onUp);
    };
  }, [storageKey]);

  // Re-clamps into the viewport whenever the browser window itself is resized (not just while
  // dragging) -- a position stored/dragged at a wider window otherwise just sits past the new
  // right/bottom edge with no way back, since the header you'd drag it back by is also off-screen.
  // Also runs once on mount, since a stored position can already predate a smaller window from a
  // previous session. Persists the clamped result so shrinking then growing the window back doesn't
  // un-clamp it from a stale localStorage value.
  useEffect(() => {
    const clamp = () => {
      setPos((p) => {
        const panelWidth = panelRef.current?.offsetWidth ?? 260;
        const panelHeight = panelRef.current?.offsetHeight ?? 44;
        const nextX = Math.min(Math.max(0, p.x), window.innerWidth - panelWidth);
        const nextY = Math.min(Math.max(0, p.y), window.innerHeight - panelHeight);
        if (nextX === p.x && nextY === p.y) return p;
        const next = { x: nextX, y: nextY };
        try {
          localStorage.setItem(storageKey, JSON.stringify(next));
        } catch {
          /* ignore -- position just won't persist across reloads */
        }
        return next;
      });
    };
    clamp();
    window.addEventListener("resize", clamp);
    return () => window.removeEventListener("resize", clamp);
  }, [storageKey]);

  const onHeaderPointerDown = (e: React.PointerEvent) => {
    setZIndex(nextZIndex());
    const tag = (e.target as HTMLElement).tagName;
    if (tag === "BUTTON" || tag === "INPUT") return;
    dragRef.current = { dragging: true, startX: e.clientX, startY: e.clientY, origX: pos.x, origY: pos.y };
    setDragging(true);
  };

  return { pos, panelRef, onHeaderPointerDown, zIndex, dragging };
}
