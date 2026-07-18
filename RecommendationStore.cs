using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace ProcLens;

/// <summary>
/// Persists recommendations separately from history samples.  Each operation uses its own
/// short-lived connection so a collector and the dashboard can safely share the database.
/// </summary>
internal sealed class RecommendationStore : IDisposable
{
    private const int BusyTimeoutMilliseconds = 3_000;
    private readonly string _connectionString;
    private readonly JsonSerializerOptions _jsonOptions;

    public RecommendationStore(string dataDirectory, JsonSerializerOptions jsonOptions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(jsonOptions);

        Directory.CreateDirectory(dataDirectory);
        DatabasePath = Path.Combine(dataDirectory, "proclens.db");
        _jsonOptions = jsonOptions;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS recommendations (
                id TEXT PRIMARY KEY,
                target_group_json TEXT NOT NULL,
                member_identities_json TEXT NOT NULL,
                action TEXT NOT NULL,
                provenance_json TEXT NOT NULL,
                confidence_pct INTEGER NOT NULL,
                confidence_kind TEXT NOT NULL,
                risk TEXT NOT NULL,
                expected_impact_json TEXT NOT NULL,
                evidence_json TEXT NOT NULL,
                created_at_utc INTEGER NOT NULL,
                expires_at_utc INTEGER NOT NULL,
                snapshot_hash TEXT NULL,
                state TEXT NOT NULL,
                snoozed_until_utc INTEGER NULL,
                user_decision TEXT NULL,
                decision_at_utc INTEGER NULL,
                action_result TEXT NULL,
                action_completed_at_utc INTEGER NULL,
                action_detail_code TEXT NULL,
                measured_outcome_json TEXT NULL,
                record_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_recommendations_current
                ON recommendations(state, expires_at_utc, snoozed_until_utc);
            CREATE TABLE IF NOT EXISTS recommendation_feedback (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                recommendation_id TEXT NOT NULL,
                kind TEXT NOT NULL,
                value_json TEXT NOT NULL,
                recorded_at_utc INTEGER NOT NULL,
                FOREIGN KEY(recommendation_id) REFERENCES recommendations(id)
            );
            CREATE INDEX IF NOT EXISTS ix_recommendation_feedback_recommendation
                ON recommendation_feedback(recommendation_id, recorded_at_utc);
            """;
        command.ExecuteNonQuery();
    }

    public string DatabasePath { get; }

    public void Upsert(RecommendationRecord recommendation, string? snapshotHash = null)
    {
        ArgumentNullException.ThrowIfNull(recommendation);
        ValidateRecommendation(recommendation);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO recommendations (
                id, target_group_json, member_identities_json, action, provenance_json,
                confidence_pct, confidence_kind, risk, expected_impact_json, evidence_json,
                created_at_utc, expires_at_utc, snapshot_hash, state, record_json)
            VALUES (
                $id, $targetGroup, $members, $action, $provenance,
                $confidencePct, $confidenceKind, $risk, $impact, $evidence,
                $createdAt, $expiresAt, $snapshotHash, $state, $record)
            ON CONFLICT(id) DO UPDATE SET
                target_group_json = excluded.target_group_json,
                member_identities_json = excluded.member_identities_json,
                action = excluded.action,
                provenance_json = excluded.provenance_json,
                confidence_pct = excluded.confidence_pct,
                confidence_kind = excluded.confidence_kind,
                risk = excluded.risk,
                expected_impact_json = excluded.expected_impact_json,
                evidence_json = excluded.evidence_json,
                expires_at_utc = excluded.expires_at_utc,
                snapshot_hash = excluded.snapshot_hash,
                record_json = excluded.record_json,
                state = CASE WHEN recommendations.state = 'expired' THEN excluded.state ELSE recommendations.state END;
            """;
        command.Parameters.AddWithValue("$id", recommendation.Id);
        command.Parameters.AddWithValue("$targetGroup", Serialize(recommendation.TargetGroup));
        command.Parameters.AddWithValue("$members", Serialize(recommendation.TargetGroup.Members));
        command.Parameters.AddWithValue("$action", EnumText(recommendation.Action));
        command.Parameters.AddWithValue("$provenance", Serialize(recommendation.Provenance));
        command.Parameters.AddWithValue("$confidencePct", recommendation.Confidence.Pct);
        command.Parameters.AddWithValue("$confidenceKind", EnumText(recommendation.Confidence.Kind));
        command.Parameters.AddWithValue("$risk", EnumText(recommendation.Risk));
        command.Parameters.AddWithValue("$impact", Serialize(recommendation.ExpectedImpact));
        command.Parameters.AddWithValue("$evidence", Serialize(recommendation.Evidence));
        command.Parameters.AddWithValue("$createdAt", ToTicks(recommendation.CreatedAtUtc));
        command.Parameters.AddWithValue("$expiresAt", ToTicks(recommendation.ExpiresAtUtc));
        command.Parameters.AddWithValue("$snapshotHash", (object?)snapshotHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$state", EnumText(recommendation.State));
        command.Parameters.AddWithValue("$record", Serialize(recommendation));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<RecommendationRecord> ListCurrent(DateTimeOffset? nowUtc = null)
    {
        var now = nowUtc ?? DateTimeOffset.UtcNow;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT record_json,
                   CASE
                       WHEN state = 'snoozed'
                            AND (snoozed_until_utc IS NULL OR snoozed_until_utc <= $now)
                           THEN 'active'
                       ELSE state
                   END AS current_state
            FROM recommendations
            WHERE expires_at_utc > $now
              AND state IN ('active', 'needed', 'snoozed')
              AND (state <> 'snoozed' OR snoozed_until_utc IS NULL OR snoozed_until_utc <= $now)
            ORDER BY confidence_pct DESC, created_at_utc DESC;
            """;
        command.Parameters.AddWithValue("$now", ToTicks(now));
        using var reader = command.ExecuteReader();
        var recommendations = new List<RecommendationRecord>();
        while (reader.Read())
        {
            var recommendation = DeserializeRecord(reader.GetString(0));
            if (recommendation is not null) recommendations.Add(recommendation with { State = ParseState(reader.GetString(1)) });
        }
        return recommendations;
    }

    public RecommendationRecord? FindById(string recommendationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recommendationId);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT record_json, state FROM recommendations WHERE id = $id;";
        command.Parameters.AddWithValue("$id", recommendationId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? DeserializeRecord(reader.GetString(0)) is { } recommendation
                ? recommendation with { State = ParseState(reader.GetString(1)) }
                : null
            : null;
    }

    public int Expire(DateTimeOffset? nowUtc = null)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE recommendations
            SET state = 'expired'
            WHERE expires_at_utc <= $now AND state NOT IN ('dismissed', 'expired');
            """;
        command.Parameters.AddWithValue("$now", ToTicks(nowUtc ?? DateTimeOffset.UtcNow));
        return command.ExecuteNonQuery();
    }

    public bool MarkNeeded(RecommendationDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.Decision != RecommendationDecisionKind.Needed) throw new ArgumentException("The decision must be 'needed'.", nameof(decision));
        return RecordFeedback(decision);
    }

    public bool Snooze(RecommendationDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.Decision != RecommendationDecisionKind.Snooze || decision.SnoozedUntilUtc is null)
            throw new ArgumentException("A snooze decision requires a snoozed-until time.", nameof(decision));
        return RecordFeedback(decision);
    }

    public bool RecordFeedback(RecommendationDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.Decision == RecommendationDecisionKind.Snooze && decision.SnoozedUntilUtc is null)
            throw new ArgumentException("A snooze decision requires a snoozed-until time.", nameof(decision));

        var state = decision.Decision switch
        {
            RecommendationDecisionKind.Needed => RecommendationState.Needed,
            RecommendationDecisionKind.Snooze => RecommendationState.Snoozed,
            RecommendationDecisionKind.Dismiss => RecommendationState.Dismissed,
            _ => throw new ArgumentOutOfRangeException(nameof(decision))
        };

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE recommendations
            SET state = $state, snoozed_until_utc = $snoozedUntil,
                user_decision = $decision, decision_at_utc = $decidedAt
            WHERE id = $id;
            """;
        update.Parameters.AddWithValue("$state", EnumText(state));
        update.Parameters.AddWithValue("$snoozedUntil", decision.SnoozedUntilUtc is { } until ? ToTicks(until) : DBNull.Value);
        update.Parameters.AddWithValue("$decision", EnumText(decision.Decision));
        update.Parameters.AddWithValue("$decidedAt", ToTicks(decision.DecidedAtUtc));
        update.Parameters.AddWithValue("$id", decision.RecommendationId);
        if (update.ExecuteNonQuery() == 0) return false;
        InsertFeedback(connection, transaction, decision.RecommendationId, "decision", Serialize(decision), decision.DecidedAtUtc);
        transaction.Commit();
        return true;
    }

    public bool RecordAction(RecommendationActionResult result, ExpectedImpact? measuredOutcome = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE recommendations
            SET state = 'acted', action_result = $result,
                action_completed_at_utc = $completedAt, action_detail_code = $detail,
                measured_outcome_json = $measuredOutcome
            WHERE id = $id;
            """;
        update.Parameters.AddWithValue("$result", EnumText(result.Result));
        update.Parameters.AddWithValue("$completedAt", ToTicks(result.CompletedAtUtc));
        update.Parameters.AddWithValue("$detail", result.DetailCode);
        update.Parameters.AddWithValue("$measuredOutcome", measuredOutcome is null ? DBNull.Value : Serialize(measuredOutcome));
        update.Parameters.AddWithValue("$id", result.RecommendationId);
        if (update.ExecuteNonQuery() == 0) return false;
        InsertFeedback(connection, transaction, result.RecommendationId, "action", Serialize(result), result.CompletedAtUtc);
        transaction.Commit();
        return true;
    }

    public void Dispose()
    {
        // Connections are scoped to operations; this method exists for a uniform store lifetime API.
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA busy_timeout={BusyTimeoutMilliseconds}; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
        command.ExecuteNonQuery();
        return connection;
    }

    private void InsertFeedback(SqliteConnection connection, SqliteTransaction transaction, string recommendationId, string kind, string valueJson, DateTimeOffset recordedAtUtc)
    {
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO recommendation_feedback(recommendation_id, kind, value_json, recorded_at_utc)
            VALUES($id, $kind, $value, $recordedAt);
            """;
        insert.Parameters.AddWithValue("$id", recommendationId);
        insert.Parameters.AddWithValue("$kind", kind);
        insert.Parameters.AddWithValue("$value", valueJson);
        insert.Parameters.AddWithValue("$recordedAt", ToTicks(recordedAtUtc));
        insert.ExecuteNonQuery();
    }

    private string Serialize<T>(T value) => JsonSerializer.Serialize(value, _jsonOptions);

    private string EnumText<T>(T value) where T : struct, Enum => JsonSerializer.Serialize(value, _jsonOptions).Trim('"');

    private RecommendationRecord? DeserializeRecord(string json)
    {
        try { return JsonSerializer.Deserialize<RecommendationRecord>(json, _jsonOptions); }
        catch (JsonException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    private static RecommendationState ParseState(string value) => Enum.TryParse<RecommendationState>(value, true, out var state) ? state : RecommendationState.Expired;

    private static long ToTicks(DateTimeOffset value) => value.UtcDateTime.Ticks;

    private static void ValidateRecommendation(RecommendationRecord recommendation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recommendation.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(recommendation.TargetGroup.Label);
        if (recommendation.TargetGroup.Members.Count == 0) throw new ArgumentException("A recommendation needs at least one member.", nameof(recommendation));
        if (recommendation.ExpiresAtUtc <= recommendation.CreatedAtUtc) throw new ArgumentException("Expiry must be after creation.", nameof(recommendation));
    }
}
