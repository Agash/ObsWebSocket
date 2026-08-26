using System.Reflection;
using MessagePack;
using ObsWebSocket.Core.Protocol.Common;
using ObsWebSocket.Core.Protocol.Responses;
using ObsWebSocket.Core.Serialization;

namespace ObsWebSocket.Tests;

/// <summary>
/// A generated record can name a collection type that nothing in the MessagePack resolver chain
/// knows how to build. It costs nothing on JSON and makes the message completely unreadable on
/// MessagePack, which is how <c>GetCanvasList</c> and <c>GetInputAudioTracks</c> both shipped
/// broken on one transport only. This walks the generated surface so the next one fails here.
/// </summary>
[TestClass]
public sealed class FormatterCoverageTests
{
    private static readonly string[] s_generatedNamespaces =
    [
        "ObsWebSocket.Core.Protocol.Requests",
        "ObsWebSocket.Core.Protocol.Responses",
        "ObsWebSocket.Core.Protocol.Events",
    ];

    [TestMethod]
    public void EveryGeneratedProperty_HasAMessagePackFormatter()
    {
        IFormatterResolver resolver = MsgPackMessageSerializer.s_msgPackOptions.Resolver;
        List<string> missing = [];

        foreach (Type generated in GeneratedTypes(s_generatedNamespaces))
        {
            foreach (
                PropertyInfo property in generated.GetProperties(
                    BindingFlags.Public | BindingFlags.Instance
                )
            )
            {
                // MessagePack never reads an ignored member, so it needs no formatter.
                if (property.GetCustomAttribute<IgnoreMemberAttribute>() is not null)
                {
                    continue;
                }

                Type type =
                    Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

                // Primitives, enums and the generated records themselves are covered by the built
                // in and source generated resolvers; the constructed generics are what get missed.
                if (!type.IsGenericType || !Resolves(resolver, type))
                {
                    if (type.IsGenericType)
                    {
                        missing.Add($"{generated.Name}.{property.Name} -> {Describe(type)}");
                    }
                }
            }
        }

        Assert.IsTrue(
            missing.Count == 0,
            $"No MessagePack formatter for: {string.Join(", ", missing.Distinct())}"
        );
    }

    /// <summary>
    /// A stub is serialized whole through the JSON bridge, so what has to resolve is the stub and
    /// its list, not the members inside it. Registering the bare form and forgetting the list is
    /// the specific mistake that made <c>GetCanvasList</c> unreadable.
    /// </summary>
    [TestMethod]
    public void EveryStub_ResolvesBothAloneAndInAList()
    {
        IFormatterResolver resolver = MsgPackMessageSerializer.s_msgPackOptions.Resolver;
        List<string> missing = [];

        Type[] stubs = [.. GeneratedTypes(["ObsWebSocket.Core.Protocol.Common"])];
        Assert.IsGreaterThan(10, stubs.Length, "expected the stub types to be discovered");

        foreach (Type stub in stubs)
        {
            if (!Resolves(resolver, stub))
            {
                missing.Add(stub.Name);
            }

            Type list = typeof(List<>).MakeGenericType(stub);
            if (!Resolves(resolver, list))
            {
                missing.Add($"List<{stub.Name}>");
            }
        }

        Assert.IsTrue(
            missing.Count == 0,
            $"No MessagePack formatter for: {string.Join(", ", missing)}"
        );
    }

    private static IEnumerable<Type> GeneratedTypes(string[] namespaces) =>
        typeof(GetVersionResponseData)
            .Assembly.GetTypes()
            .Where(t =>
                t.IsClass
                && !t.IsAbstract
                && t.IsPublic
                && t.Namespace is not null
                && namespaces.Contains(t.Namespace, StringComparer.Ordinal)
            );

    private static bool Resolves(IFormatterResolver resolver, Type type)
    {
        try
        {
            return resolver.GetFormatterDynamic(type) is not null;
        }
        catch (FormatterNotRegisteredException)
        {
            return false;
        }
    }

    private static string Describe(Type type) =>
        $"{type.Name}<{string.Join(", ", type.GetGenericArguments().Select(a => a.Name))}>";
}
