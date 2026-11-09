import type { ReactNode } from "react";
import { onHeaderDoubleClick, PANEL_HEADER_PADDING, usePersistedCollapsed } from "../ui/HudPanel";
import { useDraggablePanel, type DragPos } from "../ui/useDraggablePanel";
import { ResizeHandle, useResizableBox } from "../ui/useResizableBox";

/** The EFT web radar's floating widget chrome (its PlayersWidget / LootWidget): drag by the header, collapse, close,
 * resize from the corner; position, size and collapse remembered per widget. */
export function FloatingWidget({
  id,
  title,
  count,
  defaultPos,
  width = 340,
  onClose,
  children,
}: {
  id: string;
  title: string;
  count?: number;
  defaultPos: DragPos;
  width?: number;
  onClose: () => void;
  children: ReactNode;
}) {
  const [collapsed, setCollapsed] = usePersistedCollapsed(`xmsc2web-${id}-collapsed`, false);
  const { pos, panelRef, onHeaderPointerDown, zIndex } = useDraggablePanel(`xmsc2web-${id}-pos`, defaultPos);
  const { size, onResizePointerDown } = useResizableBox(`xmsc2web-${id}-size`, { w: width, h: null }, { minW: 200, maxW: 900, minH: 120, maxH: 2000 }, { panelRef });
  const iconBtn = { background: "none", border: 0, color: "#8b8ba7", cursor: "pointer", lineHeight: 1 } as const;

  return (
    <div
      ref={panelRef}
      style={{
        position: "fixed",
        top: pos.y,
        left: pos.x,
        zIndex,
        width: size.w,
        ...(size.h != null && !collapsed ? { height: size.h } : { maxHeight: "60vh" }),
        display: "flex",
        flexDirection: "column",
        background: "#14141b",
        border: "1px solid #2c2c42",
        borderRadius: 12,
        fontFamily: "'Inter', 'Segoe UI', system-ui, sans-serif",
        color: "#e5e7eb",
        boxShadow: "0 10px 40px rgba(0,0,0,.55)",
      }}
    >
      <div
        onPointerDown={onHeaderPointerDown}
        onDoubleClick={onHeaderDoubleClick(() => setCollapsed(!collapsed))}
        style={{
          display: "flex",
          alignItems: "center",
          justifyContent: "space-between",
          padding: PANEL_HEADER_PADDING,
          borderBottom: collapsed ? "none" : "1px solid #26263a",
          cursor: "grab",
          touchAction: "none",
          userSelect: "none",
          flexShrink: 0,
        }}
      >
        <span style={{ fontWeight: 700, fontSize: 12.5, color: "#60a5fa" }}>
          {title} {count != null && count > 0 && <span style={{ color: "#8b8ba7", fontWeight: 400 }}>({count})</span>}
        </span>
        <span style={{ display: "flex", alignItems: "center", gap: 6 }}>
          <button onClick={() => setCollapsed(!collapsed)} title={collapsed ? "Expand" : "Collapse"} style={{ ...iconBtn, fontSize: 16 }}>
            {collapsed ? "+" : "–"}
          </button>
          <button onClick={onClose} title="Close" style={{ ...iconBtn, fontSize: 15 }}>
            ×
          </button>
        </span>
      </div>
      {!collapsed && <div style={{ overflowY: "auto", flex: 1, minHeight: 0, marginBottom: 12 }}>{children}</div>}
      {!collapsed && <ResizeHandle onPointerDown={onResizePointerDown} />}
    </div>
  );
}
