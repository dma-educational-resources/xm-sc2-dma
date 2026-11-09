// Small localStorage helper shared by every persisted setting in App.tsx (render distance, player
// settings, loot filters, follow-camera config, x-ray, scene toggles, live radar URL/poll rate) --
// keybindSettings.ts already had its own bespoke load/save pair before this existed; this is the
// same idea generalized so every OTHER setting doesn't need to hand-roll it too.
const PREFIX = "xmsc2web-";

/** Merges saved data OVER `fallback` (not replacing it outright) so a newly-added field in an
 * object-shaped setting still gets its default value for users with an older save already in
 * localStorage, instead of ending up `undefined`. Primitives (numbers/booleans, e.g. render
 * distance or the x-ray toggle) can't be spread-merged, so those are just used as-is.
 *
 * Arrays (e.g. deselectedTaskIds: string[]) are a SEPARATE case, not the object-merge path above --
 * `typeof [] === "object"` is true in JS, so `{...fallback, ...parsed}` on two arrays doesn't
 * concatenate/replace them, it produces a plain `{0: "a", 1: "b", ...}` object with no `.includes`/
 * `.filter`/etc. Confirmed live as a real crash: any saved non-empty deselectedTaskIds got loaded
 * as that shape, then handed straight to the render worker's task-marker code, which called
 * `.includes` on it and threw on the very first frame. Arrays replace the fallback outright instead
 * (there's no per-field default to preserve in a list the way there is in a settings object). */
export function loadPersisted<T>(key: string, fallback: T): T {
  try {
    const raw = localStorage.getItem(PREFIX + key);
    if (!raw) return fallback;
    const parsed = JSON.parse(raw);
    if (Array.isArray(fallback)) {
      return (Array.isArray(parsed) ? parsed : fallback) as T;
    }
    if (typeof fallback === "object" && fallback !== null && typeof parsed === "object" && parsed !== null) {
      return { ...fallback, ...parsed };
    }
    return parsed;
  } catch {
    return fallback;
  }
}

/** Same idea as loadPersisted, but for a `Record<Category, Settings>` shape (e.g. playerSettings:
 * one settings object per PlayerCategory) where the missing-field backfill needs to happen ONE
 * level deeper too. loadPersisted's `{...fallback, ...parsed}` only merges at the top level -- a
 * category that already has a saved entry in localStorage gets that entry's object used wholesale,
 * so a field added to that category's settings shape AFTER the user's last save stays `undefined`
 * forever instead of picking up its default. Confirmed live: aimlineLength (added after players
 * had already saved playerSettings) rendering as the literal string "undefined" in the aimline-
 * length control for an already-saved category. Any category key `parsed` has that `fallback`
 * doesn't (e.g. a removed/renamed category from an old save) is dropped -- fallback's key set is
 * the current source of truth. */
export function loadPersistedByCategory<K extends string, V extends object>(key: string, fallback: Record<K, V>): Record<K, V> {
  const merged = loadPersisted(key, fallback);
  const result = {} as Record<K, V>;
  for (const k of Object.keys(fallback) as K[]) {
    const category = merged[k];
    result[k] = category && typeof category === "object" ? { ...fallback[k], ...category } : fallback[k];
  }
  return result;
}

export function savePersisted<T>(key: string, value: T): void {
  try {
    localStorage.setItem(PREFIX + key, JSON.stringify(value));
  } catch {
    // localStorage can throw (private browsing, quota) -- losing persistence isn't worth crashing over.
  }
}
