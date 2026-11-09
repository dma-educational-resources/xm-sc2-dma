import type { ReactNode } from "react";
import { HudPanel, usePersistedCollapsed } from "../ui/HudPanel";
import type { DragPos } from "../ui/useDraggablePanel";
import type { ViewSettings } from "../settings";

/** What every dock panel gets from NavDock. */
export type DockPanelProps = {
  defaultPos: DragPos;
  onClose: () => void;
  settings: ViewSettings;
  onSettingsChange: (patch: Partial<ViewSettings>) => void;
};

/** A dock panel's chrome: HudPanel with its position and collapse state kept per panel. */
export function DockPanel({ id, title, defaultPos, onClose, width = 300, children }: { id: string; title: string; defaultPos: DragPos; onClose: () => void; width?: number; children: ReactNode }) {
  const [collapsed, setCollapsed] = usePersistedCollapsed(`xmsc2web-dock-${id}-collapsed`);
  return (
    <HudPanel
      storageKeyPos={`xmsc2web-dock-${id}-pos`}
      defaultPos={defaultPos}
      title={title}
      collapsed={collapsed}
      onToggleCollapsed={() => setCollapsed(!collapsed)}
      onClose={onClose}
      width={width}
      maxHeight="calc(100vh - 80px)"
    >
      <div className="flex flex-col gap-3">{children}</div>
    </HudPanel>
  );
}
