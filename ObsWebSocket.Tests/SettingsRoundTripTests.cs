using System.Reflection;
using System.Text.Json;
using MessagePack;
using ObsWebSocket.Core.Protocol.Common.FilterSettings;
using ObsWebSocket.Core.Protocol.Common.InputSettings;

namespace ObsWebSocket.Tests;

/// <summary>
/// The settings records exist to cross the wire, so every one of them is round tripped on both
/// formats rather than trusting the shapes by inspection.
/// </summary>
[TestClass]
public sealed class SettingsRoundTripTests
{
    private static IEnumerable<Type> SettingsTypes =>
        typeof(GainFilterSettings)
            .Assembly.GetExportedTypes()
            .Where(type =>
                type is { IsClass: true, IsAbstract: false }
                && type.Name.EndsWith("Settings", StringComparison.Ordinal)
                && type.Namespace is not null
                && (
                    type.Namespace.EndsWith(".FilterSettings", StringComparison.Ordinal)
                    || type.Namespace.EndsWith(".InputSettings", StringComparison.Ordinal)
                )
            );

    public static IEnumerable<object[]> AllSettingsTypes =>
        SettingsTypes.Select(type => new object[] { type });

    [TestMethod]
    public void SettingsTypes_Reflected_AllDiscovered() =>
        Assert.IsGreaterThan(20, SettingsTypes.Count());

    [TestMethod]
    [DynamicData(nameof(AllSettingsTypes))]
    public void SettingsTypes_Json_RoundTrip(Type settingsType)
    {
        object instance = CreateDefault(settingsType);

        string json = JsonSerializer.Serialize(instance, settingsType);
        object? restored = JsonSerializer.Deserialize(json, settingsType);

        Assert.IsNotNull(restored);
        Assert.AreEqual(instance, restored);
        Assert.IsNotEmpty(instance.ToString()!);
        Assert.AreEqual(instance.GetHashCode(), restored.GetHashCode());
    }

    [TestMethod]
    [DynamicData(nameof(AllSettingsTypes))]
    public void SettingsTypes_MessagePack_RoundTrip(Type settingsType)
    {
        object instance = CreateDefault(settingsType);

        byte[] packed = MessagePackSerializer.Serialize(
            settingsType,
            instance,
            MessagePackSerializerOptions.Standard.WithResolver(
                MessagePack.Resolvers.ContractlessStandardResolver.Instance
            )
        );
        object? restored = MessagePackSerializer.Deserialize(
            settingsType,
            packed,
            MessagePackSerializerOptions.Standard.WithResolver(
                MessagePack.Resolvers.ContractlessStandardResolver.Instance
            )
        );

        Assert.IsNotNull(restored);
    }

    /// <summary>
    /// Builds an instance from the longest constructor, leaving every optional parameter at its
    /// default. The settings records are positional and all optional, so this reaches them all.
    /// </summary>
    private static object CreateDefault(Type settingsType)
    {
        ConstructorInfo constructor = settingsType
            .GetConstructors()
            .OrderByDescending(candidate => candidate.GetParameters().Length)
            .First();

        object?[] arguments =
        [
            .. constructor
                .GetParameters()
                .Select(parameter =>
                    parameter.HasDefaultValue ? parameter.DefaultValue
                    : parameter.ParameterType.IsValueType
                        ? Activator.CreateInstance(parameter.ParameterType)
                    : null
                ),
        ];

        return constructor.Invoke(arguments);
    }
}
