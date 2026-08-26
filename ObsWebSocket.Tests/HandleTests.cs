using ObsWebSocket.Core;

namespace ObsWebSocket.Tests;

/// <summary>
/// A handle encodes the choice OBS makes for you: a uuid wins outright, a name is read only when
/// no uuid was sent, the canvas is consulted only on the name path, and neither is
/// <c>MissingRequestField</c>. The type has to make the last one impossible and the middle two
/// automatic.
/// </summary>
[TestClass]
public sealed class HandleTests
{
    private static readonly Guid s_uuid = new("5d5db648-93a5-4985-bff8-45f4c9fe15f7");

    [TestMethod]
    public void AStringIsAName_AndAGuidIsAUuid()
    {
        SceneHandle byName = "Intro";
        SceneHandle byUuid = s_uuid;

        Assert.AreEqual("Intro", byName.Name);
        Assert.IsNull(byName.Uuid);
        Assert.IsFalse(byName.IsResolved);

        Assert.IsNull(byUuid.Name);
        Assert.AreEqual("5d5db648-93a5-4985-bff8-45f4c9fe15f7", byUuid.Uuid);
        Assert.IsTrue(byUuid.IsResolved);
    }

    /// <summary>
    /// OBS writes uuids lowercase and hyphenated, on Windows through UuidCreate and elsewhere
    /// through uuid_unparse_lower, which is what Guid's "D" format produces.
    /// </summary>
    [TestMethod]
    public void AGuidIsFormattedTheWayObsWritesOne()
    {
        Assert.AreEqual("5d5db648-93a5-4985-bff8-45f4c9fe15f7", SceneHandle.FromUuid(s_uuid).Uuid);
        Assert.AreEqual(
            SceneHandle.FromUuid("5d5db648-93a5-4985-bff8-45f4c9fe15f7"),
            SceneHandle.FromUuid(s_uuid)
        );
    }

    /// <summary>
    /// A uuid read off the wire is never parsed, so a value OBS sends that is not a Guid cannot
    /// break a response.
    /// </summary>
    [TestMethod]
    public void AUuidFromTheWireIsCarriedVerbatim()
    {
        Assert.AreEqual("not-a-guid", SceneHandle.FromUuid("not-a-guid").Uuid);
    }

    /// <summary>
    /// OBS reads canvasUuid only on the name path, so carrying it on a resolved handle would be
    /// carrying a field the server ignores.
    /// </summary>
    [TestMethod]
    public void ACanvasScopesANameAndIsDroppedByAUuid()
    {
        CanvasHandle vertical = CanvasHandle.FromUuid(s_uuid);

        Assert.AreEqual(vertical, SceneHandle.FromName("Intro", vertical).Canvas);
        Assert.AreEqual(CanvasHandle.Main, SceneHandle.FromUuid(s_uuid).Canvas);
        Assert.AreEqual(vertical, vertical.Scene("Intro").Canvas);
    }

    [TestMethod]
    public void TheMainCanvasCarriesNoUuid_WhichIsWhatOmittingTheFieldMeans()
    {
        Assert.IsNull(CanvasHandle.Main.Uuid);
        Assert.IsFalse(CanvasHandle.Main.IsResolved);
        Assert.IsNull(CanvasHandle.Main.Name);
    }

    /// <summary>
    /// A scene is a source in OBS, and the requests that take a bare source accept either.
    /// </summary>
    [TestMethod]
    public void ASceneAndAnInputBothNarrowToASource()
    {
        Assert.AreEqual("Intro", SceneHandle.FromName("Intro").AsSource().Name);
        Assert.AreEqual(
            "5d5db648-93a5-4985-bff8-45f4c9fe15f7",
            SceneHandle.FromUuid(s_uuid).AsSource().Uuid
        );
        Assert.AreEqual("Mic", InputHandle.FromName("Mic").AsSource().Name);
        Assert.AreEqual(
            "5d5db648-93a5-4985-bff8-45f4c9fe15f7",
            InputHandle.FromUuid(s_uuid).AsSource().Uuid
        );
    }

    /// <summary>
    /// A scene item known only by its source name cannot be passed where an id is required, so
    /// the missing lookup is a compile error rather than a runtime one.
    /// </summary>
    [TestMethod]
    public void ASceneItemByIdIsAHandle_ButBySourceNameItIsNotYet()
    {
        SceneHandle intro = "Intro";

        SceneItemHandle byId = intro.Item(3);
        Assert.AreEqual(3, byId.SceneItemId);
        Assert.AreEqual(intro, byId.Scene);

        UnresolvedSceneItem byName = intro.Item("Logo");
        Assert.AreEqual("Logo", byName.SourceName);
        Assert.AreEqual(intro, byName.Scene);
    }

    /// <summary>Filters have no uuid in the protocol, so the name is the identity.</summary>
    [TestMethod]
    public void AFilterIsNamedOnItsSource()
    {
        FilterHandle eq = InputHandle.FromName("Mic").Filter("EQ");

        Assert.AreEqual("EQ", eq.FilterName);
        Assert.AreEqual("Mic", eq.Source.Name);
    }

    [TestMethod]
    public void AnEmptyNameIsRefusedRatherThanSentAsOne()
    {
        _ = Assert.ThrowsExactly<ArgumentException>(() => SceneHandle.FromName(string.Empty));
        _ = Assert.ThrowsExactly<ArgumentException>(() => InputHandle.FromName(string.Empty));
        _ = Assert.ThrowsExactly<ArgumentException>(() => CanvasHandle.FromName(string.Empty));
        _ = Assert.ThrowsExactly<ArgumentException>(() =>
            InputHandle.FromName("Mic").Filter(string.Empty)
        );
    }

    [TestMethod]
    public void ANegativeSceneItemIdIsRefused()
    {
        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            SceneHandle.FromName("Intro").Item(-1)
        );
    }

    /// <summary>
    /// Handles are compared by what they address, so one built from a response equals one written
    /// by hand.
    /// </summary>
    [TestMethod]
    public void TwoHandlesForTheSameThingAreEqual()
    {
        Assert.AreEqual(SceneHandle.FromName("Intro"), (SceneHandle)"Intro");
        Assert.AreEqual(SceneHandle.FromUuid(s_uuid), (SceneHandle)s_uuid);
        Assert.AreNotEqual(SceneHandle.FromName("Intro"), SceneHandle.FromName("Outro"));

        // A name and a uuid are different addresses even when they point at one scene, because
        // nothing here has asked OBS which scene that is.
        Assert.AreNotEqual(SceneHandle.FromName("Intro"), SceneHandle.FromUuid(s_uuid));
    }

    [TestMethod]
    public void AHandleSaysWhatItAddressesWhenPrinted()
    {
        Assert.AreEqual("scene 'Intro'", SceneHandle.FromName("Intro").ToString());
        Assert.AreEqual("the main canvas", CanvasHandle.Main.ToString());
        Assert.AreEqual(
            "filter 'EQ' on source 'Mic'",
            InputHandle.FromName("Mic").Filter("EQ").ToString()
        );
        Assert.AreEqual(
            "item 3 in scene 'Intro'",
            SceneHandle.FromName("Intro").Item(3).ToString()
        );
    }
}
