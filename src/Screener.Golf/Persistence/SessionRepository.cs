using Dapper;
using Microsoft.Extensions.Logging;
using Screener.Core.Persistence;
using Screener.Golf.Models;

namespace Screener.Golf.Persistence;

/// <summary>
/// Persistence for golf sessions and swing sequences.
/// </summary>
public class SessionRepository
{
    private readonly DatabaseContext _db;
    private readonly ILogger<SessionRepository> _logger;

    public SessionRepository(DatabaseContext db, ILogger<SessionRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task SaveSessionAsync(GolfSessionInfo session)
    {
        await _db.ExecuteAsync(
            "INSERT OR REPLACE INTO golf_sessions (id, golfer_id, started_at, ended_at, source1_recording_path, source2_recording_path, total_swings, created_at) " +
            "VALUES (@Id, @GolferId, @StartedAt, @EndedAt, @Source1RecordingPath, @Source2RecordingPath, @TotalSwings, @CreatedAt)",
            new
            {
                session.Id,
                GolferId = session.GolferId ?? "unknown",
                StartedAt = session.StartedAt.ToString("O"),
                EndedAt = session.EndedAt?.ToString("O"),
                session.Source1RecordingPath,
                session.Source2RecordingPath,
                session.TotalSwings,
                CreatedAt = DateTimeOffset.UtcNow.ToString("O")
            });

        _logger.LogInformation("Saved golf session: {Id}", session.Id);
    }

    public async Task SaveSwingSequenceAsync(SwingSequence sequence, string golferId)
    {
        await _db.ExecuteAsync(
            "INSERT OR REPLACE INTO swing_sequences (id, session_id, golfer_id, sequence_number, in_point_ticks, out_point_ticks, detection_method, exported_clip_path, export_status, created_at) " +
            "VALUES (@Id, @SessionId, @GolferId, @SequenceNumber, @InPointTicks, @OutPointTicks, @DetectionMethod, @ExportedClipPath, @ExportStatus, @CreatedAt)",
            new
            {
                sequence.Id,
                sequence.SessionId,
                GolferId = golferId,
                sequence.SequenceNumber,
                sequence.InPointTicks,
                sequence.OutPointTicks,
                sequence.DetectionMethod,
                sequence.ExportedClipPath,
                sequence.ExportStatus,
                CreatedAt = sequence.CreatedAt.ToString("O")
            });
    }

    public async Task<IEnumerable<GolfSessionInfo>> GetRecentSessionsAsync(int limit = 20)
    {
        return await _db.QueryAsync<GolfSessionInfo>(
            "SELECT id, golfer_id AS GolferId, started_at AS StartedAt, ended_at AS EndedAt, " +
            "source1_recording_path AS Source1RecordingPath, source2_recording_path AS Source2RecordingPath, " +
            "total_swings AS TotalSwings FROM golf_sessions ORDER BY started_at DESC LIMIT @limit",
            new { limit });
    }

    public async Task<IEnumerable<SwingSequence>> GetSequencesForSessionAsync(string sessionId)
    {
        return await _db.QueryAsync<SwingSequence>(
            "SELECT id, session_id AS SessionId, sequence_number AS SequenceNumber, " +
            "in_point_ticks AS InPointTicks, out_point_ticks AS OutPointTicks, " +
            "detection_method AS DetectionMethod, exported_clip_path AS ExportedClipPath, " +
            "export_status AS ExportStatus, created_at AS CreatedAt " +
            "FROM swing_sequences WHERE session_id = @sessionId ORDER BY sequence_number",
            new { sessionId });
    }

    public async Task UpdateExportStatusAsync(string sequenceId, string status, string? clipPath = null)
    {
        await _db.ExecuteAsync(
            "UPDATE swing_sequences SET export_status = @status, exported_clip_path = @clipPath WHERE id = @id",
            new { id = sequenceId, status, clipPath });
    }
}
