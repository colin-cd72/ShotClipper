using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Screener.Core.Persistence;
using Screener.Golf.Overlays;

namespace Screener.Golf.Persistence;

/// <summary>
/// Persistence for overlay configurations (logo bug, lower third).
/// </summary>
public class OverlayRepository
{
    private readonly DatabaseContext _db;
    private readonly ILogger<OverlayRepository> _logger;

    public OverlayRepository(DatabaseContext db, ILogger<OverlayRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<IEnumerable<OverlayConfigRecord>> GetAllAsync()
    {
        return await _db.QueryAsync<OverlayConfigRecord>(
            "SELECT id, name, type, is_default AS IsDefault, config_json AS ConfigJson, " +
            "asset_path AS AssetPath, created_at AS CreatedAt, updated_at AS UpdatedAt " +
            "FROM overlay_configs ORDER BY type, name");
    }

    public async Task<OverlayConfigRecord?> GetDefaultAsync(string type)
    {
        return await _db.QuerySingleOrDefaultAsync<OverlayConfigRecord>(
            "SELECT id, name, type, is_default AS IsDefault, config_json AS ConfigJson, " +
            "asset_path AS AssetPath, created_at AS CreatedAt, updated_at AS UpdatedAt " +
            "FROM overlay_configs WHERE type = @type AND is_default = 1",
            new { type });
    }

    public async Task SaveAsync(OverlayConfigRecord record)
    {
        await _db.ExecuteAsync(
            "INSERT OR REPLACE INTO overlay_configs (id, name, type, is_default, config_json, asset_path, created_at, updated_at) " +
            "VALUES (@Id, @Name, @Type, @IsDefault, @ConfigJson, @AssetPath, @CreatedAt, @UpdatedAt)",
            new
            {
                record.Id,
                record.Name,
                record.Type,
                IsDefault = record.IsDefault ? 1 : 0,
                record.ConfigJson,
                record.AssetPath,
                CreatedAt = record.CreatedAt.ToString("O"),
                UpdatedAt = record.UpdatedAt.ToString("O")
            });

        _logger.LogInformation("Saved overlay config: {Name} ({Type})", record.Name, record.Type);
    }

    public async Task DeleteAsync(string id)
    {
        await _db.ExecuteAsync("DELETE FROM overlay_configs WHERE id = @id", new { id });
    }
}

/// <summary>
/// Database record for overlay configurations.
/// </summary>
public class OverlayConfigRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty; // "logo_bug" or "lower_third"
    public bool IsDefault { get; set; }
    public string ConfigJson { get; set; } = "{}";
    public string? AssetPath { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public T? DeserializeConfig<T>() where T : class
    {
        return JsonSerializer.Deserialize<T>(ConfigJson);
    }

    public void SerializeConfig<T>(T config)
    {
        ConfigJson = JsonSerializer.Serialize(config);
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
