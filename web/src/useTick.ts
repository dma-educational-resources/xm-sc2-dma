import { useEffect, useState } from "react";
import type { RadarClient, RadarTick } from "./radar";

/** The client's latest tick, re-rendering at most every `everyMs` (lists and widgets don't need 10+ updates a second). */
export function useTick(client: RadarClient, everyMs = 250): RadarTick | null {
  const [tick, setTick] = useState<RadarTick | null>(client.latest);
  useEffect(() => {
    let last = 0;
    let timer: ReturnType<typeof setTimeout> | null = null;
    const push = () => {
      timer = null;
      last = performance.now();
      setTick(client.latest);
    };
    const unsub = client.subscribe(() => {
      if (timer) return;
      const wait = everyMs - (performance.now() - last);
      if (wait <= 0) push();
      else timer = setTimeout(push, wait);
    });
    return () => {
      unsub();
      if (timer) clearTimeout(timer);
    };
  }, [client, everyMs]);
  return tick;
}
