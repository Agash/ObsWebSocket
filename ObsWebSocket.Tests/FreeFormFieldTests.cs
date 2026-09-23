using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using ObsWebSocket.Core;
using ObsWebSocket.Core.Protocol.Common.InputSettings;
using ObsWebSocket.Core.Protocol.Events;
using ObsWebSocket.Core.Protocol.Responses;
using ObsWebSocket.Tests.Fakes;

namespace ObsWebSocket.Tests;

/// <summary>
/// The fields the protocol leaves free-form: every one an event or response carries has a typed
/// reader, and every one a request carries has a typed helper.
/// </summary>
[TestClass]
public sealed class FreeFormFieldTests
{
    private const int TestTimeout = 30_000;

    private static IEnumerable<(Type Payload, PropertyInfo Field)> FreeFormFields() =>
        typeof(ObsWebSocketClient)
            .Assembly.GetExportedTypes()
            .Where(t =>
                t.Namespace
                    is "ObsWebSocket.Core.Protocol.Responses"
                        or "ObsWebSocket.Core.Protocol.Events"
            )
            .SelectMany(t =>
                t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.PropertyType == typeof(JsonElement?))
                    .Select(p => (t, p))
            );

    [TestMethod]
    public void Readers_EveryInboundFreeFormField_HasBothOverloads()
    {
        MethodInfo[] readers = typeof(ObsWebSocketFreeFormFields).GetMethods(
            BindingFlags.Public | BindingFlags.Static
        );
        List<(Type Payload, PropertyInfo Field)> fields = [.. FreeFormFields()];

        Assert.IsGreaterThan(10, fields.Count, "the protocol has many free-form fields");

        foreach ((Type payload, PropertyInfo field) in fields)
        {
            MethodInfo[] forField =
            [
                .. readers.Where(m =>
                    m.Name == $"Get{field.Name}"
                    && m.IsGenericMethodDefinition
                    && m.GetParameters()[0].ParameterType == payload
                ),
            ];

            Assert.HasCount(
                2,
                forField,
                $"{payload.Name}.{field.Name} needs a registered and an explicit reader"
            );
        }
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task GetInputSettings_EventCarriesSettings_ReadsThemBothWays()
    {
        FakeObsServer server = new();
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        TaskCompletionSource<InputSettingsChangedPayload> changed = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        fake.Client.InputSettingsChanged += (_, e) => changed.TrySetResult(e.EventData);

        server.RaiseEvent(
            "InputSettingsChanged",
            """{"inputName":"Web","inputUuid":"u","inputSettings":{"url":"https://example.com","css":"body{}"}}"""
        );

        InputSettingsChangedPayload payload = await changed.Task.WaitAsync(
            TimeSpan.FromSeconds(10)
        );

        Assert.AreEqual(
            "https://example.com",
            payload.GetInputSettings<BrowserSourceSettings>()?.Url
        );
        Assert.AreEqual(
            "body{}",
            payload.GetInputSettings(FreeFormContext.Default.OverlayCue)?.Css
        );
    }

    [TestMethod]
    public void GetInputSettings_Absent_ReturnsNull()
    {
        InputSettingsChangedPayload payload = new()
        {
            InputName = "Web",
            InputUuid = "u",
            InputSettings = null,
        };

        Assert.IsNull(payload.GetInputSettings<BrowserSourceSettings>());
        Assert.IsNull(payload.GetInputSettings(FreeFormContext.Default.OverlayCue));
        Assert.IsNull(
            (
                payload with
                {
                    InputSettings = JsonDocument.Parse("null").RootElement.Clone(),
                }
            ).GetInputSettings<BrowserSourceSettings>()
        );
    }

    [TestMethod]
    public void GetInputSettings_WrongShape_ThrowsSerialization()
    {
        InputSettingsChangedPayload payload = new()
        {
            InputName = "Web",
            InputUuid = "u",
            InputSettings = JsonDocument.Parse("""{"url":42}""").RootElement.Clone(),
        };

        ObsWebSocketSerializationException error =
            Assert.ThrowsExactly<ObsWebSocketSerializationException>(() =>
                payload.GetInputSettings<BrowserSourceSettings>()
            );
        Assert.Contains("inputSettings", error.Message);
    }

    [TestMethod]
    public void GetInputSettings_UnregisteredTypeWithoutMetadata_Throws()
    {
        InputSettingsChangedPayload payload = new()
        {
            InputName = "Web",
            InputUuid = "u",
            InputSettings = JsonDocument.Parse("{}").RootElement.Clone(),
        };

        _ = Assert.ThrowsExactly<ObsWebSocketException>(() =>
            payload.GetInputSettings<OverlayCue>()
        );
        _ = Assert.ThrowsExactly<ArgumentNullException>(() =>
            payload.GetInputSettings<OverlayCue>(null!)
        );
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task PersistentDataAsync_TypedValue_RoundTrips()
    {
        FakeObsServer server = new();
        string? stored = null;
        _ = server.OnRequest(
            "SetPersistentData",
            data =>
            {
                stored = data?.GetProperty("slotValue").GetRawText();
                return FakeObsServer.RequestOutcome.Success();
            }
        );
        _ = server.OnRequest(
            "GetPersistentData",
            _ => FakeObsServer.RequestOutcome.Success($$"""{"slotValue":{{stored ?? "null"}}}""")
        );
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);
        const string realm = "OBS_WEBSOCKET_DATA_REALM_GLOBAL";

        Assert.IsNull(
            await fake.Client.Config.GetPersistentDataAsync(
                realm,
                "cue",
                FreeFormContext.Default.OverlayCue
            ),
            "an empty slot reads as nothing"
        );

        await fake.Client.Config.SetPersistentDataAsync(
            realm,
            "cue",
            new OverlayCue("lights", "body{}"),
            FreeFormContext.Default.OverlayCue
        );
        OverlayCue? read = await fake.Client.Config.GetPersistentDataAsync(
            realm,
            "cue",
            FreeFormContext.Default.OverlayCue
        );

        Assert.AreEqual(new OverlayCue("lights", "body{}"), read);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task CallVendorRequestAsync_TypedBothWays_SendsAndReads()
    {
        FakeObsServer server = new();
        string? sentName = null;
        _ = server.OnRequest(
            "CallVendorRequest",
            data =>
            {
                sentName = data?.GetProperty("requestData").GetProperty("name").GetString();
                return FakeObsServer.RequestOutcome.Success(
                    """{"vendorName":"cues","requestType":"Fire","responseData":{"name":"done","css":null}}"""
                );
            }
        );
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        OverlayCue? reply = await fake.Client.General.CallVendorRequestAsync(
            "cues",
            "Fire",
            new OverlayCue("lights", null),
            FreeFormContext.Default.OverlayCue,
            FreeFormContext.Default.OverlayCue
        );

        Assert.AreEqual("lights", sentName);
        Assert.AreEqual("done", reply?.Name);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task BroadcastCustomEventAsync_TypedPayload_ReachesSubscribers()
    {
        FakeObsServer server = new();
        _ = server.OnRequest(
            "BroadcastCustomEvent",
            data =>
            {
                // OBS echoes the broadcast to every subscribed client, the sender included.
                server.RaiseEvent("CustomEvent", data!.Value.GetProperty("eventData").GetRawText());
                return FakeObsServer.RequestOutcome.Success(null);
            }
        );
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        TaskCompletionSource<OverlayCue?> received = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        fake.Client.CustomEvent += (_, e) =>
            received.TrySetResult(e.EventData.GetEventData(FreeFormContext.Default.OverlayCue));

        await fake.Client.General.BroadcastCustomEventAsync(
            new OverlayCue("lights", null),
            FreeFormContext.Default.OverlayCue
        );

        Assert.AreEqual("lights", (await received.Task.WaitAsync(TimeSpan.FromSeconds(10)))?.Name);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task FreeFormHelpers_Unserializable_ThrowBeforeSending()
    {
        FakeObsServer server = new();
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);
        ObsWebSocketClient client = fake.Client;
        BrokenSettings broken = new(new Unwritable());
        var typeInfo = BrokenSettingsContext.Default.BrokenSettings;

        _ = await Assert.ThrowsExactlyAsync<ObsWebSocketSerializationException>(() =>
            client.Config.SetPersistentDataAsync(
                "OBS_WEBSOCKET_DATA_REALM_GLOBAL",
                "s",
                broken,
                typeInfo
            )
        );
        _ = await Assert.ThrowsExactlyAsync<ObsWebSocketSerializationException>(() =>
            client.General.CallVendorRequestAsync("v", "t", broken, typeInfo, typeInfo)
        );
        _ = await Assert.ThrowsExactlyAsync<ObsWebSocketSerializationException>(() =>
            client.General.BroadcastCustomEventAsync(broken, typeInfo)
        );

        Assert.IsEmpty(server.Requests);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task FreeFormHelpers_NotConnected_Throw()
    {
        await using FakeObsClient fake = FakeObsClient.Build(new FakeObsServer());
        ObsWebSocketClient client = fake.Client;
        var cue = FreeFormContext.Default.OverlayCue;

        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            client.Config.GetPersistentDataAsync("OBS_WEBSOCKET_DATA_REALM_GLOBAL", "s", cue)
        );
        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            client.General.BroadcastCustomEventAsync(new OverlayCue(null, null), cue)
        );
    }
}

/// <summary>A payload shape a consumer would define.</summary>
/// <param name="Name">What the cue is called.</param>
/// <param name="Css">Styling to apply.</param>
internal sealed record OverlayCue(
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("css")] string? Css = null
);

[JsonSerializable(typeof(OverlayCue))]
internal sealed partial class FreeFormContext : JsonSerializerContext { }
