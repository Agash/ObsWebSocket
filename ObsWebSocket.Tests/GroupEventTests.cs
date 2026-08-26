using ObsWebSocket.Core;
using ObsWebSocket.Core.Events.Generated;

namespace ObsWebSocket.Tests;

/// <summary>
/// The category groups are structs the property hands out fresh on every access, so subscribing
/// through one has to reach the client's own delegate list rather than a temporary. Removal
/// matters most: a remove that silently did nothing would leak every handler.
/// </summary>
[TestClass]
public sealed class GroupEventTests
{
    private static Delegate[] Handlers(ObsWebSocketClient client, string eventName) =>
        TestUtils.GetPrivateField<MulticastDelegate>(client, eventName)?.GetInvocationList() ?? [];

    [TestMethod]
    public void AddAndRemoveThroughTheGroup_ReachTheClientsList()
    {
        (ObsWebSocketClient client, _, _) = TestUtils.SetupConnectedClientForceState();
        static void Handler(object? sender, CurrentProgramSceneChangedEventArgs e) { }

        Assert.AreEqual(0, Handlers(client, "CurrentProgramSceneChanged").Length);

        client.Scenes.CurrentProgramSceneChanged += Handler;
        Assert.AreEqual(
            1,
            Handlers(client, "CurrentProgramSceneChanged").Length,
            "adding through the group should reach the client"
        );

        client.Scenes.CurrentProgramSceneChanged -= Handler;
        Assert.AreEqual(
            0,
            Handlers(client, "CurrentProgramSceneChanged").Length,
            "removing through the group must not leave the handler attached"
        );
    }

    [TestMethod]
    public void TheGroupAndTheClientShareOneSubscriptionList()
    {
        (ObsWebSocketClient client, _, _) = TestUtils.SetupConnectedClientForceState();
        static void Handler(object? sender, CurrentProgramSceneChangedEventArgs e) { }

        client.CurrentProgramSceneChanged += Handler;
        client.Scenes.CurrentProgramSceneChanged -= Handler;
        Assert.AreEqual(
            0,
            Handlers(client, "CurrentProgramSceneChanged").Length,
            "a handler added on the client should be removable through the group"
        );

        client.Scenes.CurrentProgramSceneChanged += Handler;
        client.CurrentProgramSceneChanged -= Handler;
        Assert.AreEqual(
            0,
            Handlers(client, "CurrentProgramSceneChanged").Length,
            "a handler added through the group should be removable on the client"
        );
    }

    [TestMethod]
    public void EveryCategoryGroupCarriesItsEvents()
    {
        // The point of the change: a caller never has to know that some events sit on the client
        // and some on a group.
        (ObsWebSocketClient client, _, _) = TestUtils.SetupConnectedClientForceState();

        Assert.IsNotNull(typeof(ScenesGroup).GetEvent(nameof(client.SceneCreated)));
        Assert.IsNotNull(typeof(InputsGroup).GetEvent(nameof(client.InputCreated)));
        Assert.IsNotNull(typeof(OutputsGroup).GetEvent(nameof(client.StreamStateChanged)));
        Assert.IsNotNull(typeof(SceneItemsGroup).GetEvent(nameof(client.SceneItemCreated)));
        Assert.IsNotNull(typeof(UiGroup).GetEvent(nameof(client.StudioModeStateChanged)));
    }
}
