// Converters.cs
// WPF IValueConverter classes are removed -ImGui renders UI directly without data binding.
// Only the non-WPF utility methods are kept (icon loading via SkiaSharp).

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using SkiaSharp;

namespace SC2Radar.UI.Misc
{
    /// <summary>
    /// Handles Dictionary&lt;TEnum, TValue&gt; deserialization, silently skipping any dictionary
    /// key that no longer maps to a valid enum value. This prevents a single removed/renamed
    /// enum member from causing the entire dictionary (and anything that contains it) to be
    /// dropped during config recovery, which would reset all user customisations to defaults.
    /// </summary>
    internal sealed class SafeEnumKeyDictionaryConverterFactory : JsonConverterFactory
    {
        public static readonly SafeEnumKeyDictionaryConverterFactory Instance = new();

        public override bool CanConvert(Type typeToConvert) =>
            typeToConvert.IsGenericType &&
            typeToConvert.GetGenericTypeDefinition() == typeof(Dictionary<,>) &&
            typeToConvert.GetGenericArguments()[0].IsEnum;

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        {
            var args        = typeToConvert.GetGenericArguments();
            var converterType = typeof(SafeEnumKeyDictionaryConverter<,>).MakeGenericType(args[0], args[1]);
            return (JsonConverter)Activator.CreateInstance(converterType)!;
        }
    }

    internal sealed class SafeEnumKeyDictionaryConverter<TKey, TValue> : JsonConverter<Dictionary<TKey, TValue>>
        where TKey : struct, Enum
    {
        public override Dictionary<TKey, TValue> Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var result = new Dictionary<TKey, TValue>();
            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException("Expected start of object");

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) return result;
                if (reader.TokenType != JsonTokenType.PropertyName)
                    throw new JsonException("Expected property name");

                var keyStr = reader.GetString();
                reader.Read(); // advance to value token

                if (Enum.TryParse<TKey>(keyStr, ignoreCase: true, out var key))
                    result[key] = JsonSerializer.Deserialize<TValue>(ref reader, options)!;
                else
                    reader.Skip(); // silently drop obsolete / unknown enum key
            }
            return result;
        }

        public override void Write(
            Utf8JsonWriter writer, Dictionary<TKey, TValue> value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            foreach (var kvp in value)
            {
                writer.WritePropertyName(kvp.Key.ToString());
                JsonSerializer.Serialize(writer, kvp.Value, options);
            }
            writer.WriteEndObject();
        }
    }


    /// <summary>
    /// Utility methods for loading item icons as SkiaSharp bitmaps.
    /// The WPF IValueConverter implementations are removed -ImGui draws icons directly.
    /// </summary>
    public static class Converters
    {
        /// <summary>
        /// Load an item icon as an SKBitmap from the local icon cache.
        /// Returns null if the icon is not found.
        /// </summary>
        public static SKBitmap? LoadItemIcon(string itemId, string iconCachePath)
        {
            try
            {
                var path = Path.Combine(iconCachePath, $"{itemId}.png");
                if (!File.Exists(path)) return null;
                return SKBitmap.Decode(path);
            }
            catch { return null; }
        }

        public static class ItemIconConverter
        {
            private static readonly HttpClient _http = new()
            {
                Timeout = TimeSpan.FromSeconds(15)
            };

            /// <summary>
            /// Downloads the item's base image from assets.tarkov.dev (WebP) and
            /// saves it as a PNG in <paramref name="outputDir"/>.
            /// Skips if a valid PNG (&gt;1 KB) already exists.
            /// </summary>
            public static async Task SaveItemIconAsPng(string itemId, string outputDir, string itemName = null)
            {
                string outputPath = Path.Combine(outputDir, $"{itemId}.png");

                // Skip re-download if a valid icon already exists.
                if (File.Exists(outputPath) && new FileInfo(outputPath).Length > 1024)
                    return;

                string url = $"https://assets.tarkov.dev/{itemId}-base-image.webp";
                try
                {
                    byte[] imageBytes = await _http.GetByteArrayAsync(url).ConfigureAwait(false);

                    using var ms     = new MemoryStream(imageBytes);
                    using var codec  = SKCodec.Create(ms);
                    if (codec == null) return;

                    var info   = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888);
                    using var bmp = new SKBitmap(info);
                    if (codec.GetPixels(info, bmp.GetPixels()) != SKCodecResult.Success) return;

                    using var img  = SKImage.FromBitmap(bmp);
                    using var data = img.Encode(SKEncodedImageFormat.Png, 100);
                    using var fs   = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    data.SaveTo(fs);
                }
                catch (Exception ex)
                {
                    var label = string.IsNullOrEmpty(itemName) ? itemId : $"({itemName}) {itemId}";
                    System.Diagnostics.Debug.WriteLine($"[IconCache] Failed {label}: {ex.Message}");
                    try
                    {
                        byte[] fallbackBytes = await _http.GetByteArrayAsync("https://assets.tarkov.dev/unknown-item-512.webp").ConfigureAwait(false);
                        using var ms    = new MemoryStream(fallbackBytes);
                        using var codec = SKCodec.Create(ms);
                        if (codec != null)
                        {
                            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888);
                            using var bmp = new SKBitmap(info);
                            if (codec.GetPixels(info, bmp.GetPixels()) == SKCodecResult.Success)
                            {
                                using var img  = SKImage.FromBitmap(bmp);
                                using var data = img.Encode(SKEncodedImageFormat.Png, 100);
                                using var fs   = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
                                data.SaveTo(fs);
                            }
                        }
                    }
                    catch { }
                }
            }
        }
    }
}
