using ObsWebSocket.Core;

namespace ObsWebSocket.Tests;

/// <summary>
/// OBS returns screenshots as a data URI, not as bare Base64. Decoding the whole string
/// throws, so the in-memory screenshot helper silently returned nothing for every call.
/// </summary>
[TestClass]
public sealed class ScreenshotDecodeTests
{
    // The eight byte PNG signature, which is what a caller checks to know it got an image.
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [TestMethod]
    public void DecodeImageData_WithDataUriPrefix_ReturnsTheImageBytes()
    {
        string dataUri = "data:image/png;base64," + Convert.ToBase64String(PngSignature);

        byte[] decoded = SourcesGroup.DecodeImageData(dataUri);

        CollectionAssert.AreEqual(PngSignature, decoded);
    }

    [TestMethod]
    public void DecodeImageData_WithBareBase64_StillDecodes()
    {
        byte[] decoded = SourcesGroup.DecodeImageData(Convert.ToBase64String(PngSignature));

        CollectionAssert.AreEqual(PngSignature, decoded);
    }

    [TestMethod]
    public void DecodeImageData_WhenEmpty_Throws() =>
        Assert.ThrowsExactly<ArgumentException>(() => SourcesGroup.DecodeImageData(""));
}
