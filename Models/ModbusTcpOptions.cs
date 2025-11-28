namespace FlowMaker.ModbusTcp.Models;

/// <summary>
/// Configuration options for the Modbus TCP Flow Box
/// Production-ready with reliability settings
/// </summary>
public class ModbusTcpOptions
{
    private string _host = "localhost";
    private string _registers = "";

    /// <summary>
    /// Modbus TCP server hostname or IP address
    /// </summary>
    public string Host
    {
        get => _host;
        set => _host = string.IsNullOrWhiteSpace(value) ? "localhost" : value;
    }

    /// <summary>
    /// Modbus TCP server port (default: 502)
    /// </summary>
    public int Port { get; set; } = 502;

    /// <summary>
    /// Modbus slave/unit ID (1-247)
    /// </summary>
    public byte SlaveId { get; set; } = 1;

    /// <summary>
    /// Polling interval in milliseconds
    /// </summary>
    public int PollingIntervalMs { get; set; } = 1000;

    /// <summary>
    /// Connection timeout in milliseconds
    /// </summary>
    public int ConnectionTimeoutMs { get; set; } = 5000;

    /// <summary>
    /// Read timeout in milliseconds
    /// </summary>
    public int ReadTimeoutMs { get; set; } = 3000;

    /// <summary>
    /// Number of retries on failure
    /// </summary>
    public int Retries { get; set; } = 3;

    /// <summary>
    /// Delay between retries in milliseconds
    /// </summary>
    public int RetryDelayMs { get; set; } = 500;

    /// <summary>
    /// Register definitions (one per line)
    /// Format: name:type:address[:length]
    /// Types: coil, discrete, holding, input
    /// </summary>
    public string Registers
    {
        get => _registers;
        set => _registers = value ?? "";
    }

    /// <summary>
    /// Parse register definitions from string
    /// </summary>
    public List<RegisterDefinition> GetRegisterDefinitions()
    {
        var result = new List<RegisterDefinition>();

        if (string.IsNullOrWhiteSpace(_registers))
            return result;

        var lines = _registers.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                continue;

            var parts = line.Split(':');
            if (parts.Length < 3)
                continue;

            var name = parts[0].Trim();
            var typeStr = parts[1].Trim().ToLowerInvariant();

            if (!ushort.TryParse(parts[2].Trim(), out var address))
                continue;

            ushort length = 1;
            if (parts.Length >= 4 && ushort.TryParse(parts[3].Trim(), out var parsedLength))
                length = parsedLength;

            var regType = typeStr switch
            {
                "coil" or "coils" => RegisterType.Coil,
                "discrete" or "discreteinput" or "discrete_input" => RegisterType.DiscreteInput,
                "holding" or "holdingregister" or "holding_register" => RegisterType.HoldingRegister,
                "input" or "inputregister" or "input_register" => RegisterType.InputRegister,
                _ => RegisterType.HoldingRegister
            };

            result.Add(new RegisterDefinition
            {
                Name = name,
                Type = regType,
                Address = address,
                Length = length
            });
        }

        return result;
    }
}

/// <summary>
/// Types of Modbus registers
/// </summary>
public enum RegisterType
{
    /// <summary>Coils (read/write bits) - Function codes 1, 5, 15</summary>
    Coil,
    /// <summary>Discrete inputs (read-only bits) - Function code 2</summary>
    DiscreteInput,
    /// <summary>Holding registers (read/write 16-bit) - Function codes 3, 6, 16</summary>
    HoldingRegister,
    /// <summary>Input registers (read-only 16-bit) - Function code 4</summary>
    InputRegister
}

/// <summary>
/// Definition of a Modbus register to read
/// </summary>
public class RegisterDefinition
{
    public string Name { get; set; } = "";
    public RegisterType Type { get; set; }
    public ushort Address { get; set; }
    public ushort Length { get; set; } = 1;
}
