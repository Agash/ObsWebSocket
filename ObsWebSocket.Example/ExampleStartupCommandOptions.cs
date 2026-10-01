namespace ObsWebSocket.Example;

/// <summary>A command the example runs once it has connected, instead of the interactive prompt.</summary>
public sealed class ExampleStartupCommandOptions
{
    /// <summary>The command's name, as typed at the prompt; none runs the prompt.</summary>
    public string? Command { get; init; }

    /// <summary>The command's arguments.</summary>
    public string[] Arguments { get; init; } = [];
}
