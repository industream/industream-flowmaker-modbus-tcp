namespace FlowMaker.ModbusTcp.Models;

/// <summary>
/// Byte order for multi-register values (32-bit, 64-bit)
/// </summary>
public enum ByteOrder
{
    /// <summary>Big-Endian (ABCD) - Most significant byte first (Modbus standard)</summary>
    BigEndian,
    /// <summary>Little-Endian (DCBA) - Least significant byte first</summary>
    LittleEndian,
    /// <summary>Big-Endian Byte Swap (BADC) - Big-endian with bytes swapped within words</summary>
    BigEndianByteSwap,
    /// <summary>Little-Endian Byte Swap (CDAB) - Little-endian with bytes swapped within words</summary>
    LittleEndianByteSwap
}

/// <summary>
/// Data type for interpreting register values
/// </summary>
public enum DataType
{
    /// <summary>Raw 16-bit unsigned integer (single register)</summary>
    UInt16,
    /// <summary>16-bit signed integer (single register)</summary>
    Int16,
    /// <summary>32-bit unsigned integer (2 registers)</summary>
    UInt32,
    /// <summary>32-bit signed integer (2 registers)</summary>
    Int32,
    /// <summary>32-bit floating point (2 registers)</summary>
    Float32,
    /// <summary>64-bit unsigned integer (4 registers)</summary>
    UInt64,
    /// <summary>64-bit signed integer (4 registers)</summary>
    Int64,
    /// <summary>64-bit floating point (4 registers)</summary>
    Float64,
    /// <summary>Boolean (for coils/discrete inputs)</summary>
    Boolean,
    /// <summary>ASCII string</summary>
    String
}

/// <summary>
/// Configuration options for the Modbus TCP Flow Box
/// Production-ready with reliability settings
/// </summary>
public class ModbusTcpOptions
{
    private string _host = "localhost";
    private string _registers = "";
    private string _byteOrder = "BigEndian";

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
    /// Byte order for multi-register values
    /// </summary>
    public string ByteOrderStr
    {
        get => _byteOrder;
        set => _byteOrder = string.IsNullOrWhiteSpace(value) ? "BigEndian" : value;
    }

    /// <summary>
    /// Gets the parsed ByteOrder enum value
    /// </summary>
    public ByteOrder GetByteOrder()
    {
        return _byteOrder?.ToLowerInvariant() switch
        {
            "littleendian" or "little-endian" or "dcba" => ByteOrder.LittleEndian,
            "bigendianbyteswap" or "big-endian-byte-swap" or "badc" => ByteOrder.BigEndianByteSwap,
            "littleendianbyteswap" or "little-endian-byte-swap" or "cdab" => ByteOrder.LittleEndianByteSwap,
            _ => ByteOrder.BigEndian
        };
    }

    /// <summary>
    /// Register definitions (one per line)
    /// Format: name:type:address[:datatype]
    /// Types: coil, discrete, holding, input
    /// DataTypes: uint16, int16, uint32, int32, float32, uint64, int64, float64, bool, string
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

            // Parse data type (4th part) or default based on register type
            DataType dataType = DataType.UInt16;
            if (parts.Length >= 4)
            {
                var dataTypeStr = parts[3].Trim().ToLowerInvariant();
                dataType = dataTypeStr switch
                {
                    "int16" or "short" => DataType.Int16,
                    "uint32" or "dword" => DataType.UInt32,
                    "int32" or "long" => DataType.Int32,
                    "float32" or "float" or "real" => DataType.Float32,
                    "uint64" or "qword" => DataType.UInt64,
                    "int64" or "llong" => DataType.Int64,
                    "float64" or "double" or "lreal" => DataType.Float64,
                    "bool" or "boolean" or "bit" => DataType.Boolean,
                    "string" or "str" => DataType.String,
                    _ => DataType.UInt16
                };
            }

            var regType = typeStr switch
            {
                "coil" or "coils" => RegisterType.Coil,
                "discrete" or "discreteinput" or "discrete_input" => RegisterType.DiscreteInput,
                "holding" or "holdingregister" or "holding_register" => RegisterType.HoldingRegister,
                "input" or "inputregister" or "input_register" => RegisterType.InputRegister,
                _ => RegisterType.HoldingRegister
            };

            // Auto-set data type for coils/discrete if not specified
            if ((regType == RegisterType.Coil || regType == RegisterType.DiscreteInput) && parts.Length < 4)
            {
                dataType = DataType.Boolean;
            }

            // Calculate required register count based on data type
            ushort length = dataType switch
            {
                DataType.UInt32 or DataType.Int32 or DataType.Float32 => 2,
                DataType.UInt64 or DataType.Int64 or DataType.Float64 => 4,
                _ => 1
            };

            result.Add(new RegisterDefinition
            {
                Name = name,
                Type = regType,
                Address = address,
                Length = length,
                DataType = dataType
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
    public DataType DataType { get; set; } = DataType.UInt16;
}
