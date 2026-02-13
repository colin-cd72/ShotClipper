using Dapper;
using Microsoft.Extensions.Logging;
using Screener.Core.Persistence;
using Screener.Golf.Models;

namespace Screener.Golf.Persistence;

/// <summary>
/// CRUD operations for golfer profiles via Dapper.
/// </summary>
public class GolferRepository
{
    private readonly DatabaseContext _db;
    private readonly ILogger<GolferRepository> _logger;

    public GolferRepository(DatabaseContext db, ILogger<GolferRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<IEnumerable<GolferProfile>> GetAllAsync()
    {
        return await _db.QueryAsync<GolferProfile>(
            "SELECT id, first_name AS FirstName, last_name AS LastName, display_name AS DisplayName, " +
            "handicap AS Handicap, photo_path AS PhotoPath, is_active AS IsActive, " +
            "created_at AS CreatedAt, updated_at AS UpdatedAt FROM golfers WHERE is_active = 1 ORDER BY last_name, first_name");
    }

    public async Task<GolferProfile?> GetByIdAsync(string id)
    {
        return await _db.QuerySingleOrDefaultAsync<GolferProfile>(
            "SELECT id, first_name AS FirstName, last_name AS LastName, display_name AS DisplayName, " +
            "handicap AS Handicap, photo_path AS PhotoPath, is_active AS IsActive, " +
            "created_at AS CreatedAt, updated_at AS UpdatedAt FROM golfers WHERE id = @id",
            new { id });
    }

    public async Task CreateAsync(GolferProfile golfer)
    {
        await _db.ExecuteAsync(
            "INSERT INTO golfers (id, first_name, last_name, display_name, handicap, photo_path, is_active, created_at, updated_at) " +
            "VALUES (@Id, @FirstName, @LastName, @DisplayName, @Handicap, @PhotoPath, @IsActive, @CreatedAt, @UpdatedAt)",
            new
            {
                golfer.Id,
                golfer.FirstName,
                golfer.LastName,
                golfer.DisplayName,
                golfer.Handicap,
                golfer.PhotoPath,
                IsActive = golfer.IsActive ? 1 : 0,
                CreatedAt = golfer.CreatedAt.ToString("O"),
                UpdatedAt = golfer.UpdatedAt.ToString("O")
            });

        _logger.LogInformation("Created golfer: {Name} ({Id})", golfer.EffectiveDisplayName, golfer.Id);
    }

    public async Task UpdateAsync(GolferProfile golfer)
    {
        golfer.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.ExecuteAsync(
            "UPDATE golfers SET first_name = @FirstName, last_name = @LastName, display_name = @DisplayName, " +
            "handicap = @Handicap, photo_path = @PhotoPath, is_active = @IsActive, updated_at = @UpdatedAt WHERE id = @Id",
            new
            {
                golfer.Id,
                golfer.FirstName,
                golfer.LastName,
                golfer.DisplayName,
                golfer.Handicap,
                golfer.PhotoPath,
                IsActive = golfer.IsActive ? 1 : 0,
                UpdatedAt = golfer.UpdatedAt.ToString("O")
            });

        _logger.LogInformation("Updated golfer: {Name} ({Id})", golfer.EffectiveDisplayName, golfer.Id);
    }

    public async Task DeleteAsync(string id)
    {
        // Soft delete
        await _db.ExecuteAsync(
            "UPDATE golfers SET is_active = 0, updated_at = @now WHERE id = @id",
            new { id, now = DateTimeOffset.UtcNow.ToString("O") });

        _logger.LogInformation("Deleted golfer: {Id}", id);
    }
}
