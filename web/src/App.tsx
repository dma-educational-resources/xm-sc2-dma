import { useEffect, useMemo, useRef } from "react";
import { formatClock, RadarClient } from "./radar";
import { useViewSettings } from "./settings";
import { useTick } from "./useTick";
import { Viewer2D, type Viewer2DHandle } from "./Viewer2D";
import { NavDock } from "./navDock/NavDock";
import { PlayersWidget } from "./widgets/PlayersWidget";
import { EventsWidget } from "./widgets/EventsWidget";

/** The SC2 web radar: the EFT web radar's layout (icon rail on the left, the map filling the rest, floating panels and
 * widgets over it), fed by the desktop radar's own local server. */
export default function App() {
  const client = useMemo(() => new RadarClient(), []);
  useEffect(() => {
    client.start();
    return () => client.stop();
  }, [client]);
  const [settings, setSettings] = useViewSettings();
  const viewer = useRef<Viewer2DHandle>(null);
  const tick = useTick(client, 250);

  const status =
    client.state === "lost" ? "Lost connection to the radar - is it still running?" : client.state === "connecting" ? "Connecting to the radar..." : tick && !tick.inGame ? tick.status : "";

  return (
    <div style={{ display: "flex", width: "100%", height: "100%", background: "#101116", color: "#e7e9ee", fontFamily: "'Inter', 'Segoe UI', system-ui, sans-serif" }}>
      <NavDock
        client={client}
        settings={settings}
        onSettingsChange={setSettings}
        onResetView={() => viewer.current?.resetView()}
        onCenterOn={(x, y) => {
          setSettings({ followSelf: false });
          viewer.current?.centerOn(x, y);
        }}
      />
      <div style={{ position: "relative", flex: 1, minWidth: 0, display: "flex" }}>
        <Viewer2D ref={viewer} client={client} settings={settings} />

        {status && (
          <div style={{ position: "absolute", inset: 0, display: "flex", alignItems: "center", justifyContent: "center", pointerEvents: "none" }}>
            <div style={{ background: "#15161d", border: "1px solid #2a2c3a", borderRadius: 12, padding: "12px 18px", fontSize: 13, color: client.state === "lost" ? "#f2555a" : "#c7cad1", boxShadow: "0 20px 50px rgba(0,0,0,.55)" }}>
              {status}
            </div>
          </div>
        )}

        {settings.showClock && tick?.inGame && (
          <div
            style={{
              position: "absolute",
              top: 10,
              left: "50%",
              transform: "translateX(-50%)",
              background: "#15161dd9",
              border: "1px solid #2a2c3a",
              borderRadius: 9,
              padding: "4px 12px",
              fontSize: 12.5,
              fontWeight: 600,
              fontVariantNumeric: "tabular-nums",
              display: "flex",
              gap: 10,
              alignItems: "center",
              pointerEvents: "none",
            }}
          >
            <span>{formatClock(tick.time ?? 0)}</span>
            {tick.map?.name && <span style={{ color: "#8b8ba7", fontWeight: 500 }}>{tick.map.name}</span>}
          </div>
        )}
      </div>

      {settings.showPlayersWidget && <PlayersWidget client={client} colorMode={settings.colorMode} onClose={() => setSettings({ showPlayersWidget: false })} />}
      {settings.showEventsWidget && <EventsWidget client={client} colorMode={settings.colorMode} onClose={() => setSettings({ showEventsWidget: false })} />}
    </div>
  );
}
