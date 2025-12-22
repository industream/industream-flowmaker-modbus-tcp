using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlowMaker.ModbusTcp.Models.DataCatalog;
using Microsoft.Extensions.Logging;

namespace FlowMaker.ModbusTcp.Services;

/// <summary>
/// Service for communicating with the DataCatalog API
/// </summary>
public class DataCatalogService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<DataCatalogService> _logger;
    private readonly JsonSerializerOptions _jsonOptions;
    private bool _disposed;

    public DataCatalogService(string baseUrl, ILogger<DataCatalogService> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(30)
        };

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    /// <summary>
    /// Gets all source connections filtered by source type
    /// </summary>
    public async Task<IEnumerable<SourceConnection>> GetSourceConnectionsByTypeAsync(
        string sourceTypeName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"source-connections?sourceTypes={Uri.EscapeDataString(sourceTypeName)}";
            _logger.LogDebug("Fetching source connections from: {Url}", url);

            var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<ItemCollection<SourceConnection>>(_jsonOptions, cancellationToken);
            return result?.Items ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch source connections for type {SourceType}", sourceTypeName);
            throw;
        }
    }

    /// <summary>
    /// Gets a specific source connection by ID
    /// </summary>
    public async Task<SourceConnection?> GetSourceConnectionByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"source-connections?ids={id}";
            _logger.LogDebug("Fetching source connection by ID from: {Url}", url);

            var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<ItemCollection<SourceConnection>>(_jsonOptions, cancellationToken);
            return result?.Items?.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch source connection with ID {Id}", id);
            throw;
        }
    }

    /// <summary>
    /// Gets a specific source connection by name
    /// </summary>
    public async Task<SourceConnection?> GetSourceConnectionByNameAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"source-connections?names={Uri.EscapeDataString(name)}";
            _logger.LogDebug("Fetching source connection by name from: {Url}", url);

            var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<ItemCollection<SourceConnection>>(_jsonOptions, cancellationToken);
            return result?.Items?.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch source connection with name {Name}", name);
            throw;
        }
    }

    /// <summary>
    /// Gets all catalog entries (tags) for a specific source connection
    /// </summary>
    public async Task<IEnumerable<CatalogEntry>> GetCatalogEntriesBySourceConnectionAsync(
        Guid sourceConnectionId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Note: The current API filters by sourceTypes, not sourceConnectionId
            // We fetch all entries and filter client-side for now
            var url = $"catalog-entries?sourceTypes=Modbus-TCP";
            _logger.LogDebug("Fetching catalog entries from: {Url}", url);

            var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<ItemCollection<CatalogEntry>>(_jsonOptions, cancellationToken);

            // Filter by source connection ID client-side
            return result?.Items?.Where(e => e.SourceConnection.Id == sourceConnectionId) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch catalog entries for source connection {Id}", sourceConnectionId);
            throw;
        }
    }

    /// <summary>
    /// Gets all catalog entries (tags) filtered by source type
    /// </summary>
    public async Task<IEnumerable<CatalogEntry>> GetCatalogEntriesBySourceTypeAsync(
        string sourceTypeName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"catalog-entries?sourceTypes={Uri.EscapeDataString(sourceTypeName)}";
            _logger.LogDebug("Fetching catalog entries from: {Url}", url);

            var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<ItemCollection<CatalogEntry>>(_jsonOptions, cancellationToken);
            return result?.Items ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch catalog entries for type {SourceType}", sourceTypeName);
            throw;
        }
    }

    /// <summary>
    /// Gets all source types
    /// </summary>
    public async Task<IEnumerable<SourceType>> GetSourceTypesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var url = "source-types";
            _logger.LogDebug("Fetching source types from: {Url}", url);

            var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<ItemCollection<SourceType>>(_jsonOptions, cancellationToken);
            return result?.Items ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch source types");
            throw;
        }
    }

    /// <summary>
    /// Extracts Modbus TCP connection configuration from a SourceConnection
    /// </summary>
    public static ModbusTcpConnectionConfig ExtractModbusTcpConfig(SourceConnection sourceConnection)
    {
        var config = new ModbusTcpConnectionConfig();

        // Check for combined address field first (format: "host:port" or just "host")
        if (!string.IsNullOrEmpty(sourceConnection.Address))
        {
            var addressParts = sourceConnection.Address.Split(':');
            config.Host = addressParts[0];
            if (addressParts.Length > 1 && int.TryParse(addressParts[1], out var addrPort))
                config.Port = addrPort;
        }

        // Use direct fields from SourceConnection (override address if explicitly set)
        if (!string.IsNullOrEmpty(sourceConnection.Host))
            config.Host = sourceConnection.Host;

        if (!string.IsNullOrEmpty(sourceConnection.Port) &&
            int.TryParse(sourceConnection.Port, out var port))
            config.Port = port;

        // SlaveId - check both SlaveId and UnitId (alias)
        if (!string.IsNullOrEmpty(sourceConnection.SlaveId) &&
            byte.TryParse(sourceConnection.SlaveId, out var slaveId))
            config.SlaveId = slaveId;
        else if (!string.IsNullOrEmpty(sourceConnection.UnitId) &&
            byte.TryParse(sourceConnection.UnitId, out var unitId))
            config.SlaveId = unitId;

        if (!string.IsNullOrEmpty(sourceConnection.ConnectionTimeoutMs) &&
            int.TryParse(sourceConnection.ConnectionTimeoutMs, out var connTimeout))
            config.ConnectionTimeoutMs = connTimeout;

        if (!string.IsNullOrEmpty(sourceConnection.ReadTimeoutMs) &&
            int.TryParse(sourceConnection.ReadTimeoutMs, out var readTimeout))
            config.ReadTimeoutMs = readTimeout;

        // Poll Interval (in seconds from DataCatalog, converted to ms)
        if (!string.IsNullOrEmpty(sourceConnection.PollInterval) &&
            int.TryParse(sourceConnection.PollInterval, out var pollIntervalSec))
            config.PollingIntervalMs = pollIntervalSec * 1000;

        // Byte Order
        if (!string.IsNullOrEmpty(sourceConnection.ByteOrder))
            config.ByteOrder = sourceConnection.ByteOrder;

        return config;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _httpClient.Dispose();
            _disposed = true;
        }
    }
}
