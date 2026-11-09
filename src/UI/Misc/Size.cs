using System.Text.Json.Serialization;

namespace SC2Radar.UI.Misc
{
    /// <summary>
    /// Simple width/height size struct compatible with System.Text.Json.
    /// </summary>
    public class Size
    {
        [JsonPropertyName("width")]
        public int Width { get; set; }

        [JsonPropertyName("height")]
        public int Height { get; set; }

        // Parameterless constructor required for System.Text.Json deserialization
        public Size() { }

        public Size(int width, int height)
        {
            Width  = width;
            Height = height;
        }
    }
}