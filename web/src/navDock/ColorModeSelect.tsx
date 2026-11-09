import type { ColorMode } from "../radar";
import { DockFieldLabel, DockSelect } from "../ui/navUi";

/** Player colours or alliance colours (you green, allies yellow, enemies red), or whatever the desktop radar uses. */
export function ColorModeSelect({ value, onChange }: { value: ColorMode; onChange: (v: ColorMode) => void }) {
  return (
    <div title="Each player in their own colour, or green for you, yellow for allies and red for enemies.">
      <DockFieldLabel>Colours</DockFieldLabel>
      <DockSelect
        value={value}
        onChange={(v) => onChange(v as ColorMode)}
        options={[
          { value: "radar", label: "Same As Radar" },
          { value: "player", label: "Player Colours" },
          { value: "alliance", label: "Alliance Colours (you / allies / enemies)" },
        ]}
      />
    </div>
  );
}
