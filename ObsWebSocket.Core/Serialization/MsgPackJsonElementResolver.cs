using System.Buffers;
using System.Text.Json;
using MessagePack;
using MessagePack.Formatters;

namespace ObsWebSocket.Core.Serialization;

internal sealed class MsgPackJsonElementResolver : IFormatterResolver
{
    public static readonly IFormatterResolver Instance = new MsgPackJsonElementResolver();

    private MsgPackJsonElementResolver() { }

    public IMessagePackFormatter<T>? GetFormatter<T>() =>
        typeof(T) == typeof(JsonElement)
            ? (IMessagePackFormatter<T>)(object)JsonElementFormatter.Instance
        : typeof(T) == typeof(JsonElement?)
            ? (IMessagePackFormatter<T>)(object)NullableJsonElementFormatter.Instance
        // An array the protocol does not give an item type for becomes List<JsonElement>, and
        // nothing else in the resolver chain knows how to build one, so GetCanvasList could not
        // be read at all over MessagePack.
        : typeof(T) == typeof(List<JsonElement>)
            ? (IMessagePackFormatter<T>)(object)JsonElementListFormatter.Instance
        : null;

    internal sealed class JsonElementFormatter : IMessagePackFormatter<JsonElement>
    {
        public static readonly JsonElementFormatter Instance = new();

        public void Serialize(
            ref MessagePackWriter writer,
            JsonElement value,
            MessagePackSerializerOptions options
        )
        {
            if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                writer.WriteNil();
                return;
            }

            byte[] raw = MessagePackSerializer.ConvertFromJson(value.GetRawText());
            writer.WriteRaw(raw);
        }

        public JsonElement Deserialize(
            ref MessagePackReader reader,
            MessagePackSerializerOptions options
        )
        {
            if (reader.TryReadNil())
            {
                return default;
            }

            options.Security.DepthStep(ref reader);
            byte[] raw = ReadRawValue(ref reader);
            string json = MessagePackSerializer.ConvertToJson(raw);
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement result = document.RootElement.Clone();
            reader.Depth--;
            return result;
        }

        private static byte[] ReadRawValue(ref MessagePackReader reader)
        {
            SequencePosition start = reader.Position;
            MessagePackReader clone = reader;
            clone.Skip();
            SequencePosition end = clone.Position;
            ReadOnlySequence<byte> sequence = reader.Sequence.Slice(start, end);
            byte[] raw = new byte[checked((int)sequence.Length)];
            sequence.CopyTo(raw);
            reader = clone;
            return raw;
        }
    }

    internal sealed class NullableJsonElementFormatter : IMessagePackFormatter<JsonElement?>
    {
        public static readonly NullableJsonElementFormatter Instance = new();

        public void Serialize(
            ref MessagePackWriter writer,
            JsonElement? value,
            MessagePackSerializerOptions options
        )
        {
            if (
                !value.HasValue
                || value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            )
            {
                writer.WriteNil();
                return;
            }

            JsonElementFormatter.Instance.Serialize(ref writer, value.Value, options);
        }

        public JsonElement? Deserialize(
            ref MessagePackReader reader,
            MessagePackSerializerOptions options
        ) =>
            reader.TryReadNil()
                ? null
                : JsonElementFormatter.Instance.Deserialize(ref reader, options);
    }

    /// <summary>
    /// Reads and writes a list of <see cref="JsonElement"/>, which is what an array whose item
    /// type the protocol does not state is generated as.
    /// </summary>
    internal sealed class JsonElementListFormatter : IMessagePackFormatter<List<JsonElement>?>
    {
        public static readonly JsonElementListFormatter Instance = new();

        /// <inheritdoc/>
        public void Serialize(
            ref MessagePackWriter writer,
            List<JsonElement>? value,
            MessagePackSerializerOptions options
        )
        {
            if (value is null)
            {
                writer.WriteNil();
                return;
            }

            writer.WriteArrayHeader(value.Count);
            foreach (JsonElement item in value)
            {
                JsonElementFormatter.Instance.Serialize(ref writer, item, options);
            }
        }

        /// <inheritdoc/>
        public List<JsonElement>? Deserialize(
            ref MessagePackReader reader,
            MessagePackSerializerOptions options
        )
        {
            if (reader.TryReadNil())
            {
                return null;
            }

            options.Security.DepthStep(ref reader);
            int count = reader.ReadArrayHeader();
            List<JsonElement> items = new(count);
            for (int i = 0; i < count; i++)
            {
                items.Add(JsonElementFormatter.Instance.Deserialize(ref reader, options));
            }

            reader.Depth--;
            return items;
        }
    }
}
