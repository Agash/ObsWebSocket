using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;

namespace ObsWebSocket.Tests.Integration;

/// <summary>
/// Where the live OBS is.
/// </summary>
/// <remarks>
/// Resolution order is run parameters, then environment, then a local settings file. Run
/// parameters come from a .runsettings file or from <c>--test-parameter Obs.ServerUri=...</c>,
/// which is how CI supplies them.
/// </remarks>
internal static class ObsLiveEndpoint
{
    private const string UriParameter = "Obs.ServerUri";
    private const string PasswordParameter = "Obs.Password";
    private const string RequiredParameter = "Obs.Required";

    private static readonly Lazy<(string? Uri, string? Password, string? Required)> s_fallback =
        new(ReadFallback);

    /// <summary>Reads the endpoint for the running test.</summary>
    /// <param name="context">The running test, whose properties carry the run parameters.</param>
    public static (Uri? Uri, string? Password) Resolve(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        string? uri = Parameter(context, UriParameter) ?? s_fallback.Value.Uri;
        string? password = Parameter(context, PasswordParameter) ?? s_fallback.Value.Password;

        return (
            Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed) ? parsed : null,
            string.IsNullOrEmpty(password) ? null : password
        );
    }

    /// <summary>
    /// Returns the endpoint, or reports the test inconclusive when none is configured, so the
    /// suite still runs on a machine without OBS.
    /// </summary>
    /// <param name="context">The running test.</param>
    public static (Uri Uri, string? Password) Require(TestContext context)
    {
        (Uri? uri, string? password) = Resolve(context);
        if (uri is null)
        {
            Unavailable(
                context,
                "No live OBS configured. Pass --test-parameter Obs.ServerUri=ws://host:4455, set Obs__ServerUri, or add testsettings.local.json."
            );
        }

        return (uri, password);
    }

    /// <summary>
    /// Reports a missing prerequisite: inconclusive on a developer machine, a failure where the
    /// run declares the live OBS required.
    /// </summary>
    /// <remarks>
    /// Inconclusive counts as skipped, not failed. Without this, a live run that lost its
    /// endpoint or its fixtures would skip every live test and still pass.
    /// </remarks>
    /// <param name="context">The running test.</param>
    /// <param name="reason">What is missing.</param>
    [DoesNotReturn]
    public static void Unavailable(TestContext context, string reason)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (
            bool.TryParse(
                Parameter(context, RequiredParameter) ?? s_fallback.Value.Required,
                out bool required
            ) && required
        )
        {
            Assert.Fail($"The live OBS is required for this run. {reason}");
        }

        Assert.Inconclusive(reason);
    }

    private static string? Parameter(TestContext context, string name) =>
        context.Properties.TryGetValue(name, out object? value)
        && value is string text
        && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;

    private static (string?, string?, string?) ReadFallback()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("testsettings.local.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        return (
            configuration["Obs:ServerUri"],
            configuration["Obs:Password"],
            configuration["Obs:Required"]
        );
    }
}
