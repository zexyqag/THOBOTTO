using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using NetCord;
using NetCord.JsonModels;
using NetCord.Rest;

namespace THOBOTTO.Archive;

// Serialises NetCord's JSON models back to Discord's JSON. Some NetCord models (components, for one)
// have converters that only read, so models with their own converter are written here by their JSON
// property names instead; converters in the options beat the type's.
public static class RawJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // NetCord links a mention's member back to its user; write the link once, not forever.
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        Converters = { new ModelWriterFactory(), new SnowflakeWriter(), new PermissionsWriter() },
    };

    public static string Of(RestMessage message) => Of(((IJsonModel<JsonMessage>)message).JsonModel);

    public static string Of(JsonMessage message) => JsonSerializer.Serialize(message, Options);

    // Any other model, or an array of them: a channel, a role, a member, an emoji, a ban…
    public static string OfModel<T>(T model) where T : class => JsonSerializer.Serialize(model, Options);

    // The stored JSON with the fields a partial payload carries written over it, the rest kept.
    public static string Merge(string? stored, string partial)
    {
        if (stored is null || JsonNode.Parse(stored) is not JsonObject target)
            return partial;
        if (JsonNode.Parse(partial) is JsonObject source)
        {
            foreach (var (name, value) in source.ToList())
                target[name] = value?.DeepClone();
        }
        return target.ToJsonString();
    }

    // Ids as strings, like Discord: they don't fit a JavaScript number, so a numeric id would
    // silently change in any tool that reads the export with JavaScript.
    private sealed class SnowflakeWriter : JsonConverter<ulong>
    {
        public override ulong Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => throw new NotSupportedException("Archived JSON is only written.");

        public override void Write(Utf8JsonWriter writer, ulong value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToString());
    }

    // Permissions as Discord sends them, a string of the bits, for the same reason.
    private sealed class PermissionsWriter : JsonConverter<Permissions>
    {
        public override Permissions Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => throw new NotSupportedException("Archived JSON is only written.");

        public override void Write(Utf8JsonWriter writer, Permissions value, JsonSerializerOptions options)
            => writer.WriteStringValue(((ulong)value).ToString());
    }

    private sealed class ModelWriterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type type)
            => type.Namespace == typeof(JsonMessage).Namespace && type.GetCustomAttribute<JsonConverterAttribute>() is not null;

        public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options)
            => (JsonConverter)Activator.CreateInstance(typeof(ModelWriter<>).MakeGenericType(type))!;
    }

    private sealed class ModelWriter<T> : JsonConverter<T>
    {
        // By runtime type: components are subclasses (rows, buttons, menus) of the declared type.
        private static readonly ConcurrentDictionary<Type, (string Name, PropertyInfo Property)[]> Properties = new();

        private static (string Name, PropertyInfo Property)[] PropertiesOf(Type type) => Properties.GetOrAdd(type, t => t
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
            .Select(p => (p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.SnakeCaseLower.ConvertName(p.Name), p))
            .ToArray());

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => throw new NotSupportedException("Archived JSON is only written.");

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            foreach (var (name, property) in PropertiesOf(value!.GetType()))
            {
                if (property.GetValue(value) is not { } propertyValue)
                    continue;
                writer.WritePropertyName(name);
                JsonSerializer.Serialize(writer, propertyValue, property.PropertyType, options);
            }
            writer.WriteEndObject();
        }
    }
}
