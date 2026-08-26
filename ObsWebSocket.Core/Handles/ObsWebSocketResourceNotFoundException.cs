namespace ObsWebSocket.Core;

/// <summary>
/// Thrown when a name does not match anything in OBS.
/// </summary>
/// <remarks>
/// Resolving a name means fetching the list it would have been in, so the list is already in hand
/// when the lookup misses. Saying what does exist costs nothing and turns a typo from a puzzle
/// into an answer, which is more than OBS itself can offer: its own reply is
/// <c>ResourceNotFound</c> and the name you already knew.
/// </remarks>
public sealed class ObsWebSocketResourceNotFoundException : ObsWebSocketException
{
    /// <summary>Initializes a new instance.</summary>
    public ObsWebSocketResourceNotFoundException() { }

    /// <summary>Initializes a new instance with a message.</summary>
    /// <param name="message">The message.</param>
    public ObsWebSocketResourceNotFoundException(string message)
        : base(message) { }

    /// <summary>Initializes a new instance with a message and an inner exception.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The underlying failure.</param>
    public ObsWebSocketResourceNotFoundException(string message, Exception innerException)
        : base(message, innerException) { }

    private ObsWebSocketResourceNotFoundException(
        string message,
        string kind,
        string requestedName,
        IReadOnlyList<string> available,
        Exception? innerException
    )
        : base(message, innerException!)
    {
        Kind = kind;
        RequestedName = requestedName;
        Available = available;
    }

    /// <summary>What was being looked for, such as <c>scene</c> or <c>input</c>.</summary>
    public string? Kind { get; }

    /// <summary>The name that did not match.</summary>
    public string? RequestedName { get; }

    /// <summary>The names that did exist when the lookup ran.</summary>
    public IReadOnlyList<string> Available { get; } = [];

    /// <summary>Builds the exception, including the available names in the message.</summary>
    /// <param name="kind">What was being looked for.</param>
    /// <param name="requestedName">The name that did not match.</param>
    /// <param name="available">The names that did exist.</param>
    /// <param name="canvas">The canvas searched, when the search was canvas scoped.</param>
    /// <param name="innerException">The underlying failure, when there was one.</param>
    internal static ObsWebSocketResourceNotFoundException For(
        string kind,
        string requestedName,
        IReadOnlyList<string> available,
        CanvasHandle? canvas,
        Exception? innerException = null
    )
    {
        string scope =
            canvas is null || ReferenceEquals(canvas, CanvasHandle.Main)
                ? string.Empty
                : $" on {canvas}";

        string names =
            available.Count == 0
                ? "There are none."
                : $"Available: {string.Join(", ", available.Select(n => $"'{n}'"))}.";

        return new ObsWebSocketResourceNotFoundException(
            $"No {kind} named '{requestedName}'{scope}. {names}",
            kind,
            requestedName,
            available,
            innerException
        );
    }
}
