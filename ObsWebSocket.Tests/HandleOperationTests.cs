using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Moq;
using ObsWebSocket.Core;
using ObsWebSocket.Core.Networking;
using ObsWebSocket.Core.Protocol;
using ObsWebSocket.Core.Serialization;

namespace ObsWebSocket.Tests;

/// <summary>
/// The generated operations exist to send the right identity field and no other. OBS resolves a
/// uuid before a name and reads the canvas only on the name path, so a handle that sent both would
/// be sending a field the server ignores, and one that sent neither would be
/// <c>MissingRequestField</c>.
/// </summary>
[TestClass]
public sealed class HandleOperationTests
{
    private static readonly Guid s_uuid = new("5d5db648-93a5-4985-bff8-45f4c9fe15f7");

    [TestMethod]
    public async Task ANameHandleSendsTheNameAndNoUuid()
    {
        JsonElement sent = await CaptureAsync(
            (client, ct) => client.Scene("Intro").SetCurrentProgramAsync(ct)
        );

        Assert.AreEqual("Intro", sent.GetProperty("sceneName").GetString());
        Assert.IsFalse(
            sent.TryGetProperty("sceneUuid", out JsonElement uuid)
                && uuid.ValueKind is not JsonValueKind.Null,
            "a name handle must not also send a uuid"
        );
    }

    [TestMethod]
    public async Task AUuidHandleSendsTheUuidAndNoName()
    {
        JsonElement sent = await CaptureAsync(
            (client, ct) => client.Scene(s_uuid).SetCurrentProgramAsync(ct)
        );

        Assert.AreEqual(
            "5d5db648-93a5-4985-bff8-45f4c9fe15f7",
            sent.GetProperty("sceneUuid").GetString()
        );
        Assert.IsFalse(
            sent.TryGetProperty("sceneName", out JsonElement name)
                && name.ValueKind is not JsonValueKind.Null,
            "a uuid handle must not also send a name"
        );
    }

    /// <summary>
    /// The canvas travels with a name, because that is the only path OBS reads it on.
    /// </summary>
    [TestMethod]
    public async Task ACanvasScopedNameSendsTheCanvas()
    {
        CanvasHandle vertical = CanvasHandle.FromUuid(s_uuid);

        JsonElement sent = await CaptureAsync(
            (client, ct) => client.Scene(vertical.Scene("Intro")).GetItemListAsync(ct)
        );

        Assert.AreEqual("Intro", sent.GetProperty("sceneName").GetString());
        Assert.AreEqual(
            "5d5db648-93a5-4985-bff8-45f4c9fe15f7",
            sent.GetProperty("canvasUuid").GetString()
        );
    }

    /// <summary>
    /// A composite handle supplies both halves of the identity: the scene, and the id inside it.
    /// </summary>
    [TestMethod]
    public async Task ASceneItemSendsItsSceneAndItsId()
    {
        JsonElement sent = await CaptureAsync(
            (client, ct) =>
                client.SceneItem(SceneHandle.FromName("Intro").Item(7)).SetEnabledAsync(false, ct)
        );

        Assert.AreEqual("Intro", sent.GetProperty("sceneName").GetString());
        Assert.AreEqual(7, sent.GetProperty("sceneItemId").GetInt32());
        Assert.IsFalse(sent.GetProperty("sceneItemEnabled").GetBoolean());
    }

    [TestMethod]
    public async Task AFilterSendsItsSourceAndItsName()
    {
        JsonElement sent = await CaptureAsync(
            (client, ct) =>
                client.Filter(InputHandle.FromName("Mic").Filter("EQ")).SetEnabledAsync(true, ct)
        );

        Assert.AreEqual("Mic", sent.GetProperty("sourceName").GetString());
        Assert.AreEqual("EQ", sent.GetProperty("filterName").GetString());
        Assert.IsTrue(sent.GetProperty("filterEnabled").GetBoolean());
    }

    /// <summary>
    /// A second reference in one request stays a parameter, because only one thing can be the
    /// subject. DuplicateSceneItem is the only request in the protocol shaped this way.
    /// </summary>
    [TestMethod]
    public async Task ASecondReferenceIsAParameter()
    {
        JsonElement sent = await CaptureAsync(
            (client, ct) =>
                client
                    .SceneItem(SceneHandle.FromName("Intro").Item(7))
                    .DuplicateAsync(destinationScene: "Outro", cancellationToken: ct)
        );

        Assert.AreEqual("Intro", sent.GetProperty("sceneName").GetString());
        Assert.AreEqual(7, sent.GetProperty("sceneItemId").GetInt32());
        Assert.AreEqual("Outro", sent.GetProperty("destinationSceneName").GetString());
    }

    /// <summary>A bare string is a name, which is the whole point of the implicit conversion.</summary>
    [TestMethod]
    public async Task AStringReachesTheOperationsWithoutCeremony()
    {
        JsonElement sent = await CaptureAsync(
            (client, ct) => client.Input("Mic").ToggleMuteAsync(ct)
        );

        Assert.AreEqual("Mic", sent.GetProperty("inputName").GetString());
    }

    /// <summary>
    /// Captures the <c>requestData</c> of the request the action sends.
    /// </summary>
    /// <remarks>
    /// The call is cancelled the moment the bytes are on the wire, which is everything this test
    /// class is about. Letting it run to completion would mean canning a response per request
    /// shape, and the response is not what is under test.
    /// </remarks>
    private static async Task<JsonElement> CaptureAsync(
        Func<ObsWebSocketClient, CancellationToken, Task> act
    )
    {
        (ObsWebSocketClient? client, _, Mock<IWebSocketConnection>? socket) =
            TestUtils.SetupConnectedClientForceState();

        JsonElement? captured = null;
        using CancellationTokenSource stopOnceSent = new();

        _ = socket
            .Setup(ws =>
                ws.SendAsync(
                    It.IsAny<ReadOnlyMemory<byte>>(),
                    It.IsAny<WebSocketMessageType>(),
                    true,
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback(
                (
                    ReadOnlyMemory<byte> buffer,
                    WebSocketMessageType type,
                    bool end,
                    CancellationToken token
                ) =>
                {
                    OutgoingMessage<RequestPayload>? message = JsonSerializer.Deserialize<
                        OutgoingMessage<RequestPayload>
                    >(buffer.Span, TestUtils.s_jsonSerializerOptions);
                    captured = message?.D?.RequestData;
                    stopOnceSent.Cancel();
                }
            )
            .Returns(ValueTask.CompletedTask);

        try
        {
            await act(client, stopOnceSent.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected: the request was cancelled as soon as it had been sent.
        }

        Assert.IsNotNull(captured, "no request was sent");
        return captured.Value;
    }
}
