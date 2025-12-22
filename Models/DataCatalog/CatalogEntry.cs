namespace FlowMaker.ModbusTcp.Models.DataCatalog;

/// <summary>
/// Represents a catalog entry (tag/register) from the DataCatalog API
/// </summary>
public class CatalogEntry
{
    public required Guid Id { get; set; }
    public required string Name { get; set; }
    public required string DataType { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }

    /// <summary>
    /// Source-specific parameters. For Modbus TCP, contains register type, address, etc.
    /// </summary>
    public required Dictionary<string, object> SourceParams { get; set; }

    public required SourceConnection SourceConnection { get; set; }
    public List<Label>? Labels { get; set; }

    /// <summary>
    /// Gets the Modbus register type from SourceParams
    /// </summary>
    public string? GetRegisterType()
    {
        if (SourceParams.TryGetValue("registerType", out var regType))
        {
            return regType?.ToString();
        }
        if (SourceParams.TryGetValue("RegisterType", out var regTypePascal))
        {
            return regTypePascal?.ToString();
        }
        return null;
    }

    /// <summary>
    /// Gets the Modbus register address from SourceParams
    /// </summary>
    public ushort? GetAddress()
    {
        object? addressValue = null;
        if (SourceParams.TryGetValue("address", out var addr))
        {
            addressValue = addr;
        }
        else if (SourceParams.TryGetValue("Address", out var addrPascal))
        {
            addressValue = addrPascal;
        }

        if (addressValue != null && ushort.TryParse(addressValue.ToString(), out var address))
        {
            return address;
        }
        return null;
    }
}

/// <summary>
/// Represents a label from the DataCatalog API
/// </summary>
public class Label
{
    public required Guid Id { get; set; }
    public required string Name { get; set; }
    public string? Color { get; set; }
}
