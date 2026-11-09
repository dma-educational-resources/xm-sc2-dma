import { useEffect, useState, type ReactNode } from "react";
import { useDraggablePanel, type DragPos } from "./useDraggablePanel";
import { useResizableBox, ResizeHandle } from "./useResizableBox";

// Shared "floating dark card" chrome for every draggable HUD overlay in the 3D view -- same visual
// language as ModPanel.tsx's Session Admin widget (which is what the stats/disabled-removed panels
// were asked to match): #14141b card, #2c2c42 border, 12px radius, Inter/system-ui, a drag-by-
// header title bar with a –/+ collapse button. Previously the 3D stats box and the disabled/removed
// box were both hand-rolled flat `rgba(0,0,0,0.6)` monospace boxes with no persisted collapse state
// and no way to fully hide them beyond a bespoke one-off toggle each -- this unifies both onto the
// same shell PlayersWidget/LootWidget/ModPanel already share, and makes collapse state persist like
// their drag position already did.

function loadBoolean(key: string, fallback: boolean): boolean {
  try {
    const raw = localStorage.getItem(key);
    return raw === null ? fallback : raw === "true";
  } catch {
    return fallback;
  }
}

/** Persisted collapse state, same idea as useDraggablePanel's persisted position -- a panel you
 * collapsed last session should still be collapsed next time, not reset to fully expanded every
 * reload. */
export function usePersistedCollapsed(storageKey: string, defaultCollapsed = false): [boolean, (v: boolean) => void] {
  const [collapsed, setCollapsedState] = useState(() => loadBoolean(storageKey, defaultCollapsed));
  useEffect(() => {
    try {
      localStorage.setItem(storageKey, String(collapsed));
    } catch {
      /* ignore -- collapse state just won't persist across reloads */
    }
  }, [collapsed, storageKey]);
  return [collapsed, setCollapsedState];
}

/** Title-bar padding shared by every floating panel (dock panels here, the floating widgets, the
 * Session Admin popup) so their headers are the same compact height. */
export const PANEL_HEADER_PADDING = "5px 10px";

/** Title-bar double-click handler: toggles collapse, same as the –/+ button. Ignored when the
 * double-click lands on a header control (button/input/select) so those keep working normally. */
export function onHeaderDoubleClick(toggle: () => void) {
  return (e: React.MouseEvent) => {
    if ((e.target as HTMLElement).closest("button, input, select, textarea")) return;
    toggle();
  };
}

export function HudPanel({
  storageKeyPos,
  defaultPos,
  title,
  titleColor = "#60a5fa",
  collapsed,
  onToggleCollapsed,
  headerExtra,
  width = 260,
  maxHeight,
  zIndex,
  onClose,
  resizable = true,
  children,
}: {
  storageKeyPos: string;
  defaultPos: DragPos;
  title: ReactNode;
  titleColor?: string;
  collapsed: boolean;
  onToggleCollapsed: () => void;
  headerExtra?: ReactNode;
  width?: number;
  maxHeight?: string | number;
  /** Overrides the panel's own raise-on-focus z-index (rarely needed -- omit to let the panel
   * manage its own stacking order via useDraggablePanel). */
  zIndex?: number;
  /** Renders a "×" button next to the collapse button when provided. Distinct from `collapsed` --
   * collapse is a free "minimize in place" affordance, close removes the panel entirely (the
   * navDock owns each panel's open/closed boolean and passes this to unmount it). */
  onClose?: () => void;
  /** Bottom-right corner grip to resize the panel (on by default). Width starts at `width`;
   * height stays content/maxHeight-driven until the user first drags it. Size persists per panel
   * under `${storageKeyPos}:size`, next to its position. */
  resizable?: boolean;
  children: ReactNode;
}) {
  const { pos, panelRef, onHeaderPointerDown, zIndex: ownZIndex, dragging } = useDraggablePanel(storageKeyPos, defaultPos);
  const { size, onResizePointerDown } = useResizableBox(
    `${storageKeyPos}:size`,
    { w: width, h: null },
    { minW: 220, maxW: 1000, minH: 120, maxH: 2000 },
    { panelRef },
  );
  const panelWidth = resizable ? size.w : width;
  // A user-dragged height fixes the panel's height and lets the body scroll inside it instead of
  // the caller's maxHeight. Ignored while collapsed so collapsing still shrinks to the header.
  const fixedHeight = resizable && size.h != null && !collapsed ? size.h : null;
  return (
    <div
      ref={panelRef}
      style={{
        position: "fixed",
        top: pos.y,
        left: pos.x,
        zIndex: zIndex ?? ownZIndex,
        width: panelWidth,
        ...(fixedHeight != null ? { height: fixedHeight } : {}),
        background: "#15161d",
        border: "1px solid #2a2c3a",
        borderRadius: 14,
        fontFamily: "'Inter', 'Segoe UI', system-ui, sans-serif",
        color: "#e7e9ee",
        boxShadow: dragging ? "0 24px 60px rgba(0,0,0,.7), 0 0 0 1px rgba(79,140,255,.35)" : "0 20px 50px rgba(0,0,0,.55)",
        display: "flex",
        flexDirection: "column",
        transition: "box-shadow .12s",
      }}
    >
      <div
        onPointerDown={onHeaderPointerDown}
        onDoubleClick={onHeaderDoubleClick(onToggleCollapsed)}
        style={{
          display: "flex",
          alignItems: "center",
          justifyContent: "space-between",
          gap: 8,
          padding: PANEL_HEADER_PADDING,
          borderBottom: collapsed ? "none" : "1px solid #21232e",
          cursor: dragging ? "grabbing" : "grab",
          touchAction: "none",
          userSelect: "none",
          flexShrink: 0,
        }}
      >
        <span style={{ display: "flex", alignItems: "center", gap: 7, minWidth: 0, overflow: "hidden" }}>
          <span
            aria-hidden
            style={{ width: 6, height: 6, borderRadius: 999, background: titleColor, boxShadow: `0 0 8px ${titleColor}80`, flexShrink: 0 }}
          />
          <span style={{ fontWeight: 700, fontSize: 12.5, color: "#e7e9ee", overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>
            {title}
          </span>
        </span>
        <div style={{ display: "flex", alignItems: "center", gap: 2, flexShrink: 0 }}>
          {headerExtra}
          <button onClick={onToggleCollapsed} title={collapsed ? "Expand" : "Collapse"} style={hudIconBtnStyle}>
            {collapsed ? "+" : "–"}
          </button>
          {onClose && (
            <button onClick={onClose} title="Close" style={hudIconBtnStyle}>
              ×
            </button>
          )}
        </div>
      </div>
      {/* When resizable, the scroll area stops short of the bottom edge so its scrollbar never sits
          under the resize grip (a native scrollbar wins the pointer over an overlapping element). */}
      {!collapsed &&
        (fixedHeight != null ? (
          <div style={{ ...bodyPadding(resizable), flex: 1, minHeight: 0, overflowY: "auto" }}>{children}</div>
        ) : (
          <div style={{ ...bodyPadding(resizable), maxHeight, overflowY: maxHeight ? "auto" : undefined }}>{children}</div>
        ))}
      {resizable && !collapsed && <ResizeHandle onPointerDown={onResizePointerDown} />}
    </div>
  );
}

function bodyPadding(resizable: boolean): React.CSSProperties {
  return resizable ? { padding: "12px 12px 4px", marginBottom: 12 } : { padding: "12px" };
}

export const hudIconBtnStyle: React.CSSProperties = {
  background: "none",
  border: 0,
  borderRadius: 4,
  color: "#5b5e6e",
  cursor: "pointer",
  fontSize: 14,
  lineHeight: 1,
  width: 18,
  height: 18,
  display: "flex",
  alignItems: "center",
  justifyContent: "center",
};
