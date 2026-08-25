namespace ObsWebSocket.Core;

/// <summary>Thrown when a scene item cannot be found by name within a scene.</summary>
[Serializable]
public class SceneItemNotFoundException : ObsWebSocketException
{
    /// <summary>
    /// The name of the scene where the item was expected to be found.
    /// </summary>
    public string? SceneName { get; }

    /// <summary>
    /// The name of the source that was expected to be found within the scene.
    /// </summary>
    public string? SourceName { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SceneItemNotFoundException"/> class with a specified error message.
    /// </summary>
    /// <param name="message"></param>
    /// <param name="sceneName"></param>
    /// <param name="sourceName"></param>
    public SceneItemNotFoundException(
        string message,
        string? sceneName = null,
        string? sourceName = null
    )
        : base(message)
    {
        SceneName = sceneName;
        SourceName = sourceName;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SceneItemNotFoundException"/> class with a specified error message and inner exception.
    /// </summary>
    /// <param name="message"></param>
    /// <param name="innerException"></param>
    /// <param name="sceneName"></param>
    /// <param name="sourceName"></param>
    public SceneItemNotFoundException(
        string message,
        Exception? innerException,
        string? sceneName = null,
        string? sourceName = null
    )
        : base(message, innerException)
    {
        SceneName = sceneName;
        SourceName = sourceName;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SceneItemNotFoundException"/> class with no message or inner exception.
    /// </summary>
    public SceneItemNotFoundException() { }

    /// <summary>
    /// Initializes a new instance of the <see cref="SceneItemNotFoundException"/> class with a specified error message.
    /// </summary>
    /// <param name="message"></param>
    public SceneItemNotFoundException(string message)
        : base(message) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="SceneItemNotFoundException"/> class with a specified error message and inner exception.
    /// </summary>
    /// <param name="message"></param>
    /// <param name="inner"></param>
    public SceneItemNotFoundException(string message, Exception inner)
        : base(message, inner) { }
}
