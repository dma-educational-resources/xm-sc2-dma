using System.Text.Json.Serialization;

namespace SC2Radar.Common.Misc.Config
{
    public sealed class MapLayerOverride
    {
        [JsonPropertyName("minHeight")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? MinHeight { get; set; }

        [JsonPropertyName("maxHeight")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? MaxHeight { get; set; }

        [JsonPropertyName("dimBaseLayer")]
        public bool DimBaseLayer { get; set; } = true;

        [JsonPropertyName("zones")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<object>? Zones { get; set; }
    }

    public sealed class MapConfigOverride
    {
        [JsonPropertyName("x")]
        public float X { get; set; }

        [JsonPropertyName("y")]
        public float Y { get; set; }

        [JsonPropertyName("scale")]
        public float Scale { get; set; }

        [JsonPropertyName("disableDimming")]
        public bool DisableDimming { get; set; }

        [JsonPropertyName("layers")]
        public List<MapLayerOverride> Layers { get; set; } = new();
    }
}
