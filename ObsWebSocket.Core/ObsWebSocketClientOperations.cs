using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ObsWebSocket.Core.Serialization;

namespace ObsWebSocket.Core;

/// <summary>
/// Client level extensions that do not belong to one protocol category, alongside the generated
/// <c>WaitForEventAsync</c>. Everything category scoped lives on the category group instead, so
/// <c>client.Scenes</c>, <c>client.Inputs</c> and the rest are where those methods are found.
/// </summary>
public static partial class ObsWebSocketClientOperations
{
    private static readonly JsonSerializerOptions s_helperJsonOptions = CreateHelperOptions();

    private static JsonSerializerOptions CreateHelperOptions() =>
        new(ObsWebSocketJsonContext.Default.Options)
        {
            TypeInfoResolver = JsonTypeInfoResolver.Combine(
                ObsWebSocketJsonContext.Default,
                ObsWebSocketSettingsJsonContext.Default
            ),
        };

    /// <summary>
    /// Resolves the source generated metadata for a settings type the library knows about.
    /// </summary>
    /// <typeparam name="T">The settings type to resolve.</typeparam>
    /// <exception cref="ObsWebSocketException">
    /// Thrown when the type is not registered in either generated context, since serializing it
    /// would otherwise fall back to reflection and break under trimming.
    /// </exception>
    internal static JsonTypeInfo<T> GetRegisteredTypeInfo<T>()
        where T : class
    {
        JsonTypeInfo<T>? typeInfo;
        try
        {
            typeInfo = s_helperJsonOptions.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            throw new ObsWebSocketException(NotRegistered<T>(), ex);
        }

        return typeInfo ?? throw new ObsWebSocketException(NotRegistered<T>());
    }

    private static string NotRegistered<T>() =>
        $"Type '{typeof(T).Name}' is not registered in ObsWebSocketJsonContext. "
        + "Pass an explicit JsonTypeInfo<T> or use a library-registered settings type.";
}
