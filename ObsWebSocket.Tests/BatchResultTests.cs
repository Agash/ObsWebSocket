using System.Text.Json;
using ObsWebSocket.Core;
using ObsWebSocket.Core.Protocol;
using ObsWebSocket.Core.Protocol.Requests;
using ObsWebSocket.Core.Protocol.Responses;
using ObsWebSocket.Core.Serialization;

namespace ObsWebSocket.Tests;

/// <summary>
/// Covers reading typed data out of batch results, on both transports and on the shapes OBS
/// produces for absent payloads and failed requests.
/// </summary>
[TestClass]
public sealed class BatchResultTests
{
    private static RequestStatus Ok => new(true, 100, null);

    private static RequestResponsePayload<object> JsonResult(string type, object? payload)
    {
        JsonElement element =
            payload is null
                ? default
                : JsonSerializer.SerializeToElement(
                    payload,
                    ObsWebSocketJsonContext.Default.Options.GetTypeInfo(payload.GetType())
                );

        return new RequestResponsePayload<object>(type, $"{type}_0", Ok, element);
    }

    private static RequestResponsePayload<object> MsgPackResult(string type, object payload)
    {
        byte[] bytes = MessagePack.MessagePackSerializer.Serialize(
            payload.GetType(),
            payload,
            MsgPackMessageSerializer.s_msgPackOptions
        );

        return new RequestResponsePayload<object>(
            type,
            $"{type}_0",
            Ok,
            new ReadOnlyMemory<byte>(bytes)
        );
    }

    [TestMethod]
    public void GetRequiredData_JsonPayload_Deserializes()
    {
        RequestResponsePayload<object> result = JsonResult(
            "GetVersion",
            new GetVersionResponseData { ObsVersion = "32.2.2", RpcVersion = 1 }
        );

        Assert.AreEqual("32.2.2", result.GetRequiredData<GetVersionResponseData>().ObsVersion);
    }

    [TestMethod]
    public void GetRequiredData_MsgPackPayload_Deserializes()
    {
        // The MessagePack transport hands back raw payload bytes rather than a JsonElement.
        RequestResponsePayload<object> result = MsgPackResult(
            "GetVersion",
            new GetVersionResponseData { ObsVersion = "32.2.2", RpcVersion = 1 }
        );

        Assert.AreEqual("32.2.2", result.GetRequiredData<GetVersionResponseData>().ObsVersion);
    }

    [TestMethod]
    public void GetData_AbsentJsonPayload_ReturnsNull()
    {
        // An absent payload arrives as default(JsonElement), whose ValueKind is Undefined.
        RequestResponsePayload<object> result = JsonResult("SetCurrentProgramScene", null);

        Assert.IsNull(result.GetData<GetVersionResponseData>());
    }

    [TestMethod]
    public void GetRequiredData_FailedRequest_ThrowsCarryingStatus()
    {
        RequestResponsePayload<object> result = new(
            "GetSceneItemList",
            "GetSceneItemList_1",
            new RequestStatus(false, 600, "No source was found."),
            null
        );

        ObsWebSocketRequestException ex =
            Assert.ThrowsExactly<ObsWebSocketRequestException>(
                () => result.GetRequiredData<GetSceneItemListResponseData>()
            );

        Assert.AreEqual(600, ex.Status?.Code);
        Assert.AreEqual("GetSceneItemList", ex.RequestType);
    }

    [TestMethod]
    public void TryGetData_FailedRequest_ReturnsFalseWithoutThrowing()
    {
        RequestResponsePayload<object> result = new(
            "GetSceneItemList",
            "GetSceneItemList_1",
            new RequestStatus(false, 600, "No source was found."),
            null
        );

        Assert.IsFalse(result.TryGetData(out GetSceneItemListResponseData? data));
        Assert.IsNull(data);
    }

    [TestMethod]
    public void AllSucceededAndGetFailures_ReflectStatuses()
    {
        List<RequestResponsePayload<object>> results =
        [
            JsonResult("GetVersion", new GetVersionResponseData { ObsVersion = "1", RpcVersion = 1 }),
            new("GetStats", "GetStats_1", new RequestStatus(false, 604, "nope"), null),
        ];

        Assert.IsFalse(results.AllSucceeded());
        Assert.AreEqual("GetStats", results.GetFailures().Single().RequestType);
    }

    [TestMethod]
    public void Builder_TypedAdd_SerializesWithSuppliedTypeInfo()
    {
        ObsBatchBuilder builder = new();
        _ = builder.Add(
            "SetCurrentProgramScene",
            new SetCurrentProgramSceneRequestData(sceneName: "Intro"),
            (System.Text.Json.Serialization.Metadata.JsonTypeInfo<SetCurrentProgramSceneRequestData>)
                ObsWebSocketJsonContext.Default.Options.GetTypeInfo(
                    typeof(SetCurrentProgramSceneRequestData)
                )
        );

        BatchRequestItem item = builder.Build().Single();
        Assert.AreEqual("SetCurrentProgramScene", item.RequestType);
        JsonElement element = (JsonElement)item.RequestData!;
        Assert.AreEqual("Intro", element.GetProperty("sceneName").GetString());
    }

    [TestMethod]
    public void Items_IsSnapshot_NotLiveView()
    {
        ObsBatchBuilder builder = new();
        _ = builder.General.GetVersion();

        IReadOnlyList<BatchRequestItem> snapshot = builder.Items;
        _ = builder.General.GetStats();

        Assert.AreEqual(1, snapshot.Count, "a taken snapshot must not grow with the builder");
        Assert.AreEqual(2, builder.Items.Count);
    }

    [TestMethod]
    public void OrderBatchResults_OutOfOrderResponse_RestoresSubmissionOrder()
    {
        // Parallel execution returns results in completion order, so submission order has to be
        // restored from the ids the client stamps on each request.
        List<RequestPayload> sent =
        [
            new("GetVersion", "batch_0", null),
            new("GetStats", "batch_1", null),
            new("GetSceneList", "batch_2", null),
        ];

        List<RequestResponsePayload<object>> shuffled =
        [
            new("GetSceneList", "batch_2", Ok, null),
            new("GetVersion", "batch_0", Ok, null),
            new("GetStats", "batch_1", Ok, null),
        ];

        List<RequestResponsePayload<object>> ordered = InvokeOrder(shuffled, sent);

        CollectionAssert.AreEqual(
            new[] { "GetVersion", "GetStats", "GetSceneList" },
            ordered.Select(r => r.RequestType).ToArray()
        );
    }

    [TestMethod]
    public void OrderBatchResults_TruncatedResponse_KeepsRemainingInOrder()
    {
        // haltOnFailure stops the batch early, so OBS returns fewer results than requests.
        List<RequestPayload> sent =
        [
            new("GetVersion", "batch_0", null),
            new("GetStats", "batch_1", null),
            new("GetSceneList", "batch_2", null),
        ];

        List<RequestResponsePayload<object>> truncated =
        [
            new("GetStats", "batch_1", Ok, null),
            new("GetVersion", "batch_0", Ok, null),
        ];

        List<RequestResponsePayload<object>> ordered = InvokeOrder(truncated, sent);

        CollectionAssert.AreEqual(
            new[] { "GetVersion", "GetStats" },
            ordered.Select(r => r.RequestType).ToArray()
        );
    }

    private static List<RequestResponsePayload<object>> InvokeOrder(
        List<RequestResponsePayload<object>> results,
        List<RequestPayload> sent
    )
    {
        System.Reflection.MethodInfo method =
            typeof(ObsWebSocketClient).GetMethod(
                "OrderBatchResults",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
            ) ?? throw new MissingMethodException("ObsWebSocketClient", "OrderBatchResults");

        return (List<RequestResponsePayload<object>>)method.Invoke(null, [results, sent])!;
    }
}
