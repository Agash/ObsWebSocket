using System.Text.Json;
using MessagePack;

namespace ObsWebSocket.Core.Serialization;

/// <summary>
/// Checks a batch payload against the record it is about to be read as.
/// </summary>
/// <remarks>
/// Reading a payload as the wrong record is silent on MessagePack, which maps by key name and
/// leaves everything unmatched at its default, and silent on JSON too for the response records that
/// have no required member, which is most of them. Response records almost never share field
/// names, so a payload carrying none of the target's keys did not come from that request.
/// <para>
/// This rejects rather than identifies. Five response shapes are shared by more than one request,
/// but those records are field for field identical, so reading one as another yields the right
/// values anyway. A payload that overlaps the target only partly still passes, which is the
/// remaining gap.
/// </para>
/// </remarks>
internal static class PayloadShape
{
    /// <summary>
    /// Throws when a payload carries none of the fields <typeparamref name="TResponse"/> expects.
    /// </summary>
    /// <typeparam name="TResponse">The record the payload is about to be read as.</typeparam>
    /// <param name="responseData">The transport shaped payload.</param>
    /// <exception cref="ObsWebSocketSerializationException">Thrown when the payload cannot be that record.</exception>
    public static void EnsurePlausible<TResponse>(object responseData)
        where TResponse : class
    {
        string[] expected = ObsWebSocketPayloadSchema.KnownKeys(typeof(TResponse).Name);
        if (expected.Length == 0)
        {
            return;
        }

        bool anyMatch = responseData switch
        {
            JsonElement json => MatchesJson(json, expected),
            ReadOnlyMemory<byte> packed => MatchesMsgPack(packed, expected),
            _ => true,
        };

        if (!anyMatch)
        {
            throw new ObsWebSocketSerializationException(
                $"This payload carries none of the fields {typeof(TResponse).Name} expects "
                    + $"({string.Join(", ", expected)}), so it came from a different request. Under "
                    + "RequestBatchExecutionType.Parallel OBS labels each result with another "
                    + "request's type, which is the usual cause."
            );
        }
    }

    private static bool MatchesJson(JsonElement json, string[] expected)
    {
        if (json.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        bool sawAny = false;
        foreach (JsonProperty property in json.EnumerateObject())
        {
            sawAny = true;
            if (Array.IndexOf(expected, property.Name) >= 0)
            {
                return true;
            }
        }

        // An empty object tells us nothing either way.
        return !sawAny;
    }

    private static bool MatchesMsgPack(ReadOnlyMemory<byte> packed, string[] expected)
    {
        MessagePackReader reader = new(packed);
        if (reader.NextMessagePackType != MessagePackType.Map)
        {
            return true;
        }

        int count = reader.ReadMapHeader();
        if (count == 0)
        {
            return true;
        }

        for (int i = 0; i < count; i++)
        {
            string? key = reader.ReadString();
            if (key is not null && Array.IndexOf(expected, key) >= 0)
            {
                return true;
            }

            reader.Skip();
        }

        return false;
    }
}
