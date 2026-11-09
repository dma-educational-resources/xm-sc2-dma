using System.Text.Json.Serialization;

namespace SC2Radar.Common.Misc.Data
{
    [Obfuscation(Exclude = true, ApplyToMembers = true)]
    public enum HotkeyMode
    {
        Toggle = 0,
        OnKey = 1
    }

    public class HotkeyActionModel
    {
        public string Name { get; set; }  // Display name like "Toggle ESP"
        public string Key { get; set; }   // Internal config key like "ToggleESP"
    }

    /// <summary>
    /// Individual hotkey entry for each action.
    /// </summary>
    public sealed class HotkeyEntry
    {
        /// <summary>
        /// If the hotkey is enabled.
        /// </summary>
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// Hotkey trigger mode: Toggle or OnKey (Hold).
        /// </summary>
        [JsonPropertyName("mode")]
        public HotkeyMode Mode { get; set; } = HotkeyMode.Toggle;

        [JsonPropertyName("keys")]
        public List<int> Keys { get; set; } = new();

        /// <summary>
        /// Virtual keycode (int) for the hotkey.
        /// </summary>
        [JsonPropertyName("key")]
        public int Key { get; set; } = -1;

        /// <summary>
        /// Per-key mode overrides. Keys absent from this dict fall back to <see cref="Mode"/>.
        /// </summary>
        [JsonPropertyName("keyModes")]
        public Dictionary<int, HotkeyMode> KeyModes { get; set; } = new();

        /// <summary>
        /// Human-readable UI label set each frame by UI.Toggle - used to match
        /// this entry against the conflict map for accurate self-exclusion.
        /// Not serialised; populated at render time.
        /// </summary>
        [JsonIgnore]
        public string DisplayLabel { get; set; }

        /// <summary>Returns the effective mode for a specific key, falling back to <see cref="Mode"/>.</summary>
        public HotkeyMode GetKeyMode(int vk) =>
            KeyModes != null && KeyModes.TryGetValue(vk, out var m) ? m : Mode;

        /// <summary>
        /// Removes a specific keycode from the Keys list and its per-key mode override.
        /// </summary>
        public void RemoveKey(int keyCode)
        {
            Keys.Remove(keyCode);
            KeyModes?.Remove(keyCode);
        }

        /// <summary>
        /// Adds a key if not already present.
        /// </summary>
        public void AddKey(int keyCode)
        {
            if (keyCode > 0 && !Keys.Contains(keyCode))
                Keys.Add(keyCode);
        }
    }
}
