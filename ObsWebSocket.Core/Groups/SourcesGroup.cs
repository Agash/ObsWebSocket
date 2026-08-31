using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using ObsWebSocket.Core.Events;
using ObsWebSocket.Core.Events.Generated;
using ObsWebSocket.Core.Networking;
using ObsWebSocket.Core.Protocol;
using ObsWebSocket.Core.Protocol.Common;
using ObsWebSocket.Core.Protocol.Common.FilterSettings;
using ObsWebSocket.Core.Protocol.Common.InputSettings;
using ObsWebSocket.Core.Protocol.Generated;
using ObsWebSocket.Core.Protocol.Requests;
using ObsWebSocket.Core.Protocol.Responses;

namespace ObsWebSocket.Core;

/// <summary>
/// Conveniences for the <c>Sources</c> category, alongside its generated requests.
/// </summary>
public readonly partial struct SourcesGroup
{
    /// <summary>
    /// Checks if an input or scene source with the given name exists in OBS.
    /// </summary>
    /// <param name="sourceName">The name of the input or scene to check.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>True if a source (input or scene) with the specified name exists, false otherwise.</returns>
    /// <exception cref="ObsWebSocketException">Thrown if an unexpected error occurs during API calls.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public async Task<bool> SourceExistsAsync(
        string sourceName,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceName);
        client.EnsureConnected();

        try
        {
            // Check inputs first
            GetInputListResponseData? inputListResponse = await client
                .Inputs.GetInputListAsync(
                    new GetInputListRequestData(),
                    cancellationToken: cancellationToken
                )
                .ConfigureAwait(false);
            if (
                inputListResponse?.Inputs?.Any(i =>
                    string.Equals(i.InputName, sourceName, StringComparison.Ordinal)
                )
                ?? false
            )
            {
                return true;
            }

            // Check scenes if not found in inputs
            ObsWebSocket.Core.Protocol.Responses.GetSceneListResponseData? sceneListResponse =
                await client
                    .Scenes.GetSceneListAsync(new(), cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            return sceneListResponse?.Scenes?.Any(s =>
                    string.Equals(s.SceneName, sourceName, StringComparison.Ordinal)
                )
                ?? false;
        }
        catch (ObsWebSocketException ex)
        {
            // Log the specific OBS error but return false as the source effectively doesn't exist or couldn't be verified
            client._logger.LogWarning(
                ex,
                "OBS error while checking if source '{SourceName}' exists. Assuming it doesn't.",
                sourceName
            );
            return false;
        }
        // Let other exceptions (like InvalidOperationException for disconnect) propagate
    }

    /// <summary>
    /// Gets a screenshot of a source and returns it as a byte array.
    /// </summary>
    /// <param name="sourceName">The name of the source (input or scene).</param>
    /// <param name="imageFormat">The desired image format (e.g., "png", "jpg", "bmp"). Use GetVersion for supported formats.</param>
    /// <param name="width">Optional width to scale the screenshot to.</param>
    /// <param name="height">Optional height to scale the screenshot to.</param>
    /// <param name="compressionQuality">Optional compression quality (0-100 for formats like jpg, -1 for default).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A byte array containing the image data, or null if the source was not found or an error occurred.</returns>
    /// <exception cref="ObsWebSocketException">Thrown for OBS errors other than 'ResourceNotFound' or Base64 decoding errors.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public async Task<byte[]?> GetSourceScreenshotBytesAsync(
        string sourceName,
        string imageFormat = "png", // Common default
        int? width = null,
        int? height = null,
        int? compressionQuality = -1, // Use -1 for OBS default quality
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceName);
        ArgumentException.ThrowIfNullOrEmpty(imageFormat);
        client.EnsureConnected();

        GetSourceScreenshotResponseData? response;
        try
        {
            response = await client
                .Sources.GetSourceScreenshotAsync(
                    new GetSourceScreenshotRequestData(
                        sourceName: sourceName,
                        imageFormat: imageFormat,
                        imageWidth: width,
                        imageHeight: height,
                        imageCompressionQuality: compressionQuality
                    ),
                    cancellationToken: cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (ObsWebSocketRequestException ex)
            when (ex.StatusCode is RequestStatusCode.ResourceNotFound)
        {
            client._logger.LogWarning(
                "Source '{SourceName}' not found for screenshot.",
                sourceName
            );
            return null;
        }
        // Let other exceptions propagate

        if (string.IsNullOrEmpty(response?.ImageData))
        {
            client._logger.LogWarning(
                "Received null or empty image data for screenshot of '{SourceName}'.",
                sourceName
            );
            return null;
        }

        try
        {
            return DecodeImageData(response.ImageData);
        }
        catch (FormatException formatEx)
        {
            client._logger.LogError(
                formatEx,
                "Failed to decode Base64 image data for screenshot of '{SourceName}'.",
                sourceName
            );
            return null;
        }
    }

    /// <summary>
    /// Captures a screenshot of the named source and returns the raw image bytes.
    /// The <paramref name="sourceUuid"/> parameter can be used to identify the source
    /// unambiguously when multiple sources share the same display name.
    /// </summary>
    /// <param name="sourceName">The name of the source or scene to capture.</param>
    /// <param name="imageFormat">Image format: <c>"png"</c>, <c>"jpg"</c>, or <c>"bmp"</c>.</param>
    /// <param name="width">Optional output width. <see langword="null"/> uses the source width.</param>
    /// <param name="height">Optional output height. <see langword="null"/> uses the source height.</param>
    /// <param name="compressionQuality">
    /// JPEG compression quality 0 to 100 (<c>-1</c> uses the OBS default).
    /// Ignored for lossless formats.
    /// </param>
    /// <param name="sourceUuid">
    /// Optional source UUID for unambiguous identification.
    /// When <see langword="null"/> the lookup is by <paramref name="sourceName"/> alone.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The decoded image bytes, or an empty array if OBS returned no data.</returns>
    /// <exception cref="ObsWebSocketException">Thrown if OBS rejects the request.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public async Task<byte[]> GetSourceScreenshotOnCanvasBytesAsync(
        string sourceName,
        string imageFormat = "png",
        int? width = null,
        int? height = null,
        int compressionQuality = -1,
        string? sourceUuid = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceName);
        client.EnsureConnected();

        GetSourceScreenshotResponseData? response = await client
            .Sources.GetSourceScreenshotAsync(
                new GetSourceScreenshotRequestData(
                    imageFormat: imageFormat,
                    sourceName: sourceName,
                    sourceUuid: sourceUuid,
                    imageWidth: width,
                    imageHeight: height,
                    imageCompressionQuality: compressionQuality
                ),
                cancellationToken
            )
            .ConfigureAwait(false);

        string? b64 = response?.ImageData;
        if (string.IsNullOrEmpty(b64))
        {
            return [];
        }

        int commaIdx = b64.IndexOf(',', StringComparison.Ordinal);
        string base64 = commaIdx >= 0 ? b64[(commaIdx + 1)..] : b64;
        return Convert.FromBase64String(base64);
    }

    /// <summary>
    /// Saves a screenshot of the named source directly to a file on the OBS host machine.
    /// The <paramref name="sourceUuid"/> parameter can be used to identify the source
    /// unambiguously when multiple sources share the same display name.
    /// </summary>
    /// <param name="sourceName">The name of the source or scene to capture.</param>
    /// <param name="filePath">Absolute path on the OBS host where the image will be saved.</param>
    /// <param name="imageFormat">Image format: <c>"png"</c>, <c>"jpg"</c>, or <c>"bmp"</c>.</param>
    /// <param name="width">Optional output width. <see langword="null"/> uses the source width.</param>
    /// <param name="height">Optional output height. <see langword="null"/> uses the source height.</param>
    /// <param name="compressionQuality">JPEG compression quality 0 to 100 (<c>-1</c> uses the OBS default).</param>
    /// <param name="sourceUuid">
    /// Optional source UUID for unambiguous identification.
    /// When <see langword="null"/> the lookup is by <paramref name="sourceName"/> alone.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="ObsWebSocketException">Thrown if OBS rejects the request.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public async Task SaveSourceScreenshotToFileAsync(
        string sourceName,
        string filePath,
        string imageFormat = "png",
        int? width = null,
        int? height = null,
        int compressionQuality = -1,
        string? sourceUuid = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceName);
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        client.EnsureConnected();

        await client
            .Sources.SaveSourceScreenshotAsync(
                new SaveSourceScreenshotRequestData(
                    imageFormat: imageFormat,
                    imageFilePath: filePath,
                    sourceName: sourceName,
                    sourceUuid: sourceUuid,
                    imageWidth: width,
                    imageHeight: height,
                    imageCompressionQuality: compressionQuality
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Decodes the image OBS returns, which arrives as a data URI rather than bare Base64.
    /// </summary>
    /// <param name="imageData">The value of the response's image data field.</param>
    internal static byte[] DecodeImageData(string imageData)
    {
        ArgumentException.ThrowIfNullOrEmpty(imageData);

        int comma = imageData.IndexOf(',', StringComparison.Ordinal);
        return Convert.FromBase64String(comma >= 0 ? imageData[(comma + 1)..] : imageData);
    }
}
