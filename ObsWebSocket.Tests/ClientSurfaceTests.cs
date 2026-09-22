using System.Reflection;
using System.Text.Json;
using MessagePack;
using Microsoft.Extensions.Options;
using ObsWebSocket.Core;
using ObsWebSocket.Core.Events.Generated;
using ObsWebSocket.Core.Protocol;
using ObsWebSocket.Core.Protocol.Generated;
using ObsWebSocket.Core.Protocol.Responses;
using ObsWebSocket.Core.Serialization;
using ObsWebSocket.Tests.Fakes;

namespace ObsWebSocket.Tests;

/// <summary>
/// The smaller public surface around the client: option validation, the event and batch
/// conveniences, reading batch results, and the exception types callers catch.
/// </summary>
[TestClass]
public sealed class ClientSurfaceTests
{
    private const int TestTimeout = 30_000;

    #region Options validation

    [TestMethod]
    [DataRow(null, "ServerUri is required")]
    [DataRow("http://localhost:4455", "scheme must be ws or wss")]
    public void Validate_EndpointNotWebSocket_Fails(string? uri, string expected)
    {
        ObsWebSocketClientOptions options = new() { ServerUri = uri is null ? null : new Uri(uri) };

        ValidateOptionsResult result = new ObsWebSocketClientOptionsValidator().Validate(
            null,
            options
        );

        Assert.IsTrue(result.Failed);
        Assert.Contains(expected, result.FailureMessage!);
    }

    [TestMethod]
    [DataRow(nameof(ObsWebSocketClientOptions.HandshakeTimeoutMs), 0, "HandshakeTimeoutMs")]
    [DataRow(nameof(ObsWebSocketClientOptions.RequestTimeoutMs), 0, "RequestTimeoutMs")]
    [DataRow(
        nameof(ObsWebSocketClientOptions.InitialReconnectDelayMs),
        -1,
        "InitialReconnectDelayMs"
    )]
    [DataRow(nameof(ObsWebSocketClientOptions.MaxReconnectDelayMs), -5, "MaxReconnectDelayMs")]
    [DataRow(
        nameof(ObsWebSocketClientOptions.MaxIncomingMessageBytes),
        16,
        "MaxIncomingMessageBytes"
    )]
    public void Validate_NumberOutOfRange_Fails(string property, int value, string expected)
    {
        ObsWebSocketClientOptions options = new() { ServerUri = new Uri("ws://localhost:4455") };
        typeof(ObsWebSocketClientOptions).GetProperty(property)!.SetValue(options, value);

        ValidateOptionsResult result = new ObsWebSocketClientOptionsValidator().Validate(
            null,
            options
        );

        Assert.IsTrue(result.Failed);
        Assert.Contains(expected, result.FailureMessage!);
    }

    [TestMethod]
    public void Validate_ShrinkingBackoff_Fails()
    {
        ObsWebSocketClientOptions options = new()
        {
            ServerUri = new Uri("wss://localhost:4455"),
            ReconnectBackoffMultiplier = 0.5,
        };

        ValidateOptionsResult result = new ObsWebSocketClientOptionsValidator().Validate(
            null,
            options
        );

        Assert.Contains("ReconnectBackoffMultiplier", result.FailureMessage!);
    }

    [TestMethod]
    public void Validate_DefaultsWithEndpoint_Succeeds()
    {
        ValidateOptionsResult result = new ObsWebSocketClientOptionsValidator().Validate(
            null,
            new ObsWebSocketClientOptions { ServerUri = new Uri("ws://localhost:4455") }
        );

        Assert.IsTrue(result.Succeeded);
    }

    #endregion

    #region Event and batch conveniences

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task WaitForEventAsync_EachOverload_ReturnsArrivingEvent()
    {
        FakeObsServer server = new();
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);
        ObsWebSocketClient client = fake.Client;
        const string payload = """{"sceneName":"Live","sceneUuid":"p"}""";

        Task<CurrentProgramSceneChangedEventArgs> untimed =
            client.WaitForEventAsync<CurrentProgramSceneChangedEventArgs>();
        Task<CurrentProgramSceneChangedEventArgs> timed =
            client.WaitForEventAsync<CurrentProgramSceneChangedEventArgs>(TimeSpan.FromSeconds(10));
        Task<CurrentProgramSceneChangedEventArgs> filtered =
            client.WaitForEventAsync<CurrentProgramSceneChangedEventArgs>(e =>
                e.EventData.SceneName == "Live"
            );

        server.RaiseEvent("CurrentProgramSceneChanged", payload);

        foreach (
            Task<CurrentProgramSceneChangedEventArgs> wait in new[] { untimed, timed, filtered }
        )
        {
            Assert.AreEqual(
                "Live",
                (await wait.WaitAsync(TimeSpan.FromSeconds(10))).EventData.SceneName
            );
        }
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task WaitForEventAsync_UntimedAndCancelled_ThrowsOperationCanceled()
    {
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(new FakeObsServer());
        using CancellationTokenSource cancel = new();

        Task<CurrentProgramSceneChangedEventArgs> wait =
            fake.Client.WaitForEventAsync<CurrentProgramSceneChangedEventArgs>(cancel.Token);
        await cancel.CancelAsync();

        _ = await Assert.ThrowsAsync<OperationCanceledException>(() => wait);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task CallBatchAsync_BuiltInline_ResultsAddressableByReference()
    {
        FakeObsServer server = new();
        _ = server.Returns("GetStudioModeEnabled", """{"studioModeEnabled":true}""");
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        BatchRef<GetStudioModeEnabledResponseData>? studio = null;
        BatchResults results = await fake.Client.CallBatchAsync(batch =>
            studio = batch.Ui.GetStudioModeEnabled()
        );

        Assert.IsTrue(results.Get(studio!.Value).StudioModeEnabled);
        Assert.IsTrue(results.TryGet(studio.Value, out GetStudioModeEnabledResponseData? read));
        Assert.IsTrue(read!.StudioModeEnabled);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task CallBatchAsync_Parallel_RefusesTypedReads()
    {
        FakeObsServer server = new();
        _ = server.Returns("GetStudioModeEnabled", """{"studioModeEnabled":true}""");
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        ObsBatchBuilder builder = new();
        BatchRef<GetStudioModeEnabledResponseData> studio = builder.Ui.GetStudioModeEnabled();

        BatchResults results = await fake.Client.CallBatchAsync(
            builder,
            executionType: RequestBatchExecutionType.Parallel
        );

        // OBS mis-pairs payloads under parallel execution, so reading one is refused.
        _ = Assert.ThrowsExactly<ObsWebSocketException>(() => results.Get(studio));
        Assert.IsFalse(results.TryGet(studio, out GetStudioModeEnabledResponseData? _));
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task CallBatchAsync_NullBatch_Throws()
    {
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(new FakeObsServer());

        _ = await Assert.ThrowsExactlyAsync<ArgumentNullException>(() =>
            fake.Client.CallBatchAsync((ObsBatchBuilder)null!)
        );
        _ = await Assert.ThrowsExactlyAsync<ArgumentNullException>(() =>
            fake.Client.CallBatchAsync((Action<ObsBatchBuilder>)null!)
        );
    }

    #endregion

    #region Reading batch results

    private static RequestResponsePayload<object> Result(object? data, bool succeeded = true) =>
        new("GetStudioModeEnabled", "1", new RequestStatus(succeeded, succeeded ? 100 : 600), data);

    [TestMethod]
    public void GetData_AlreadyTyped_ReturnsSameInstance()
    {
        GetStudioModeEnabledResponseData typed = new() { StudioModeEnabled = true };

        Assert.AreSame(typed, Result(typed).GetData<GetStudioModeEnabledResponseData>());
    }

    [TestMethod]
    public void GetData_MessagePackBytes_Deserializes()
    {
        ReadOnlyMemory<byte> packed = MessagePackSerializer.ConvertFromJson(
            """{"studioModeEnabled":true}"""
        );

        Assert.IsTrue(
            Result(packed).GetRequiredData<GetStudioModeEnabledResponseData>().StudioModeEnabled
        );
    }

    [TestMethod]
    public void GetData_MessagePackWrongShape_ThrowsSerialization()
    {
        ReadOnlyMemory<byte> packed = MessagePackSerializer.ConvertFromJson(
            """{"studioModeEnabled":"not a bool"}"""
        );

        _ = Assert.ThrowsExactly<ObsWebSocketSerializationException>(() =>
            Result(packed).GetData<GetStudioModeEnabledResponseData>()
        );
        Assert.IsFalse(Result(packed).TryGetData(out GetStudioModeEnabledResponseData? _));
    }

    [TestMethod]
    public void GetData_UnreadableRuntimeType_ThrowsSerialization()
    {
        _ = Assert.ThrowsExactly<ObsWebSocketSerializationException>(() =>
            Result(new object()).GetData<GetStudioModeEnabledResponseData>()
        );
    }

    [TestMethod]
    public void GetData_AbsentPayload_ReturnsNull()
    {
        Assert.IsNull(Result(null).GetData<GetStudioModeEnabledResponseData>());
        Assert.IsNull(Result(default(JsonElement)).GetData<GetStudioModeEnabledResponseData>());
        Assert.IsNull(
            Result(JsonDocument.Parse("null").RootElement.Clone())
                .GetData<GetStudioModeEnabledResponseData>()
        );
        Assert.IsFalse(Result(null).TryGetData(out GetStudioModeEnabledResponseData? _));
    }

    [TestMethod]
    public void TryGetData_FailedResult_ReturnsFalse()
    {
        RequestResponsePayload<object> failed = Result(
            JsonDocument.Parse("""{"studioModeEnabled":true}""").RootElement.Clone(),
            succeeded: false
        );

        Assert.IsFalse(failed.TryGetData(out GetStudioModeEnabledResponseData? data));
        Assert.IsNull(data);
        _ = Assert.ThrowsExactly<ObsWebSocketRequestException>(() =>
            failed.GetRequiredData<GetStudioModeEnabledResponseData>()
        );
    }

    [TestMethod]
    public void GetData_JsonWrongShape_ThrowsSerialization()
    {
        RequestResponsePayload<object> wrong = Result(
            JsonDocument.Parse("""{"studioModeEnabled":"yes"}""").RootElement.Clone()
        );

        _ = Assert.ThrowsExactly<ObsWebSocketSerializationException>(() =>
            wrong.GetData<GetStudioModeEnabledResponseData>()
        );
        Assert.IsFalse(wrong.TryGetData(out GetStudioModeEnabledResponseData? _));
    }

    #endregion

    #region Exception types

    private static IEnumerable<Type> PublicExceptions() =>
        typeof(ObsWebSocketException)
            .Assembly.GetExportedTypes()
            .Where(t => typeof(Exception).IsAssignableFrom(t) && !t.IsAbstract);

    [TestMethod]
    public void PublicExceptions_StandardConstructors_CarryMessageAndCause()
    {
        InvalidOperationException cause = new("cause");

        foreach (Type type in PublicExceptions())
        {
            ConstructorInfo? withMessage = type.GetConstructor([typeof(string)]);
            ConstructorInfo? withCause =
                type.GetConstructor([typeof(string), typeof(Exception)])
                ?? type.GetConstructor([typeof(string), typeof(Exception)]);
            ConstructorInfo? empty = type.GetConstructor(Type.EmptyTypes);

            Assert.IsNotNull(withMessage, $"{type.Name} lacks a message constructor");
            Assert.IsNotNull(withCause, $"{type.Name} lacks a message and cause constructor");
            Assert.IsNotNull(empty, $"{type.Name} lacks a parameterless constructor");

            Exception fromMessage = (Exception)withMessage.Invoke(["broke"]);
            Assert.AreEqual("broke", fromMessage.Message, type.Name);

            Exception fromCause = (Exception)withCause.Invoke(["broke", cause]);
            Assert.AreSame(cause, fromCause.InnerException, type.Name);

            _ = (Exception)empty.Invoke([]);
        }
    }

    [TestMethod]
    public void SceneItemNotFoundException_Constructed_ExposesSceneAndSource()
    {
        InvalidOperationException cause = new("cause");

        SceneItemNotFoundException plain = new("missing", "Live", "Camera");
        SceneItemNotFoundException withCause = new("missing", cause, "Live", "Camera");

        Assert.AreEqual("Live", plain.SceneName);
        Assert.AreEqual("Camera", plain.SourceName);
        Assert.AreEqual("Live", withCause.SceneName);
        Assert.AreEqual("Camera", withCause.SourceName);
        Assert.AreSame(cause, withCause.InnerException);
    }

    #endregion
}
