using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ObsWebSocket.Core;
using ObsWebSocket.Example;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// Reads appsettings.json, environment variables, command-line args
builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

builder.Logging.ClearProviders();
builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
builder.Logging.AddConsole();

// Configure OBS WebSocket Client options from "Obs" section in appsettings.json
builder.Services.Configure<ObsWebSocketClientOptions>(builder.Configuration.GetSection("Obs"));

// Add the ObsWebSocketClient and its dependencies
builder.Services.AddObsWebSocketClient();

// Add our background service
builder.Services.AddHostedService<Worker>();

using IHost host = builder.Build();

await host.RunAsync();

Console.WriteLine("\nExample application finished.");
