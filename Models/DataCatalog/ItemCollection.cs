namespace FlowMaker.ModbusTcp.Models.DataCatalog;

/// <summary>
/// Generic wrapper for API responses containing a collection of items
/// </summary>
public class ItemCollection<T>
{
    public required IEnumerable<T> Items { get; set; }
}
