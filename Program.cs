using FlowMaker.ModbusTcp;
using FlowMaker.ModbusTcp.Sources;
using Industream.FlowMaker.Sdk.Clients;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Entry point for FlowMaker Modbus TCP Client
var host = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((context, config) =>
    {
        config.AddEnvironmentVariables();
    })
    .ConfigureServices((context, services) =>
    {
        var startup = new Startup();
        startup.ConfigureServices(services, context.Configuration);
    })
    .ConfigureLogging((context, logging) =>
    {
        logging.ClearProviders();
        logging.AddConsole();
        logging.SetMinimumLevel(LogLevel.Information);
    })
    .Build();

var logger = host.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("===========================================");
logger.LogInformation("  FlowMaker Modbus TCP Client v2.0.5");
logger.LogInformation("===========================================");

// Log environment configuration
logger.LogInformation("FM_WORKER_ID: {WorkerId}",
    Environment.GetEnvironmentVariable("FM_WORKER_ID") ?? "not set");
logger.LogInformation("FM_ROUTER_TRANSPORT_ADDRESS: {Address}",
    Environment.GetEnvironmentVariable("FM_ROUTER_TRANSPORT_ADDRESS") ?? "not set");
logger.LogInformation("FM_RUNTIME_HTTP_ADDRESS: {Address}",
    Environment.GetEnvironmentVariable("FM_RUNTIME_HTTP_ADDRESS") ?? "not set");

try
{
    var flowMakerClientBuilder = host.Services.GetRequiredService<FlowMakerClientBuilder>();

    logger.LogInformation("Registering Modbus TCP Client flow box...");

    await flowMakerClientBuilder
        .DeclareFlowElement(ModbusTcpElement.GetRegistrationInfo())
        .Build()
        .Run();
}
catch (Exception ex)
{
    logger.LogCritical(ex, "FlowMaker Modbus TCP Client failed to start");
    Environment.Exit(1);
}
