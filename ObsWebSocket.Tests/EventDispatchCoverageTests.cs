using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ObsWebSocket.Core;
using ObsWebSocket.Tests.Fakes;

namespace ObsWebSocket.Tests;

/// <summary>
/// Every event the protocol defines reaches its public event through the real receive path. The
/// receive loop drops what it cannot place, so an event missing from dispatch would otherwise go
/// unnoticed.
/// </summary>
[TestClass]
public sealed class EventDispatchCoverageTests
{
    private const int TestTimeout = 30_000;

    private static readonly JsonElement[] s_events = ReadEvents();

    /// <summary>String fields the client reads as enums, so a placeholder would not parse.</summary>
    private static readonly Dictionary<string, string> s_enumSamples = new(StringComparer.Ordinal)
    {
        ["outputState"] = "OBS_WEBSOCKET_OUTPUT_STARTED",
        ["mediaAction"] = "OBS_WEBSOCKET_MEDIA_INPUT_ACTION_PLAY",
        ["monitorType"] = "OBS_MONITORING_TYPE_NONE",
    };

    /// <summary>Object fields the client models as a typed record with required members.</summary>
    private static readonly Dictionary<string, string> s_objectSamples = new(StringComparer.Ordinal)
    {
        ["sceneItemTransform"] =
            """{"positionX":0,"positionY":0,"rotation":0,"scaleX":1,"scaleY":1,"width":0,"height":0,"sourceWidth":0,"sourceHeight":0,"alignment":5,"boundsType":"OBS_BOUNDS_NONE","boundsAlignment":0,"boundsWidth":0,"boundsHeight":0,"cropLeft":0,"cropTop":0,"cropRight":0,"cropBottom":0}""",
    };

    public static IEnumerable<object[]> EventTypes =>
        s_events.Select(e => new object[] { e.GetProperty("eventType").GetString()! });

    [TestMethod]
    public void Protocol_Loaded_DefinesEvents() => Assert.IsGreaterThan(50, s_events.Length);

    [TestMethod]
    [Timeout(TestTimeout)]
    [DynamicData(nameof(EventTypes))]
    public async Task EventDispatch_EveryProtocolEvent_RaisesPublicEvent(string eventType)
    {
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(new FakeObsServer());

        EventInfo publicEvent =
            typeof(ObsWebSocketClient).GetEvent(eventType)
            ?? throw new AssertFailedException($"No public event is declared for {eventType}.");

        TaskCompletionSource<object> raised = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        Delegate handler = Delegate.CreateDelegate(
            publicEvent.EventHandlerType!,
            raised,
            typeof(EventDispatchCoverageTests)
                .GetMethod(nameof(Capture), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(publicEvent.EventHandlerType!.GetGenericArguments()[0])
        );
        publicEvent.AddEventHandler(fake.Client, handler);

        fake.Server.RaiseEvent(eventType, SamplePayload(eventType).ToJsonString());

        object args = await raised.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsInstanceOfType(args, publicEvent.EventHandlerType.GetGenericArguments()[0]);
    }

    private static void Capture<TArgs>(TaskCompletionSource<object> raised, object? _, TArgs e)
        where TArgs : notnull => raised.TrySetResult(e);

    /// <summary>Builds an event payload that satisfies every field the protocol lists.</summary>
    private static JsonObject SamplePayload(string eventType)
    {
        JsonElement definition = s_events.Single(e =>
            e.GetProperty("eventType").GetString() == eventType
        );
        JsonObject payload = [];

        if (!definition.TryGetProperty("dataFields", out JsonElement fields))
        {
            return payload;
        }

        foreach (JsonElement field in fields.EnumerateArray())
        {
            string name = field.GetProperty("valueName").GetString()!;
            string type = field.GetProperty("valueType").GetString()!;

            // Dotted names describe a nested object.
            JsonObject target = payload;
            string[] path = name.Split('.');
            foreach (string segment in path[..^1])
            {
                target =
                    target[segment] as JsonObject
                    ?? (JsonObject)(target[segment] = new JsonObject());
            }

            string leaf = path[^1];
            target[leaf] = type switch
            {
                "Number" => 1,
                "Boolean" => true,
                "Object" => s_objectSamples.TryGetValue(leaf, out string? shape)
                    ? JsonNode.Parse(shape)
                    : new JsonObject(),
                _ when type.StartsWith("Array", StringComparison.Ordinal) => new JsonArray(),
                _ => s_enumSamples.TryGetValue(leaf, out string? sample) ? sample : "sample",
            };
        }

        return payload;
    }

    private static JsonElement[] ReadEvents()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "protocol.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return
        [
            .. document
                .RootElement.GetProperty("events")
                .EnumerateArray()
                .Select(e => e.Clone())
                .OrderBy(e => e.GetProperty("eventType").GetString(), StringComparer.Ordinal),
        ];
    }
}
