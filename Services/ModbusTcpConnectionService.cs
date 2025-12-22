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

    // Periodic status logging
    private Timer? _statusTimer;
    private const int StatusIntervalMs = 30000; // 30 seconds

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

        // Start periodic status logging
        _statusTimer = new Timer(LogPeriodicStatus, null, StatusIntervalMs, StatusIntervalMs);
    }

    /// <summary>
    /// Logs periodic status information every 30 seconds
    /// </summary>
    private void LogPeriodicStatus(object? state)
    {
        try
        {
            var health = Health;
            var lastReadAgo = health.LastSuccessfulRead != DateTime.MinValue
                ? $"{(DateTime.UtcNow - health.LastSuccessfulRead).TotalSeconds:F1}s ago"
                : "never";

            if (health.IsHealthy)
            {
                _logger.LogInformation(
                    "[STATUS] OK | Connected={Connected} | Host={Host}:{Port} | SlaveId={SlaveId} | " +
                    "Reads={Reads} | Errors={Errors} | LastRead={LastRead} | Uptime={Uptime:F0}s",
                    health.IsConnected,
                    _options.Host,
                    _options.Port,
                    _options.SlaveId,
                    health.TotalReadsCompleted,
                    health.TotalErrors,
                    lastReadAgo,
                    health.UptimeSeconds);
            }
            else
            {
                _logger.LogWarning(
                    "[STATUS] DEGRADED | Connected={Connected} | Reconnecting={Reconnecting} | " +
                    "Host={Host}:{Port} | ReconnectAttempts={Attempts} | Errors={Errors} | LastRead={LastRead}",
                    health.IsConnected,
                    health.IsReconnecting,
                    _options.Host,
                    _options.Port,
                    health.ReconnectAttempts,
                    health.TotalErrors,
                    lastReadAgo);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error logging periodic status");
        }
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
        // Dispose previous connection safely
        try { _modbusMaster?.Dispose(); } catch { /* Ignore */ }
        try { _tcpClient?.Dispose(); } catch { /* Ignore */ }

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

        // Configure TCP keep-alive for detecting dead connections
        _tcpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        _tcpClient.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 30);      // Start keep-alive after 30s idle
        _tcpClient.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 10); // Send keep-alive every 10s
        _tcpClient.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3); // 3 retries before disconnect

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

        _logger.LogInformation("========================================");
        _logger.LogInformation("[CONNECTED] Modbus TCP server at {Host}:{Port}", _options.Host, _options.Port);
        _logger.LogInformation("[CONNECTED] SlaveId={SlaveId}, Registers={Count}, PollingInterval={Interval}ms",
            _options.SlaveId, _registerDefinitions.Count, _options.PollingIntervalMs);
        _logger.LogInformation("========================================");
        SafeInvokeConnectionStatusChanged(true, "Connected");
    }

    /// <summary>
    /// Safely invoke event without breaking caller on exception
    /// </summary>
    private void SafeInvokeConnectionStatusChanged(bool connected, string message)
    {
        try
        {
            OnConnectionStatusChanged?.Invoke(connected, message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error in OnConnectionStatusChanged event handler");
        }
    }

    /// <summary>
    /// Safely invoke data received event
    /// </summary>
    private void SafeInvokeDataReceived(ModbusDataPoint dataPoint)
    {
        try
        {
            OnDataReceived?.Invoke(dataPoint);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error in OnDataReceived event handler");
        }
    }

    /// <summary>
    /// Checks if TCP connection is actually alive (not just Connected property)
    /// </summary>
    private bool IsConnectionAlive()
    {
        if (_tcpClient == null || !_tcpClient.Connected)
            return false;

        try
        {
            // Poll the socket to check if it's still connected
            var socket = _tcpClient.Client;
            return !(socket.Poll(1000, SelectMode.SelectRead) && socket.Available == 0);
        }
        catch
        {
            return false;
        }
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
                if (_disposed)
                {
                    _logger.LogDebug("Service disposed, stopping polling");
                    break;
                }

                // Use IsConnectionAlive() for more reliable connection detection
                if (!IsConnectionAlive())
                {
                    await TriggerReconnectAsync(cancellationToken);
                    continue;
                }

                var dataPoint = await ReadAllRegistersAsync(cancellationToken);

                if (dataPoint.Values.Count > 0)
                {
                    Interlocked.Increment(ref _totalReadsCompleted);
                    _lastSuccessfulRead = DateTime.UtcNow;
                    SafeInvokeDataReceived(dataPoint);
                }

                await Task.Delay(_options.PollingIntervalMs, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                _logger.LogDebug("Service disposed during polling");
                break;
            }
            catch (Exception ex) when (IsConnectionError(ex))
            {
                Interlocked.Increment(ref _totalErrors);
                _logger.LogWarning("Connection error during polling: {Error}", ex.Message);

                if (!cancellationToken.IsCancellationRequested)
                {
                    await TriggerReconnectAsync(cancellationToken);
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _totalErrors);
                _logger.LogError(ex, "Error during register polling");

                if (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(1000, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
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
            Address = register.Address,
            DataType = register.DataType.ToString()
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
                    value.Value = ConvertRegistersToValue(holdingRegisters, register.DataType, register.Quantity, _options.GetByteOrder());
                    break;

                case RegisterType.InputRegister:
                    var inputRegisters = await _modbusMaster.ReadInputRegistersAsync(_options.SlaveId, register.Address, register.Length);
                    value.RawValues = inputRegisters;
                    value.Value = ConvertRegistersToValue(inputRegisters, register.DataType, register.Quantity, _options.GetByteOrder());
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

    /// <summary>
    /// Converts raw register values to the appropriate data type with byte order handling
    /// </summary>
    private static object ConvertRegistersToValue(ushort[] registers, DataType dataType, ushort quantity, ByteOrder byteOrder)
    {
        if (registers == null || registers.Length == 0)
            return 0;

        // If quantity > 1, return an array of values
        if (quantity > 1)
        {
            return ConvertRegistersToArray(registers, dataType, quantity, byteOrder);
        }

        // Single value conversion
        return ConvertSingleValue(registers, dataType, byteOrder);
    }

    /// <summary>
    /// Converts registers to an array of values
    /// </summary>
    private static object ConvertRegistersToArray(ushort[] registers, DataType dataType, ushort quantity, ByteOrder byteOrder)
    {
        int registersPerValue = dataType switch
        {
            DataType.UInt32 or DataType.Int32 or DataType.Float32 => 2,
            DataType.UInt64 or DataType.Int64 or DataType.Float64 => 4,
            _ => 1
        };

        return dataType switch
        {
            DataType.Int16 => ExtractArray(registers, quantity, registersPerValue, byteOrder,
                (regs, bo) => (short)regs[0]),
            DataType.UInt16 => ExtractUInt16Array(registers, quantity),
            DataType.Int32 => ExtractArray(registers, quantity, registersPerValue, byteOrder, ToInt32),
            DataType.UInt32 => ExtractArray(registers, quantity, registersPerValue, byteOrder, ToUInt32),
            DataType.Float32 => ExtractArray(registers, quantity, registersPerValue, byteOrder, ToFloat32),
            DataType.Int64 => ExtractArray(registers, quantity, registersPerValue, byteOrder, ToInt64),
            DataType.UInt64 => ExtractArray(registers, quantity, registersPerValue, byteOrder, ToUInt64),
            DataType.Float64 => ExtractArray(registers, quantity, registersPerValue, byteOrder, ToFloat64),
            DataType.Boolean => registers.Select(r => r != 0).ToArray(),
            DataType.String => RegistersToString(registers),
            _ => registers
        };
    }

    /// <summary>
    /// Extracts an array of UInt16 values (simple case, no conversion needed)
    /// </summary>
    private static ushort[] ExtractUInt16Array(ushort[] registers, ushort quantity)
    {
        var result = new ushort[Math.Min(quantity, registers.Length)];
        Array.Copy(registers, result, result.Length);
        return result;
    }

    /// <summary>
    /// Generic method to extract an array of converted values
    /// </summary>
    private static T[] ExtractArray<T>(ushort[] registers, ushort quantity, int registersPerValue, ByteOrder byteOrder,
        Func<ushort[], ByteOrder, T> converter)
    {
        var result = new List<T>();
        for (int i = 0; i < quantity && (i * registersPerValue) < registers.Length; i++)
        {
            int startIndex = i * registersPerValue;
            int length = Math.Min(registersPerValue, registers.Length - startIndex);
            var slice = new ushort[length];
            Array.Copy(registers, startIndex, slice, 0, length);

            // Reorder the slice based on byte order
            var orderedSlice = ReorderRegisters(slice, byteOrder);
            result.Add(converter(orderedSlice, byteOrder));
        }
        return result.ToArray();
    }

    /// <summary>
    /// Converts a single value from registers
    /// </summary>
    private static object ConvertSingleValue(ushort[] registers, DataType dataType, ByteOrder byteOrder)
    {
        // Reorder registers based on byte order
        var orderedRegisters = ReorderRegisters(registers, byteOrder);

        return dataType switch
        {
            DataType.Int16 => (short)orderedRegisters[0],
            DataType.UInt16 => orderedRegisters[0],
            DataType.Int32 when orderedRegisters.Length >= 2 => ToInt32(orderedRegisters, byteOrder),
            DataType.UInt32 when orderedRegisters.Length >= 2 => ToUInt32(orderedRegisters, byteOrder),
            DataType.Float32 when orderedRegisters.Length >= 2 => ToFloat32(orderedRegisters, byteOrder),
            DataType.Int64 when orderedRegisters.Length >= 4 => ToInt64(orderedRegisters, byteOrder),
            DataType.UInt64 when orderedRegisters.Length >= 4 => ToUInt64(orderedRegisters, byteOrder),
            DataType.Float64 when orderedRegisters.Length >= 4 => ToFloat64(orderedRegisters, byteOrder),
            DataType.Boolean => orderedRegisters[0] != 0,
            DataType.String => RegistersToString(orderedRegisters),
            _ => orderedRegisters.Length == 1 ? orderedRegisters[0] : orderedRegisters
        };
    }

    /// <summary>
    /// Reorders registers based on byte order for word-level ordering
    /// </summary>
    private static ushort[] ReorderRegisters(ushort[] registers, ByteOrder byteOrder)
    {
        if (registers.Length <= 1)
            return registers;

        var result = new ushort[registers.Length];

        switch (byteOrder)
        {
            case ByteOrder.BigEndian: // ABCD - standard, no reordering needed
                Array.Copy(registers, result, registers.Length);
                break;

            case ByteOrder.LittleEndian: // DCBA - reverse word order
                for (int i = 0; i < registers.Length; i++)
                    result[i] = registers[registers.Length - 1 - i];
                break;

            case ByteOrder.BigEndianByteSwap: // BADC - swap bytes within each word
                for (int i = 0; i < registers.Length; i++)
                    result[i] = SwapBytes(registers[i]);
                break;

            case ByteOrder.LittleEndianByteSwap: // CDAB - reverse word order and swap bytes
                for (int i = 0; i < registers.Length; i++)
                    result[i] = SwapBytes(registers[registers.Length - 1 - i]);
                break;
        }

        return result;
    }

    private static ushort SwapBytes(ushort value)
    {
        return (ushort)((value >> 8) | (value << 8));
    }

    private static int ToInt32(ushort[] registers, ByteOrder byteOrder)
    {
        var bytes = GetBytesFromRegisters(registers, 2, byteOrder);
        return BitConverter.ToInt32(bytes, 0);
    }

    private static uint ToUInt32(ushort[] registers, ByteOrder byteOrder)
    {
        var bytes = GetBytesFromRegisters(registers, 2, byteOrder);
        return BitConverter.ToUInt32(bytes, 0);
    }

    private static float ToFloat32(ushort[] registers, ByteOrder byteOrder)
    {
        var bytes = GetBytesFromRegisters(registers, 2, byteOrder);
        return BitConverter.ToSingle(bytes, 0);
    }

    private static long ToInt64(ushort[] registers, ByteOrder byteOrder)
    {
        var bytes = GetBytesFromRegisters(registers, 4, byteOrder);
        return BitConverter.ToInt64(bytes, 0);
    }

    private static ulong ToUInt64(ushort[] registers, ByteOrder byteOrder)
    {
        var bytes = GetBytesFromRegisters(registers, 4, byteOrder);
        return BitConverter.ToUInt64(bytes, 0);
    }

    private static double ToFloat64(ushort[] registers, ByteOrder byteOrder)
    {
        var bytes = GetBytesFromRegisters(registers, 4, byteOrder);
        return BitConverter.ToDouble(bytes, 0);
    }

    /// <summary>
    /// Converts registers to bytes with proper byte order handling
    /// </summary>
    private static byte[] GetBytesFromRegisters(ushort[] registers, int count, ByteOrder byteOrder)
    {
        var bytes = new byte[count * 2];

        for (int i = 0; i < count && i < registers.Length; i++)
        {
            var regBytes = BitConverter.GetBytes(registers[i]);

            // Handle byte order within registers
            switch (byteOrder)
            {
                case ByteOrder.BigEndian:
                case ByteOrder.LittleEndian:
                    // Big-endian byte order within register
                    if (BitConverter.IsLittleEndian)
                    {
                        bytes[i * 2] = regBytes[1];
                        bytes[i * 2 + 1] = regBytes[0];
                    }
                    else
                    {
                        bytes[i * 2] = regBytes[0];
                        bytes[i * 2 + 1] = regBytes[1];
                    }
                    break;

                case ByteOrder.BigEndianByteSwap:
                case ByteOrder.LittleEndianByteSwap:
                    // Little-endian byte order within register (swapped)
                    if (BitConverter.IsLittleEndian)
                    {
                        bytes[i * 2] = regBytes[0];
                        bytes[i * 2 + 1] = regBytes[1];
                    }
                    else
                    {
                        bytes[i * 2] = regBytes[1];
                        bytes[i * 2 + 1] = regBytes[0];
                    }
                    break;
            }
        }

        // BitConverter expects little-endian on most systems
        if (!BitConverter.IsLittleEndian)
            Array.Reverse(bytes);

        return bytes;
    }

    private static string RegistersToString(ushort[] registers)
    {
        var bytes = new byte[registers.Length * 2];
        for (int i = 0; i < registers.Length; i++)
        {
            bytes[i * 2] = (byte)(registers[i] >> 8);
            bytes[i * 2 + 1] = (byte)(registers[i] & 0xFF);
        }
        return System.Text.Encoding.ASCII.GetString(bytes).TrimEnd('\0');
    }

    private async Task TriggerReconnectAsync(CancellationToken cancellationToken)
    {
        if (_disposed) return;

        if (!await _reconnectLock.WaitAsync(0, cancellationToken))
        {
            return; // Another reconnection is in progress
        }

        try
        {
            _isReconnecting = true;
            _logger.LogWarning("========================================");
            _logger.LogWarning("[DISCONNECTED] Lost connection to {Host}:{Port}", _options.Host, _options.Port);
            _logger.LogWarning("========================================");
            SafeInvokeConnectionStatusChanged(false, "Reconnecting...");

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

        _logger.LogInformation("========================================");
        _logger.LogInformation("[SHUTDOWN] Disposing ModbusTcpConnectionService...");

        // Stop status timer
        try { _statusTimer?.Dispose(); } catch { /* Ignore */ }

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

        _logger.LogInformation("[SHUTDOWN] Final stats - Reads: {Reads}, Errors: {Errors}, Uptime: {Uptime:F0}s",
            _totalReadsCompleted, _totalErrors, _uptimeStopwatch.Elapsed.TotalSeconds);
        _logger.LogInformation("========================================");
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
