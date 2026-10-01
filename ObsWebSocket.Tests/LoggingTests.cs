using System.Reflection;
using Microsoft.Extensions.Logging;
using ObsWebSocket.Core;

namespace ObsWebSocket.Tests;

[TestClass]
public sealed class LoggingTests
{
    [TestMethod]
    public void EventIds_AreExplicitAndUnique()
    {
        List<(int Id, string Method)> ids = [];
        foreach (Type type in typeof(ObsWebSocketClient).Assembly.GetTypes())
        {
            foreach (
                MethodInfo method in type.GetMethods(
                    BindingFlags.Instance
                        | BindingFlags.Static
                        | BindingFlags.Public
                        | BindingFlags.NonPublic
                        | BindingFlags.DeclaredOnly
                )
            )
            {
                if (method.GetCustomAttribute<LoggerMessageAttribute>() is { } attribute)
                {
                    string name = $"{type.FullName}.{method.Name}";
                    Assert.AreNotEqual(-1, attribute.EventId, $"{name} has no event id");
                    ids.Add((attribute.EventId, name));
                }
            }
        }

        Assert.IsGreaterThan(100, ids.Count, $"the log methods were found ({ids.Count})");
        string[] duplicates =
        [
            .. ids.GroupBy(static i => i.Id)
                .Where(static g => g.Count() > 1)
                .Select(static g =>
                    $"{g.Key}: {string.Join(", ", g.Select(static i => i.Method))}"
                ),
        ];
        Assert.IsEmpty(duplicates, string.Join("; ", duplicates));
    }
}
