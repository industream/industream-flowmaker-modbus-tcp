namespace FlowMaker.ModbusTcp.Models.DataCatalog;

/// <summary>
/// Represents a source type from the DataCatalog API
/// </summary>
public class SourceType
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public bool IsProtected { get; set; }
}
