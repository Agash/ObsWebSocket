using System.Buffers;
using System.Text.Json;
using MessagePack;
using MessagePack.Formatters;

namespace ObsWebSocket.Core.Serialization;

internal sealed class MsgPackJsonElementResolver : IFormatterResolver
{
    public static readonly IFormatterResolver Instance = new MsgPackJsonElementResolver();

    private MsgPackJsonElementResolver() { }

    public IMessagePackFormatter<T>? GetFormatter<T>()
    {
        if (typeof(T) == typeof(JsonElement))
        {
            return (IMessagePackFormatter<T>)(object)JsonElementFormatter.Instance;
        }

        return typeof(T) == typeof(JsonElement?)
            ? (IMessagePackFormatter<T>)(object)NullableJsonElementFormatter.Instance
            : null;
    }

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
            if (!value.HasValue || value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                writer.WriteNil();
                return;
            }

            JsonElementFormatter.Instance.Serialize(ref writer, value.Value, options);
        }

        public JsonElement? Deserialize(
            ref MessagePackReader reader,
            MessagePackSerializerOptions options
        )
        {
            if (reader.TryReadNil())
            {
                return null;
            }

            return JsonElementFormatter.Instance.Deserialize(ref reader, options);
        }
    }
}
