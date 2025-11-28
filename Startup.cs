using Industream.FlowMaker.Sdk.Clients;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FlowMaker.ModbusTcp;

public class Startup
{
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // Add logging
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        // Configure FlowMaker client options from environment variables
        services.AddOptions<FlowMakerClientOptions>()
            .Configure(options =>
            {
                options.RouterTransportAddress =
                    Environment.GetEnvironmentVariable("FM_ROUTER_TRANSPORT_ADDRESS")
                    ?? throw new InvalidOperationException("FM_ROUTER_TRANSPORT_ADDRESS not set");

                options.RuntimeHttpUri =
                    Environment.GetEnvironmentVariable("FM_RUNTIME_HTTP_ADDRESS")
                    ?? throw new InvalidOperationException("FM_RUNTIME_HTTP_ADDRESS not set");

                options.LoggerAddress =
                    Environment.GetEnvironmentVariable("FM_WORKER_LOG_SOCKET_IO_ENDPOINT")
                    ?? throw new InvalidOperationException("FM_WORKER_LOG_SOCKET_IO_ENDPOINT not set");
            })
            .Validate(options =>
                !string.IsNullOrEmpty(options.RouterTransportAddress) &&
                !string.IsNullOrEmpty(options.RuntimeHttpUri) &&
                !string.IsNullOrEmpty(options.LoggerAddress),
                "All FlowMakerClientOptions properties must be set via environment variables.");

        // Add FlowMaker SDK services
        services.AddFlowMakerBuilder();
    }

    public void Configure(ILogger<Startup> logger)
    {
        logger.LogInformation("FlowMaker Modbus TCP Client is starting...");
        logger.LogInformation("Worker ID: {WorkerId}",
            Environment.GetEnvironmentVariable("FM_WORKER_ID") ?? "not set");
    }
}
