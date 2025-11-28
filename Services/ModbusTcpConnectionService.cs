using FlowMaker.ModbusTcp.Models;
using Microsoft.Extensions.Logging;
using NModbus;
using System.Diagnostics;
using System.Net.Sockets;

namespace FlowMaker.ModbusTcp.Services;

/// <summary>
/// Service for managing Modbus TCP connections and reading registers
/// Production-ready with automatic reconnection, health monitoring, and proper error handling
/// </summary>
public class ModbusTcpConnectionService : IDisposable
{
    private readonly ModbusTcpOptions _options;
    private readonly ILogger<ModbusTcpConnectionService> _logger;
    private readonly List<RegisterDefinition> _registerDefinitions;
    private TcpClient? _tcpClient;
    private IModbusMaster? _modbusMaster;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly SemaphoreSlim _reconnectLock = new(1, 1);
    private bool _disposed;
    private CancellationTokenSource? _pollingCts;
    private Task? _pollingTask;

    // Reconnection settings
    private const int InitialReconnectDelayMs = 1000;
    private const int MaxReconnectDelayMs = 60000;
    private int _currentReconnectDelay = InitialReconnectDelayMs;
    private int _reconnectAttempts = 0;
    private bool _isReconnecting = false;

    // Health metrics
    private DateTime _lastSuccessfulRead = DateTime.MinValue;
    private DateTime _lastConnectionTime = DateTime.MinValue;
    private long _totalReadsCompleted = 0;
    private long _totalErrors = 0;
    private readonly Stopwatch _uptimeStopwatch = new();

    /// <summary>
    /// Event raised when data is read from registers
    /// </summary>
    public event Action<ModbusDataPoint>? OnDataReceived;

    /// <summary>
    /// Event raised when connection status changes
    /// </summary>
    public event Action<bool, string>? OnConnectionStatusChanged;

    /// <summary>
    /// Gets the current health status
    /// </summary>
    public HealthStatus Health => new()
    {
        IsConnected = _tcpClient?.Connected == true,
        LastSuccessfulRead = _lastSuccessfulRead,
        LastConnectionTime = _lastConnectionTime,
        TotalReadsCompleted = _totalReadsCompleted,
        TotalErrors = _totalErrors,
        UptimeSeconds = _uptimeStopwatch.Elapsed.TotalSeconds,
        ReconnectAttempts = _reconnectAttempts,
        IsReconnecting = _isReconnecting
    };

    public ModbusTcpConnectionService(ModbusTcpOptions options, ILogger<ModbusTcpConnectionService> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        ValidateOptions(options);
        _registerDefinitions = options.GetRegisterDefinitions();
        _uptimeStopwatch.Start();

        _logger.LogInformation("ModbusTcpConnectionService initialized for {Host}:{Port}, SlaveId={SlaveId}, Registers={Count}",
            options.Host, options.Port, options.SlaveId, _registerDefinitions.Count);
    }

    private void ValidateOptions(ModbusTcpOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Host))
            throw new ArgumentException("Host is required", nameof(options));

        if (options.Port < 1 || options.Port > 65535)
            throw new ArgumentException("Port must be between 1 and 65535", nameof(options));

        if (options.SlaveId < 1 || options.SlaveId > 247)
            throw new ArgumentException("SlaveId must be between 1 and 247", nameof(options));

        if (options.PollingIntervalMs < 100)
            throw new ArgumentException("PollingIntervalMs must be >= 100", nameof(options));

        if (options.ConnectionTimeoutMs < 1000)
            throw new ArgumentException("ConnectionTimeoutMs must be >= 1000", nameof(options));

        if (options.ReadTimeoutMs < 500)
            throw new ArgumentException("ReadTimeoutMs must be >= 500", nameof(options));
    }

    /// <summary>
    /// Connects to the Modbus TCP server
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            if (_tcpClient?.Connected == true)
            {
                _logger.LogDebug("Already connected to Modbus server");
                return;
            }

            await ConnectInternalAsync(cancellationToken);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private async Task ConnectInternalAsync(CancellationToken cancellationToken)
    {
        _tcpClient?.Dispose();
        _tcpClient = new TcpClient();

        _logger.LogInformation("Connecting to Modbus TCP server at {Host}:{Port}...", _options.Host, _options.Port);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.ConnectionTimeoutMs);

        try
        {
            await _tcpClient.ConnectAsync(_options.Host, _options.Port, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Connection to {_options.Host}:{_options.Port} timed out after {_options.ConnectionTimeoutMs}ms");
        }

        _tcpClient.ReceiveTimeout = _options.ReadTimeoutMs;
        _tcpClient.SendTimeout = _options.ReadTimeoutMs;

        var factory = new ModbusFactory();
        _modbusMaster = factory.CreateMaster(_tcpClient);
        _modbusMaster.Transport.ReadTimeout = _options.ReadTimeoutMs;
        _modbusMaster.Transport.WriteTimeout = _options.ReadTimeoutMs;
        _modbusMaster.Transport.Retries = _options.Retries;

        _lastConnectionTime = DateTime.UtcNow;
        _currentReconnectDelay = InitialReconnectDelayMs;
        _reconnectAttempts = 0;

        _logger.LogInformation("Connected to Modbus TCP server at {Host}:{Port}", _options.Host, _options.Port);
        OnConnectionStatusChanged?.Invoke(true, "Connected");
    }

    /// <summary>
    /// Starts polling registers
    /// </summary>
    public void StartPolling(CancellationToken cancellationToken)
    {
        _pollingCts?.Cancel();
        _pollingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pollingTask = PollRegistersAsync(_pollingCts.Token);
    }

    /// <summary>
    /// Stops polling registers
    /// </summary>
    public void StopPolling()
    {
        _pollingCts?.Cancel();
    }

    private async Task PollRegistersAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting register polling with interval {Interval}ms", _options.PollingIntervalMs);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_tcpClient?.Connected != true)
                {
                    await TriggerReconnectAsync(cancellationToken);
                    continue;
                }

                var dataPoint = await ReadAllRegistersAsync(cancellationToken);

                if (dataPoint.Values.Count > 0)
                {
                    Interlocked.Increment(ref _totalReadsCompleted);
                    _lastSuccessfulRead = DateTime.UtcNow;
                    OnDataReceived?.Invoke(dataPoint);
                }

                await Task.Delay(_options.PollingIntervalMs, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (IsConnectionError(ex))
            {
                Interlocked.Increment(ref _totalErrors);
                _logger.LogWarning("Connection error during polling: {Error}", ex.Message);
                await TriggerReconnectAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _totalErrors);
                _logger.LogError(ex, "Error during register polling");
                await Task.Delay(1000, cancellationToken);
            }
        }

        _logger.LogInformation("Register polling stopped");
    }

    private async Task<ModbusDataPoint> ReadAllRegistersAsync(CancellationToken cancellationToken)
    {
        var dataPoint = new ModbusDataPoint
        {
            Timestamp = DateTime.UtcNow,
            Host = _options.Host,
            SlaveId = _options.SlaveId
        };

        foreach (var register in _registerDefinitions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var value = await ReadRegisterAsync(register, cancellationToken);
            dataPoint.Values[register.Name] = value;
        }

        return dataPoint;
    }

    private async Task<ModbusValue> ReadRegisterAsync(RegisterDefinition register, CancellationToken cancellationToken)
    {
        var value = new ModbusValue
        {
            Name = register.Name,
            Type = register.Type.ToString(),
            Address = register.Address
        };

        if (_modbusMaster == null)
        {
            value.Quality = "Bad";
            value.Error = "Not connected";
            return value;
        }

        try
        {
            switch (register.Type)
            {
                case RegisterType.Coil:
                    var coils = await _modbusMaster.ReadCoilsAsync(_options.SlaveId, register.Address, register.Length);
                    value.Value = register.Length == 1 ? coils[0] : coils;
                    break;

                case RegisterType.DiscreteInput:
                    var discreteInputs = await _modbusMaster.ReadInputsAsync(_options.SlaveId, register.Address, register.Length);
                    value.Value = register.Length == 1 ? discreteInputs[0] : discreteInputs;
                    break;

                case RegisterType.HoldingRegister:
                    var holdingRegisters = await _modbusMaster.ReadHoldingRegistersAsync(_options.SlaveId, register.Address, register.Length);
                    value.RawValues = holdingRegisters;
                    value.Value = register.Length == 1 ? holdingRegisters[0] : holdingRegisters;
                    break;

                case RegisterType.InputRegister:
                    var inputRegisters = await _modbusMaster.ReadInputRegistersAsync(_options.SlaveId, register.Address, register.Length);
                    value.RawValues = inputRegisters;
                    value.Value = register.Length == 1 ? inputRegisters[0] : inputRegisters;
                    break;
            }

            value.Quality = "Good";
        }
        catch (Exception ex)
        {
            value.Quality = "Bad";
            value.Error = ex.Message;
            _logger.LogWarning("Error reading register {Name} at address {Address}: {Error}",
                register.Name, register.Address, ex.Message);
        }

        return value;
    }

    private async Task TriggerReconnectAsync(CancellationToken cancellationToken)
    {
        if (!await _reconnectLock.WaitAsync(0, cancellationToken))
        {
            return; // Another reconnection is in progress
        }

        try
        {
            _isReconnecting = true;
            OnConnectionStatusChanged?.Invoke(false, "Reconnecting...");

            while (!cancellationToken.IsCancellationRequested)
            {
                _reconnectAttempts++;
                _logger.LogInformation("Reconnection attempt {Attempt} in {Delay}ms...",
                    _reconnectAttempts, _currentReconnectDelay);

                try
                {
                    await Task.Delay(_currentReconnectDelay, cancellationToken);
                    await ConnectInternalAsync(cancellationToken);

                    _isReconnecting = false;
                    _logger.LogInformation("Reconnected successfully after {Attempts} attempts", _reconnectAttempts);
                    return;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _totalErrors);
                    _logger.LogWarning("Reconnection attempt {Attempt} failed: {Error}",
                        _reconnectAttempts, ex.Message);

                    // Exponential backoff
                    _currentReconnectDelay = Math.Min(_currentReconnectDelay * 2, MaxReconnectDelayMs);
                }
            }
        }
        finally
        {
            _isReconnecting = false;
            _reconnectLock.Release();
        }
    }

    private static bool IsConnectionError(Exception ex)
    {
        return ex is SocketException ||
               ex is IOException ||
               ex is TimeoutException ||
               ex is ObjectDisposedException ||
               (ex.InnerException != null && IsConnectionError(ex.InnerException));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _logger.LogInformation("Disposing ModbusTcpConnectionService...");

        _pollingCts?.Cancel();

        try
        {
            _pollingTask?.Wait(TimeSpan.FromSeconds(5));
        }
        catch { /* Ignore */ }

        try
        {
            _modbusMaster?.Dispose();
        }
        catch { /* Ignore */ }

        try
        {
            _tcpClient?.Dispose();
        }
        catch { /* Ignore */ }

        _connectionLock.Dispose();
        _reconnectLock.Dispose();
        _pollingCts?.Dispose();

        _logger.LogInformation("ModbusTcpConnectionService disposed. Total reads: {Reads}, Total errors: {Errors}",
            _totalReadsCompleted, _totalErrors);
    }
}

/// <summary>
/// Health status for monitoring
/// </summary>
public class HealthStatus
{
    public bool IsConnected { get; init; }
    public DateTime LastSuccessfulRead { get; init; }
    public DateTime LastConnectionTime { get; init; }
    public long TotalReadsCompleted { get; init; }
    public long TotalErrors { get; init; }
    public double UptimeSeconds { get; init; }
    public int ReconnectAttempts { get; init; }
    public bool IsReconnecting { get; init; }

    public bool IsHealthy => IsConnected && !IsReconnecting;
}
