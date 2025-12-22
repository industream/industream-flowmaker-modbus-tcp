using FlowMaker.ModbusTcp.Models;
using FlowMaker.ModbusTcp.Models.DataCatalog;
using FlowMaker.ModbusTcp.Services;
using Industream.FlowMaker.Sdk.Clients;
using Industream.FlowMaker.Sdk.Elements;
using Industream.FlowMaker.Sdk.Elements.Advanced;
using Industream.FlowMaker.Sdk.Serializations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Collections.Concurrent;

namespace FlowMaker.ModbusTcp.Sources;

/// <summary>
/// Helper to load UI configuration from external file
/// </summary>
internal static class UiConfigLoader
{
    public static string? GetJsBundleImplementation()
    {
        var configPath = Environment.GetEnvironmentVariable("FM_WORKER_APP_CONFIG") ?? "/usr/app/config/";
        var filePath = Path.Combine(configPath, "config.source.jsbundle.js");

        if (File.Exists(filePath))
        {
            var content = File.ReadAllText(filePath);
            return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(content));
        }

        return null;
    }
}

/// <summary>
/// Modbus TCP Source Flow Box - Reads data from a Modbus TCP server and pushes to downstream elements
/// Connection parameters are retrieved from DataCatalog via SourceConnectionId
/// </summary>
public class ModbusTcpElement : FlowBoxRaw, IFlowBoxSource, IFlowBoxDestroy
{
    private readonly InOutSubject _inOutSubject = new();
    private readonly ModbusTcpConnectionService _connectionService;
    private readonly CancellationTokenSource _cts = new();
    private readonly ISerializer _serializer;
    private readonly Header _header;
    private readonly ModbusTcpOptions _options;
    private readonly DataCatalogService _dataCatalogService;
    private Task? _pollingTask;

    // Resolved configuration from DataCatalog
    private ModbusTcpConnectionConfig? _resolvedConnectionConfig;

    // Error tracking
    private readonly ConcurrentQueue<DateTime> _recentErrors = new();
    private const int MaxRecentErrors = 100;
    private long _totalPushErrors = 0;
    private long _successfulPushes = 0;

    public ModbusTcpElement(FlowBoxInitParams metadata, IServiceProvider serviceProvider)
        : base(metadata, serviceProvider)
    {
        _options = metadata.OptionsAs<ModbusTcpOptions>();
        _serializer = Serialization.GetSerializer(SerializationMethods.Msgpack);
        _header = new Header(SerializationMethods.Msgpack);

        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger<ModbusTcpConnectionService>();
        var dataCatalogLogger = loggerFactory.CreateLogger<DataCatalogService>();

        // Initialize DataCatalog service (always required)
        var apiUrl = Environment.GetEnvironmentVariable("FM_DATACATALOG_URL") ?? "http://datacatalog-api:8002";
        _dataCatalogService = new DataCatalogService(apiUrl, dataCatalogLogger);
        Log(LogLevel.Information, $"DataCatalog API URL: {apiUrl}");

        _connectionService = new ModbusTcpConnectionService(_options, logger);

        var registers = _options.GetRegisterDefinitions();
        Log(LogLevel.Information, $"Modbus TCP Element created");
        Log(LogLevel.Information, $"SourceConnectionId: {_options.SourceConnectionId}");
        Log(LogLevel.Information, $"Polling interval: {_options.PollingIntervalMs}ms");
        Log(LogLevel.Information, $"Configured {registers.Count} registers");

        // Subscribe to connection status changes
        _connectionService.OnConnectionStatusChanged += OnConnectionStatusChanged;

        // Subscribe to data received events
        _connectionService.OnDataReceived += OnDataReceived;

        // Start connection and polling
        _pollingTask = StartPollingAsync();
    }

    private void OnConnectionStatusChanged(bool connected, string message)
    {
        if (connected)
        {
            Log(LogLevel.Information, $"Connection status: {message}");
        }
        else
        {
            Log(LogLevel.Warning, $"Connection status: {message}");
        }
    }

    private void OnDataReceived(ModbusDataPoint dataPoint)
    {
        try
        {
            // Use proper async handling instead of fire-and-forget
            PushDataWithRetryAsync(dataPoint, CancellationToken.None)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        TrackError();
                        Log(LogLevel.Error, $"Failed to push data after retries: {t.Exception?.GetBaseException().Message}");
                    }
                    else
                    {
                        Interlocked.Increment(ref _successfulPushes);
                    }
                }, TaskContinuationOptions.ExecuteSynchronously);

            Log(LogLevel.Trace, $"Received {dataPoint.Values.Count} register values");
        }
        catch (Exception ex)
        {
            TrackError();
            Log(LogLevel.Error, $"Error handling received data: {ex.Message}");
        }
    }

    private void TrackError()
    {
        Interlocked.Increment(ref _totalPushErrors);
        _recentErrors.Enqueue(DateTime.UtcNow);

        // Keep only recent errors
        while (_recentErrors.Count > MaxRecentErrors)
        {
            _recentErrors.TryDequeue(out _);
        }
    }

    private async Task StartPollingAsync()
    {
        var retryCount = 0;
        const int maxInitialRetries = 5;
        const int initialRetryDelayMs = 2000;

        // Resolve configuration from DataCatalog
        try
        {
            await ResolveDataCatalogConfigurationAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Failed to resolve DataCatalog configuration: {ex.Message}");
            throw new InvalidOperationException($"DataCatalog configuration required. Error: {ex.Message}", ex);
        }

        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                Log(LogLevel.Information, $"Connecting to Modbus TCP server: {_options.Host}:{_options.Port}...");
                await _connectionService.ConnectAsync(_cts.Token);
                Log(LogLevel.Information, "Connected to Modbus TCP server successfully");

                var registers = _options.GetRegisterDefinitions();
                if (registers.Count == 0)
                {
                    Log(LogLevel.Warning, "No registers configured - polling not started");
                    await Task.Delay(30000, _cts.Token);
                    continue;
                }

                Log(LogLevel.Information, "Starting polling...");
                _connectionService.StartPolling(_cts.Token);
                Log(LogLevel.Information, $"Polling active - reading {registers.Count} registers every {_options.PollingIntervalMs}ms");

                // Reset retry count on successful connection
                retryCount = 0;

                // Keep the task alive until cancellation
                try
                {
                    await Task.Delay(Timeout.Infinite, _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                Log(LogLevel.Information, "Modbus TCP polling stopped (cancelled)");
                break;
            }
            catch (Exception ex)
            {
                retryCount++;
                var delay = Math.Min(initialRetryDelayMs * (int)Math.Pow(2, retryCount - 1), 60000);

                if (retryCount <= maxInitialRetries)
                {
                    Log(LogLevel.Warning, $"Initial connection failed (attempt {retryCount}/{maxInitialRetries}): {ex.Message}. Retrying in {delay}ms...");
                }
                else
                {
                    Log(LogLevel.Error, $"Connection failed (attempt {retryCount}): {ex.Message}. Retrying in {delay}ms...");
                }

                try
                {
                    await Task.Delay(delay, _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        Log(LogLevel.Information, "Modbus TCP polling task ended");
    }

    private async Task PushDataWithRetryAsync(ModbusDataPoint data, CancellationToken cancellationToken)
    {
        const int maxRetries = 3;
        const int retryDelayMs = 100;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                await PushDataAsync(data);
                return;
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                Log(LogLevel.Warning, $"Push attempt {attempt} failed: {ex.Message}. Retrying...");
                await Task.Delay(retryDelayMs * attempt, cancellationToken);
            }
        }

        // Final attempt - let it throw
        await PushDataAsync(data);
    }

    /// <summary>
    /// Resolves connection configuration from DataCatalog using SourceConnectionId
    /// </summary>
    private async Task ResolveDataCatalogConfigurationAsync(CancellationToken cancellationToken)
    {
        Log(LogLevel.Information, "Resolving configuration from DataCatalog...");

        // Validate SourceConnectionId
        if (string.IsNullOrWhiteSpace(_options.SourceConnectionId))
        {
            throw new InvalidOperationException("SourceConnectionId is required");
        }

        if (!Guid.TryParse(_options.SourceConnectionId, out var sourceId))
        {
            throw new InvalidOperationException($"Invalid SourceConnectionId format: {_options.SourceConnectionId}");
        }

        // Get SourceConnection from DataCatalog
        var sourceConnection = await _dataCatalogService.GetSourceConnectionByIdAsync(sourceId, cancellationToken);

        if (sourceConnection == null)
        {
            throw new InvalidOperationException($"SourceConnection not found in DataCatalog: {sourceId}");
        }

        Log(LogLevel.Information, $"Found SourceConnection: {sourceConnection.Name} (ID: {sourceConnection.Id})");

        // Extract Modbus TCP connection configuration from SourceConnection
        _resolvedConnectionConfig = DataCatalogService.ExtractModbusTcpConfig(sourceConnection);

        // Apply resolved connection config to options (for ModbusTcpConnectionService)
        ApplyResolvedConnectionConfig(_resolvedConnectionConfig);

        Log(LogLevel.Information, $"Resolved connection: {_resolvedConnectionConfig.Host}:{_resolvedConnectionConfig.Port}");
        Log(LogLevel.Information, $"Resolved SlaveId: {_resolvedConnectionConfig.SlaveId}");
        Log(LogLevel.Information, $"Resolved PollingInterval: {_resolvedConnectionConfig.PollingIntervalMs}ms");
        Log(LogLevel.Information, $"Resolved ByteOrder: {_resolvedConnectionConfig.ByteOrder}");
    }

    /// <summary>
    /// Applies resolved connection configuration to the options
    /// </summary>
    private void ApplyResolvedConnectionConfig(ModbusTcpConnectionConfig config)
    {
        _options.Host = config.Host;
        _options.Port = config.Port;
        _options.SlaveId = config.SlaveId;

        // Use values from DataCatalog if they have non-default values
        if (config.ConnectionTimeoutMs > 0)
            _options.ConnectionTimeoutMs = config.ConnectionTimeoutMs;
        if (config.ReadTimeoutMs > 0)
            _options.ReadTimeoutMs = config.ReadTimeoutMs;
        if (config.PollingIntervalMs > 0)
            _options.PollingIntervalMs = config.PollingIntervalMs;
        if (!string.IsNullOrEmpty(config.ByteOrder))
            _options.ByteOrderStr = config.ByteOrder;
    }

    private async Task PushDataAsync(ModbusDataPoint data)
    {
        var tcs = new TaskCompletionSource();

        // Add timeout to prevent indefinite blocking
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        byte[] serializedData = _serializer.Serialize(data);
        _inOutSubject.InputSubject.OnNext((_header, serializedData, tcs.SetResult));

        var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, timeoutCts.Token));

        if (completedTask != tcs.Task)
        {
            throw new TimeoutException("Push data timed out after 30 seconds");
        }

        await tcs.Task;
    }

    public void OnOutputReady(byte[] outputId, Header header, PushOutDelegate pushOut)
    {
        _inOutSubject.OutputSubject.OnNext(pushOut);
    }

    public void OnDestroy()
    {
        Log(LogLevel.Information, "Destroying Modbus TCP Element...");

        // Unsubscribe from events
        _connectionService.OnDataReceived -= OnDataReceived;
        _connectionService.OnConnectionStatusChanged -= OnConnectionStatusChanged;

        _cts.Cancel();

        try
        {
            // Wait for polling task with timeout
            if (_pollingTask != null)
            {
                var completed = _pollingTask.Wait(TimeSpan.FromSeconds(10));
                if (!completed)
                {
                    Log(LogLevel.Warning, "Polling task did not complete within timeout");
                }
            }
        }
        catch (AggregateException ex)
        {
            foreach (var inner in ex.Flatten().InnerExceptions)
            {
                if (inner is not OperationCanceledException)
                {
                    Log(LogLevel.Warning, $"Error during shutdown: {inner.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log(LogLevel.Warning, $"Unexpected error during shutdown: {ex.Message}");
        }

        _connectionService.Dispose();
        _dataCatalogService.Dispose();
        _inOutSubject.InputSubject.OnCompleted();
        _inOutSubject.OutputSubject.OnCompleted();

        // Dispose CancellationTokenSource
        try { _cts.Dispose(); } catch { /* Ignore */ }

        // Log final statistics
        var health = _connectionService.Health;
        Log(LogLevel.Information, $"Modbus TCP Element destroyed. Stats: Reads={health.TotalReadsCompleted}, " +
            $"PushSuccess={_successfulPushes}, PushErrors={_totalPushErrors}, ConnErrors={health.TotalErrors}");
    }

    /// <summary>
    /// Returns the registration info for this flow box
    /// </summary>
    public static FlowBoxRegistrationInfo GetRegistrationInfo()
    {
        var registrationInfo = new RegistrationInfoBuilder(FlowElementType.Source)
            .SetId("modbus-tcp-client", "industream")
            .SetDisplayName("Modbus TCP Client")
            .SetCurrentVersion("2.0.5")
            .SetIcon("settings_input_hdmi")
            .SetUiConfigType("js-bundle")
            .SetOutputs([
                new TypeDefinition("default", "Modbus Data", [SerializationMethods.Msgpack])
            ])
            .SetDefaultOptionValues(new ModbusTcpOptions
            {
                SourceConnectionId = "",
                PollingIntervalMs = 1000,
                ConnectionTimeoutMs = 5000,
                ReadTimeoutMs = 3000,
                Retries = 3,
                RetryDelayMs = 500,
                ByteOrderStr = "BigEndian",
                Registers = ""
            })
            .Build((meta, services) => new ModbusTcpElement(meta, services));

        // Load js-bundle implementation from external file
        var implementation = UiConfigLoader.GetJsBundleImplementation();
        if (implementation != null)
        {
            registrationInfo.UiConfig.Implementation = implementation;
        }

        return registrationInfo;
    }

    /// <summary>
    /// Internal subject for backpressure handling between data production and consumption
    /// </summary>
    private class InOutSubject
    {
        public Subject<(Header, byte[], Action)> InputSubject = new();
        public Subject<PushOutDelegate> OutputSubject = new();

        public InOutSubject()
        {
            InputSubject.Zip(OutputSubject, (inputs, outputs) => (Inputs: inputs, Outputs: outputs))
                .Subscribe(pair =>
                {
                    var (inputHeader, inputData, inputResolver) = pair.Inputs;
                    var outputResolver = pair.Outputs;
                    outputResolver(inputHeader, inputData);
                    inputResolver();
                });
        }
    }
}
