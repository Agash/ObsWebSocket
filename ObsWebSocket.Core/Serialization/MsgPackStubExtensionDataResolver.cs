using System.Buffers;
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

    /// <summary>
    /// Every hand-written stub, in both its bare and its list form.
    /// </summary>
    /// <remarks>
    /// A table rather than the <c>typeof(T) ==</c> chain the other resolvers use, because here the
    /// two forms of each stub have to be registered together: registering the bare stub and
    /// forgetting the list is what made a response unreadable over MessagePack while JSON read it
    /// fine. One <see cref="Register{T}"/> call cannot express half a stub. Every instantiation is
    /// written out, so nothing here needs reflection.
    /// </remarks>
    private static readonly Dictionary<Type, object> s_formatters = BuildFormatters();

    public IMessagePackFormatter<T>? GetFormatter<T>() =>
        s_formatters.TryGetValue(typeof(T), out object? formatter)
            ? (IMessagePackFormatter<T>)formatter
            : null;

    private static Dictionary<Type, object> BuildFormatters()
    {
        Dictionary<Type, object> map = [];
        Register<SceneStub>(map);
        Register<SceneItemStub>(map);
        Register<SceneItemTransformStub>(map);
        Register<FilterStub>(map);
        Register<InputStub>(map);
        Register<InputVolumeMeterStub>(map);
        Register<SceneItemOrderStub>(map);
        Register<TransitionStub>(map);
        Register<OutputStub>(map);
        Register<MonitorStub>(map);
        Register<PropertyItemStub>(map);
        Register<CanvasStub>(map);
        Register<CanvasFlagsStub>(map);
        Register<CanvasVideoSettingsStub>(map);
        return map;
    }

    private static void Register<T>(Dictionary<Type, object> map)
        where T : class
    {
        map[typeof(T)] = new MsgPackJsonBridgeFormatter<T>();
        map[typeof(List<T>)] = new MsgPackJsonBridgeFormatter<List<T>>();
    }

    private sealed class MsgPackJsonBridgeFormatter<T> : IMessagePackFormatter<T?>
        where T : class
    {
        public void Serialize(
            ref MessagePackWriter writer,
            T? value,
            MessagePackSerializerOptions options
        )
        {
            if (value is null)
            {
                writer.WriteNil();
                return;
            }

            JsonTypeInfo<T> typeInfo =
                (JsonTypeInfo<T>)ObsWebSocketJsonContext.Default.Options.GetTypeInfo(typeof(T));
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

            JsonTypeInfo<T> typeInfo =
                (JsonTypeInfo<T>)ObsWebSocketJsonContext.Default.Options.GetTypeInfo(typeof(T));
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
