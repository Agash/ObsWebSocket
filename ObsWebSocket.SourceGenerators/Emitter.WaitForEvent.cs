// ObsWebSocket.SourceGenerators/Emitter.WaitForEvent.cs
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace ObsWebSocket.SourceGenerators;

/// <summary>
/// Contains emitter logic specifically for generating the WaitForEventAsync helper method.
/// </summary>
internal static partial class Emitter
{
    /// <summary>
    /// Generates the source code for the WaitForEventAsync extension method.
    /// </summary>
    /// <param name="context">The source production context.</param>
    /// <param name="protocol">The parsed protocol definition.</param>
    public static void GenerateWaitForEventHelper(
        SourceProductionContext context,
        ProtocolDefinition protocol
    )
    {
        if (protocol.Events is null || protocol.Events.Count == 0)
        {
            return; // No events to wait for
        }

        StringBuilder builder = BuildSourceHeader("// Helper: WaitForEventAsync<TEventArgs>");

        // Add necessary usings
        builder.AppendLine("using System;");
        builder.AppendLine("using System.Threading;");
        builder.AppendLine("using System.Threading.Tasks;");
        builder.AppendLine("using Microsoft.Extensions.Logging;"); // For client._logger access
        builder.AppendLine("using ObsWebSocket.Core;"); // For ObsWebSocketClient, ObsEventArgs, ObsWebSocketException
        builder.AppendLine("using ObsWebSocket.Core.Events;"); // Base EventArgs
        builder.AppendLine($"using {GeneratedEventArgsNamespace};"); // Where the specific generated EventArgs live

        builder.AppendLine();
        builder.AppendLine(
            $"namespace {ExtensionsNamespace}; // Add to ObsWebSocket.Core namespace"
        );
        builder.AppendLine();
        builder.AppendLine("/// <summary>");
        builder.AppendLine(
            "/// Contains generated helper methods for the <see cref=\"ObsWebSocketClient\"/>."
        );
        builder.AppendLine("/// </summary>");
        builder.AppendLine("public static partial class ObsWebSocketClientHelpers");
        builder.AppendLine("{");

        // Generate the WaitForEventAsync method signature and documentation
        builder.AppendLine("    /// <summary>");
        builder.AppendLine(
            "    /// Asynchronously waits for a specific OBS event of type <typeparamref name=\"TEventArgs\"/> that satisfies a predicate condition."
        );
        builder.AppendLine("    /// </summary>");
        builder.AppendLine(
            "    /// <typeparam name=\"TEventArgs\">The specific type of <see cref=\"ObsEventArgs\"/> to wait for.</typeparam>"
        );
        builder.AppendLine(
            "    /// <param name=\"client\">The <see cref=\"ObsWebSocketClient\"/> instance.</param>"
        );
        builder.AppendLine(
            "    /// <param name=\"predicate\">A function to test each received event of the specified type for a condition.</param>"
        );
        builder.AppendLine(
            "    /// <param name=\"timeout\">The maximum time to wait for the event.</param>"
        );
        builder.AppendLine(
            "    /// <param name=\"cancellationToken\">A token to cancel the wait operation externally.</param>"
        );
        builder.AppendLine("    /// <returns>");
        builder.AppendLine(
            "    /// A task representing the asynchronous operation. The task result contains the <typeparamref name=\"TEventArgs\"/> instance"
        );
        builder.AppendLine(
            "    /// that satisfied the predicate, or <c>null</c> if the timeout occurred or the operation was canceled."
        );
        builder.AppendLine("    /// </returns>");
        builder.AppendLine("    /// <remarks>");
        builder.AppendLine(
            "    /// This method subscribes temporarily to the corresponding event on the client, waits for a matching event or timeout/cancellation,"
        );
        builder.AppendLine(
            "    /// and then automatically unsubscribes. It ensures that the subscription happens *before* the method returns,"
        );
        builder.AppendLine(
            "    /// making it suitable for 'subscribe-then-act' patterns to avoid race conditions."
        );
        builder.AppendLine(
            "    /// This helper is generated based on the OBS WebSocket protocol definition and avoids runtime reflection."
        );
        builder.AppendLine("    /// </remarks>");
        builder.AppendLine(
            "    /// <exception cref=\"ArgumentNullException\">Thrown if client or predicate is null.</exception>"
        );
        builder.AppendLine(
            "    /// <exception cref=\"InvalidOperationException\">Thrown if the client is not connected.</exception>"
        );
        builder.AppendLine(
            "    /// <exception cref=\"NotSupportedException\">Thrown if waiting for the specified <typeparamref name=\"TEventArgs\"/> type is not supported (e.g., no corresponding event found in the protocol).</exception>"
        );
        builder.AppendLine(
            "    public static async Task<TEventArgs?> WaitForEventAsync<TEventArgs>("
        );
        builder.AppendLine("        this ObsWebSocketClient client,");
        builder.AppendLine("        Func<TEventArgs, bool> predicate,");
        builder.AppendLine("        TimeSpan timeout,");
        builder.AppendLine(
            "        CancellationToken cancellationToken = default) where TEventArgs : ObsEventArgs"
        );
        builder.AppendLine("    {");

        // Add common setup code
        builder.AppendLine("        ArgumentNullException.ThrowIfNull(client);");
        builder.AppendLine("        ArgumentNullException.ThrowIfNull(predicate);");
        builder.AppendLine();
        builder.AppendLine("        client.EnsureConnected(); // Checks if connection is active");
        builder.AppendLine();
        builder.AppendLine(
            "        // Use RunContinuationsAsynchronously to avoid potential deadlocks if predicate runs synchronously"
        );
        builder.AppendLine(
            "        TaskCompletionSource<TEventArgs?> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);"
        );
        builder.AppendLine("        // Link the external token with our internal timeout token");
        builder.AppendLine(
            "        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);"
        );
        builder.AppendLine("        linkedCts.CancelAfter(timeout); // Apply timeout");
        builder.AppendLine();
        builder.AppendLine(
            "        Action? unsubscribeAction = null; // Delegate to hold the unsubscribe logic"
        );
        builder.AppendLine();
        builder.AppendLine("        try");
        builder.AppendLine("        {");

        // Start the switch statement to map TEventArgs type to the specific event subscription
        builder.AppendLine(
            "            // This switch is generated based on known events with data from protocol.json"
        );
        builder.AppendLine("            switch (typeof(TEventArgs))");
        builder.AppendLine("            {");

        // Iterate through events that have data fields (and thus specific EventArgs)
        foreach (OBSEvent? eventDef in protocol.Events.Where(e => e.DataFields?.Count > 0))
        {
            try
            {
                string eventName = SanitizeIdentifier(eventDef.EventType);
                string eventArgsTypeName = $"{GeneratedEventArgsNamespace}.{eventName}EventArgs";
                string eventFieldName = eventName; // Assumes event field name matches sanitized EventType

                // Generate a case for this specific EventArgs type
                builder.AppendLine(
                    $"                case Type t when t == typeof({eventArgsTypeName}):"
                );
                builder.AppendLine("                {");
                // Define the specific handler lambda
                builder.AppendLine(
                    $"                    EventHandler<{eventArgsTypeName}> specificHandler = (sender, e) =>"
                );
                builder.AppendLine("                    {");
                builder.AppendLine("                        try");
                builder.AppendLine("                        {");
                // The cast is necessary because predicate expects TEventArgs, but 'e' is specific
                builder.AppendLine(
                    "                            if (predicate((TEventArgs)(object)e))"
                );
                builder.AppendLine("                            {");
                // ----- FIX: Add explicit cast to TEventArgs? via object? -----
                builder.AppendLine(
                    "                                tcs.TrySetResult((TEventArgs?)(object?)e);"
                );
                builder.AppendLine("                            }");
                builder.AppendLine("                        }");
                builder.AppendLine("                        catch (Exception ex)");
                builder.AppendLine("                        {");
                builder.AppendLine(
                    "                            // Catch exceptions from user predicate and fail the task"
                );
                builder.AppendLine("                            tcs.TrySetException(ex);");
                builder.AppendLine("                        }");
                builder.AppendLine("                    };");
                // Subscribe the handler
                builder.AppendLine(
                    $"                    client.{eventFieldName} += specificHandler;"
                );
                // Assign the correct unsubscribe logic to the delegate
                // The lambda correctly captures the specificHandler variable
                builder.AppendLine(
                    $"                    unsubscribeAction = () => client.{eventFieldName} -= specificHandler;"
                );
                builder.AppendLine("                    break;");
                builder.AppendLine("                }");
            }
            catch (Exception ex)
            {
                // Report error for a specific event but continue generating others
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        Diagnostics.IdentifierGenerationError,
                        Location.None,
                        eventDef.EventType,
                        $"Generating WaitForEvent case for {eventDef.EventType}",
                        ex.ToString() // Include full exception details for debugging
                    )
                );
            }
        }

        // Add the default case for unsupported types
        builder.AppendLine("                default:");
        builder.AppendLine(
            $"                    throw new NotSupportedException($\"Waiting for event type '{{typeof(TEventArgs).FullName}}' is not supported by the generated WaitForEventAsync. Check if it's a valid event args type derived from ObsEventArgs and corresponds to an event with data fields in the protocol.\");"
        );

        // Close the switch statement
        builder.AppendLine("            }");
        builder.AppendLine();

        // Wait for the event handler to complete the TCS or for cancellation/timeout
        builder.AppendLine(
            "            // Wait for the TaskCompletionSource to be set by the event handler or for cancellation/timeout"
        );
        builder.AppendLine(
            "            await tcs.Task.WaitAsync(linkedCts.Token).ConfigureAwait(false);"
        );
        builder.AppendLine(
            "            // Retrieve the result. If WaitAsync threw OperationCanceledException, this won't be reached."
        );
        builder.AppendLine(
            "            // If the TCS was set with an exception (e.g., from predicate), awaiting here will rethrow it."
        );
        builder.AppendLine("            return await tcs.Task;");

        builder.AppendLine("        }"); // End try
        builder.AppendLine(
            "        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)"
        );
        builder.AppendLine("        {");
        builder.AppendLine(
            "            // Timeout occurred or external cancellation token was triggered"
        );
        builder.AppendLine(
            "            // Log level Debug is appropriate as timeout/cancellation can be expected conditions"
        );
        builder.AppendLine(
            "            client._logger.LogDebug(\"WaitForEventAsync<{EventType}> timed out or was canceled.\", typeof(TEventArgs).Name);"
        );
        builder.AppendLine(
            "            // Ensure the TaskCompletionSource reflects the cancellation"
        );
        builder.AppendLine("            tcs.TrySetCanceled(linkedCts.Token);");
        builder.AppendLine(
            "            return null; // Return null as per method contract for timeout/cancellation"
        );
        builder.AppendLine("        }");
        builder.AppendLine("        catch (Exception ex)");
        builder.AppendLine("        {");
        builder.AppendLine(
            "            // Catch exceptions from predicate execution propagated via tcs.TrySetException or other unexpected errors"
        );
        builder.AppendLine(
            "            client._logger.LogError(ex, \"Exception occurred during WaitForEventAsync<{EventType}> predicate evaluation or waiting.\", typeof(TEventArgs).Name);"
        );
        builder.AppendLine(
            "            // Ensure the TaskCompletionSource reflects the error if it wasn't already set"
        );
        builder.AppendLine("            tcs.TrySetException(ex);");
        builder.AppendLine("            throw; // Re-throw the exception to the caller");
        builder.AppendLine("        }");
        builder.AppendLine("        finally");
        builder.AppendLine("        {");
        builder.AppendLine(
            "            // Crucial: Always attempt to unsubscribe the temporary handler"
        );
        builder.AppendLine(
            "            // This prevents memory leaks if the method completes or throws."
        );
        builder.AppendLine("            unsubscribeAction?.Invoke();");
        builder.AppendLine("        }");
        builder.AppendLine("    }"); // End WaitForEventAsync method

        // Close class and namespace
        builder.AppendLine("}"); // End ObsWebSocketClientHelpers class
        // File-scoped namespace is assumed, no closing brace needed here

        // Add the generated source file to the compilation
        context.AddSource(
            "ObsWebSocketClient.WaitForEvent.g.cs",
            SourceText.From(builder.ToString(), Encoding.UTF8)
        );
    }
}
