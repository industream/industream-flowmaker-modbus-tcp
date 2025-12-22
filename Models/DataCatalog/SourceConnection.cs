using System.Text.Json.Serialization;
using FlowMaker.ModbusTcp.Converters;

namespace FlowMaker.ModbusTcp.Models.DataCatalog;

/// <summary>
/// Represents a source connection from the DataCatalog API
/// </summary>
public class SourceConnection
{
    public required Guid Id { get; set; }
    public required string Name { get; set; }
    public required SourceType SourceType { get; set; }

    // Modbus TCP specific fields (from DataCatalog)
    // Using FlexibleStringConverter to handle both string and numeric values from API
    public string? Address { get; set; }  // Combined host:port format
    public string? Host { get; set; }

    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Port { get; set; }

    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? SlaveId { get; set; }

    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? UnitId { get; set; }  // Alias for SlaveId

    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? ConnectionTimeoutMs { get; set; }

    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? ReadTimeoutMs { get; set; }

    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? PollInterval { get; set; }  // Poll interval in seconds

    public string? ByteOrder { get; set; }  // BigEndian, LittleEndian, etc.

    /// <summary>
    /// Extension data for any additional fields
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, object>? ExtensionData { get; set; }
}

/// <summary>
/// Modbus TCP specific connection configuration extracted from SourceConnection
/// </summary>
public class ModbusTcpConnectionConfig
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 502;
    public byte SlaveId { get; set; } = 1;
    public int ConnectionTimeoutMs { get; set; } = 5000;
    public int ReadTimeoutMs { get; set; } = 3000;
    public int PollingIntervalMs { get; set; } = 1000;
    public string ByteOrder { get; set; } = "BigEndian";
}
