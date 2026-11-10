using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

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
}
