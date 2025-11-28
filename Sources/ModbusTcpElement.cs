using FlowMaker.ModbusTcp.Models;
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
    public static string? GetConfigImplementation()
    {
        var configPath = Environment.GetEnvironmentVariable("FM_WORKER_APP_CONFIG") ?? "/usr/app/config/";
        var filePath = Path.Combine(configPath, "config.uimaker.json");

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
/// Production-ready with automatic reconnection, health monitoring, and proper error handling
/// </summary>
public class ModbusTcpElement : FlowBoxRaw, IFlowBoxSource, IFlowBoxDestroy
{
    private readonly InOutSubject _inOutSubject = new();
    private readonly ModbusTcpConnectionService _connectionService;
    private readonly CancellationTokenSource _cts = new();
    private readonly ISerializer _serializer;
    private readonly Header _header;
    private readonly ModbusTcpOptions _options;
    private Task? _pollingTask;

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

        _connectionService = new ModbusTcpConnectionService(_options, logger);

        var registers = _options.GetRegisterDefinitions();
        Log(LogLevel.Information, $"Modbus TCP Element created for {_options.Host}:{_options.Port}");
        Log(LogLevel.Information, $"Slave ID: {_options.SlaveId}, Polling interval: {_options.PollingIntervalMs}ms");
        Log(LogLevel.Information, $"Monitoring {registers.Count} registers");

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

        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                Log(LogLevel.Information, "Connecting to Modbus TCP server...");
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
        _inOutSubject.InputSubject.OnCompleted();
        _inOutSubject.OutputSubject.OnCompleted();

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
            .SetCurrentVersion("1.0.0")
            .SetIcon("settings_input_hdmi")
            .SetOutputs([
                new TypeDefinition("default", "Modbus Data", [SerializationMethods.Msgpack])
            ])
            .SetDefaultOptionValues(new ModbusTcpOptions
            {
                Host = "localhost",
                Port = 502,
                SlaveId = 1,
                PollingIntervalMs = 1000,
                ConnectionTimeoutMs = 5000,
                ReadTimeoutMs = 3000,
                Retries = 3,
                RetryDelayMs = 500,
                ByteOrderStr = "BigEndian",
                Registers = "# Format: name:type:address[:datatype]\n# temperature:holding:0:float32"
            })
            .Build((meta, services) => new ModbusTcpElement(meta, services));

        // Load implementation from external file
        var implementation = UiConfigLoader.GetConfigImplementation();
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
