using System.Buffers;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using MessagePack;
using MessagePack.Formatters;
using ObsWebSocket.Core.Protocol.Common;

namespace ObsWebSocket.Core.Serialization;

internal sealed class MsgPackStubExtensionDataResolver : IFormatterResolver
{
    public static readonly IFormatterResolver Instance = new MsgPackStubExtensionDataResolver();

    private MsgPackStubExtensionDataResolver() { }

    public IMessagePackFormatter<T>? GetFormatter<T>()
    {
        Type type = typeof(T);
        if (type == typeof(SceneStub))
        {
            return (IMessagePackFormatter<T>)(object)new MsgPackJsonBridgeFormatter<SceneStub>();
        }

        if (type == typeof(SceneItemTransformStub))
        {
            return (IMessagePackFormatter<T>)(object)
                new MsgPackJsonBridgeFormatter<SceneItemTransformStub>();
        }

        if (type == typeof(SceneItemStub))
        {
            return (IMessagePackFormatter<T>)(object)new MsgPackJsonBridgeFormatter<SceneItemStub>();
        }

        if (type == typeof(FilterStub))
        {
            return (IMessagePackFormatter<T>)(object)new MsgPackJsonBridgeFormatter<FilterStub>();
        }

        if (type == typeof(InputStub))
        {
            return (IMessagePackFormatter<T>)(object)new MsgPackJsonBridgeFormatter<InputStub>();
        }

        if (type == typeof(TransitionStub))
        {
            return (IMessagePackFormatter<T>)(object)new MsgPackJsonBridgeFormatter<TransitionStub>();
        }

        if (type == typeof(List<SceneStub>))
        {
            return (IMessagePackFormatter<T>)(object)new MsgPackJsonBridgeFormatter<List<SceneStub>>();
        }

        if (type == typeof(List<SceneItemStub>))
        {
            return (IMessagePackFormatter<T>)(object)new MsgPackJsonBridgeFormatter<List<SceneItemStub>>();
        }

        if (type == typeof(List<FilterStub>))
        {
            return (IMessagePackFormatter<T>)(object)new MsgPackJsonBridgeFormatter<List<FilterStub>>();
        }

        if (type == typeof(List<InputStub>))
        {
            return (IMessagePackFormatter<T>)(object)new MsgPackJsonBridgeFormatter<List<InputStub>>();
        }

        if (type == typeof(List<TransitionStub>))
        {
            return (IMessagePackFormatter<T>)(object)new MsgPackJsonBridgeFormatter<List<TransitionStub>>();
        }

        if (type == typeof(List<OutputStub>))
        {
            return (IMessagePackFormatter<T>)(object)new MsgPackJsonBridgeFormatter<List<OutputStub>>();
        }

        if (type == typeof(List<MonitorStub>))
        {
            return (IMessagePackFormatter<T>)(object)new MsgPackJsonBridgeFormatter<List<MonitorStub>>();
        }

        if (type == typeof(List<PropertyItemStub>))
        {
            return (IMessagePackFormatter<T>)(object)
                new MsgPackJsonBridgeFormatter<List<PropertyItemStub>>();
        }

        return type == typeof(OutputStub)
            ? (IMessagePackFormatter<T>)(object)new MsgPackJsonBridgeFormatter<OutputStub>()
            : type == typeof(MonitorStub)
            ? (IMessagePackFormatter<T>)(object)new MsgPackJsonBridgeFormatter<MonitorStub>()
            : type == typeof(PropertyItemStub)
            ? (IMessagePackFormatter<T>)(object)
                new MsgPackJsonBridgeFormatter<PropertyItemStub>()
            : null;
    }

    private sealed class MsgPackJsonBridgeFormatter<T> : IMessagePackFormatter<T?>
        where T : class
    {
        public void Serialize(ref MessagePackWriter writer, T? value, MessagePackSerializerOptions options)
        {
            if (value is null)
            {
                writer.WriteNil();
                return;
            }

            JsonTypeInfo<T> typeInfo = (JsonTypeInfo<T>)
                ObsWebSocketJsonContext.Default.Options.GetTypeInfo(typeof(T));
            string json = JsonSerializer.Serialize(value, typeInfo);
            byte[] raw = MessagePackSerializer.ConvertFromJson(json);
            writer.WriteRaw(raw);
        }

        public T? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            if (reader.TryReadNil())
            {
                return null;
            }

            options.Security.DepthStep(ref reader);

            byte[] raw = ReadRawValue(ref reader);
            string json = MessagePackSerializer.ConvertToJson(raw);

            JsonTypeInfo<T> typeInfo = (JsonTypeInfo<T>)
                ObsWebSocketJsonContext.Default.Options.GetTypeInfo(typeof(T));
            T? result = JsonSerializer.Deserialize(json, typeInfo);

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
}
