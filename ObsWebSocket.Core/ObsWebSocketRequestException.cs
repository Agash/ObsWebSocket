using ObsWebSocket.Core.Protocol;
using ObsRequestStatus = ObsWebSocket.Core.Protocol.Generated.RequestStatus;

namespace ObsWebSocket.Core;

/// <summary>
/// Thrown when OBS rejects a request, carrying the status it reported.
/// </summary>
public class ObsWebSocketRequestException : ObsWebSocketException
{
    /// <summary>Initializes a new instance.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="requestType">The request type OBS rejected.</param>
    /// <param name="requestId">The identifier of the rejected request.</param>
    /// <param name="status">The status code OBS reported.</param>
    /// <param name="comment">The comment OBS attached, if any.</param>
    public ObsWebSocketRequestException(
        string message,
        string requestType,
        string requestId,
        RequestStatus? status,
        string? comment
    )
        : base(message)
    {
        RequestType = requestType;
        RequestId = requestId;
        Status = status;
        Comment = comment;
    }

    /// <summary>Initializes a new instance.</summary>
    public ObsWebSocketRequestException() { }

    /// <summary>Initializes a new instance.</summary>
    /// <param name="message">The error message.</param>
    public ObsWebSocketRequestException(string message)
        : base(message) { }

    /// <summary>Initializes a new instance.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The underlying cause.</param>
    public ObsWebSocketRequestException(string message, Exception innerException)
        : base(message, innerException) { }

    /// <summary>The request type OBS rejected.</summary>
    public string RequestType { get; } = string.Empty;

    /// <summary>The identifier of the rejected request.</summary>
    public string RequestId { get; } = string.Empty;

    /// <summary>The status OBS reported, when one was available.</summary>
    public RequestStatus? Status { get; }

    /// <summary>The comment OBS attached, if any.</summary>
    public string? Comment { get; }

    /// <summary>
    /// The status OBS reported as <see cref="ObsWebSocket.Core.Protocol.Generated.RequestStatus"/>,
    /// so a handler can match on the reason rather than on the text of
    /// <see cref="Exception.Message"/>. Note that the enum and the <see cref="Status"/> record
    /// share the name <c>RequestStatus</c> in different namespaces.
    /// </summary>
    /// <example>
    /// <code>
    /// catch (ObsWebSocketRequestException ex)
    ///     when (ex.StatusCode is RequestStatus.ResourceNotFound) { }
    /// </code>
    /// </example>
    public ObsRequestStatus? StatusCode => Status is null ? null : (ObsRequestStatus)Status.Code;
}

/// <summary>
/// Thrown when a request or an awaited event does not complete within its timeout.
/// </summary>
public class ObsWebSocketTimeoutException : ObsWebSocketException
{
    /// <summary>Initializes a new instance.</summary>
    public ObsWebSocketTimeoutException() { }

    /// <summary>Initializes a new instance.</summary>
    /// <param name="message">The error message.</param>
    public ObsWebSocketTimeoutException(string message)
        : base(message) { }

    /// <summary>Initializes a new instance.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The underlying cause.</param>
    public ObsWebSocketTimeoutException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>
/// Thrown when a payload cannot be serialized for sending or deserialized on receipt.
/// </summary>
public class ObsWebSocketSerializationException : ObsWebSocketException
{
    /// <summary>Initializes a new instance.</summary>
    public ObsWebSocketSerializationException() { }

    /// <summary>Initializes a new instance.</summary>
    /// <param name="message">The error message.</param>
    public ObsWebSocketSerializationException(string message)
        : base(message) { }

    /// <summary>Initializes a new instance.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The underlying cause.</param>
    public ObsWebSocketSerializationException(string message, Exception innerException)
        : base(message, innerException) { }
}
