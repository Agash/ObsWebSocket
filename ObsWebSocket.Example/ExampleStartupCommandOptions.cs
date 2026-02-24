namespace ObsWebSocket.Example;

public sealed class ExampleStartupCommandOptions
{
    public string? Command { get; init; }

    public string[] Arguments { get; init; } = [];
}
