using MessagePack;

namespace FlowMaker.ModbusTcp.Models;

/// <summary>
/// Data point containing Modbus register values
/// </summary>
[MessagePackObject]
public class ModbusDataPoint
{
    [Key("timestamp")]
    public DateTime Timestamp { get; set; }

    [Key("host")]
    public string Host { get; set; } = "";

    [Key("slaveId")]
    public byte SlaveId { get; set; }

    [Key("values")]
    public Dictionary<string, ModbusValue> Values { get; set; } = new();
}

/// <summary>
/// Individual Modbus register value
/// </summary>
[MessagePackObject]
public class ModbusValue
{
    [Key("name")]
    public string Name { get; set; } = "";

    [Key("type")]
    public string Type { get; set; } = "";

    [Key("dataType")]
    public string DataType { get; set; } = "UInt16";

    [Key("address")]
    public ushort Address { get; set; }

    [Key("value")]
    public object? Value { get; set; }

    [Key("rawValues")]
    public ushort[]? RawValues { get; set; }

    [Key("quality")]
    public string Quality { get; set; } = "Good";

    [Key("error")]
    public string? Error { get; set; }
}
