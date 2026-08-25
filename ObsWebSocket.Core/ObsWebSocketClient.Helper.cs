using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using ObsWebSocket.Core.Events;
using ObsWebSocket.Core.Events.Generated;
using ObsWebSocket.Core.Protocol;
using ObsWebSocket.Core.Protocol.Common.InputSettings;
using ObsWebSocket.Core.Protocol.Generated; // Assuming generated enums are here
using ObsWebSocket.Core.Protocol.Requests;
using ObsWebSocket.Core.Protocol.Responses;

namespace ObsWebSocket.Core; // Or ObsWebSocket.Core.Extensions

/// <summary>
/// Provides helpful extension methods for common OBS WebSocket tasks.
/// </summary>
public static partial class ObsWebSocketClientHelpers
{
    private static readonly JsonSerializerOptions s_helperJsonOptions = CreateHelperOptions();

    private static JsonSerializerOptions CreateHelperOptions()
    {
        JsonSerializerOptions options = new(ObsWebSocket.Core.Serialization.ObsWebSocketJsonContext.Default.Options)
        {
            TypeInfoResolver = System.Text.Json.Serialization.Metadata.JsonTypeInfoResolver.Combine(
                ObsWebSocket.Core.Serialization.ObsWebSocketJsonContext.Default,
                ObsWebSocket.Core.Serialization.ObsWebSocketSettingsJsonContext.Default
            ),
        };
        return options;
    }

    internal static JsonTypeInfo<T> GetRegisteredTypeInfo<T>() where T : class
    {
        JsonTypeInfo<T>? typeInfo;
        try
        {
            typeInfo = s_helperJsonOptions.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            throw new ObsWebSocketException(
                $"Type '{typeof(T).Name}' is not registered in ObsWebSocketJsonContext. Pass an explicit JsonTypeInfo<T> or use a library-registered settings type.",
                ex
            );
        }
        return typeInfo ?? throw new ObsWebSocketException(
            $"Type '{typeof(T).Name}' is not registered in ObsWebSocketJsonContext. Pass an explicit JsonTypeInfo<T> or use a library-registered settings type."
        );
    }









    // Helper #4 (SwitchSceneAndWaitAsync) - Deferred due to complexity without reflection



















    // ────────────────────────────────────────────────────────────────────────
    // Generic input settings helpers
    // ────────────────────────────────────────────────────────────────────────

















    // -------------------------------------------------------------------------
    // Transition Settings helpers
    // -------------------------------------------------------------------------









    // -------------------------------------------------------------------------
    // Output Settings helpers
    // -------------------------------------------------------------------------









    // -------------------------------------------------------------------------
    // Stream Service Settings helpers
    // -------------------------------------------------------------------------









    // -------------------------------------------------------------------------
    // Default Settings helpers (read-only)
    // -------------------------------------------------------------------------

















    // Helper #14 (WaitForEventAsync<TEventArgs>) - Deferred due to complexity/reflection constraints.

    // ────────────────────────────────────────────────────────────────────────
    // Virtualcam helpers
    // ────────────────────────────────────────────────────────────────────────





    // ────────────────────────────────────────────────────────────────────────
    // Canvas-aware screenshot helpers
    // ────────────────────────────────────────────────────────────────────────






}


