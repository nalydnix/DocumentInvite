using System.Globalization;
using System.Text;
using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocumentInvite.Api.Features.Audit;

/// <summary>Searches, exports and verifies the audit trail. Exports and verification are themselves audited.</summary>
public sealed class AuditQueryService(AppDbContext db, IAuditLog audit, IAuditVerifier verifier)
{
    private static readonly string[] CsvHeader =
    [
        "id", "occurredAt", "actorType", "actorId", "action", "targetType", "targetId",
        "ipAddress", "userAgent", "correlationId", "details",
    ];

    public async Task<AuditPage> ListAsync(AuditQuery query, CancellationToken ct)
    {
        var filtered = Filter(db.AuditEvents.AsNoTracking(), query);
        var total = await filtered.CountAsync(ct);
        var items = await filtered
            .OrderByDescending(x => x.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(ct);

        return new AuditPage(items.Select(ToResponse).ToList(), total, query.Page, query.PageSize);
    }

    /// <summary>
    /// Records the export, then streams the rows that matched when it started. The export event
    /// is not part of its own output.
    /// </summary>
    public async Task ExportCsvAsync(AuditQuery query, Stream output, CancellationToken ct)
    {
        var lastId = await db.AuditEvents.AsNoTracking().MaxAsync(x => (long?)x.Id, ct) ?? 0;
        var filtered = Filter(db.AuditEvents.AsNoTracking().Where(x => x.Id <= lastId), query);
        var count = await filtered.CountAsync(ct);

        await audit.RecordAsync("audit.exported", new AuditTarget("audit"),
            AuditDetails.Empty.WithCount("rowCount", count), ct);

        await using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true);
        await writer.WriteAsync(string.Join(',', CsvHeader) + "\r\n");
        await foreach (var e in filtered.OrderBy(x => x.Id).AsAsyncEnumerable().WithCancellation(ct))
        {
            var fields = new[]
            {
                e.Id.ToString(CultureInfo.InvariantCulture),
                e.OccurredAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                e.ActorType.ToString(),
                e.ActorId?.ToString("D"),
                e.Action,
                e.TargetType,
                e.TargetId?.ToString("D"),
                e.IpAddress,
                e.UserAgent,
                e.CorrelationId,
                e.DetailsJson,
            };
            await writer.WriteAsync(string.Join(',', fields.Select(CsvField)) + "\r\n");
        }

        await writer.FlushAsync(ct);
    }

    public async Task<AuditVerifyResponse> VerifyAsync(CancellationToken ct)
    {
        var result = await verifier.VerifyAsync(ct);
        var details = AuditDetails.Empty
            .WithEnum("outcome", result.IsValid ? VerificationOutcome.Valid : VerificationOutcome.Broken)
            .WithCount("eventsChecked", result.EventsChecked);
        if (result.FirstBrokenEventId is { } brokenId)
        {
            details.WithCount("firstBrokenEventId", brokenId);
        }

        await audit.RecordAsync("audit.verified", new AuditTarget("audit"), details, ct);
        return new AuditVerifyResponse(result.IsValid, result.FirstBrokenEventId, result.EventsChecked);
    }

    private static IQueryable<AuditEvent> Filter(IQueryable<AuditEvent> events, AuditQuery q)
    {
        if (q.From is { } from)
        {
            events = events.Where(x => x.OccurredAt >= from);
        }

        if (q.To is { } to)
        {
            events = events.Where(x => x.OccurredAt < to);
        }

        if (q.ActorId is { } actorId)
        {
            events = events.Where(x => x.ActorId == actorId);
        }

        if (!string.IsNullOrWhiteSpace(q.Action))
        {
            var pattern = EscapeLike(q.Action.Trim()) + "%";
            events = events.Where(x => EF.Functions.Like(x.Action, pattern, "\\"));
        }

        if (!string.IsNullOrWhiteSpace(q.TargetType))
        {
            var targetType = q.TargetType.Trim();
            events = events.Where(x => x.TargetType == targetType);
        }

        if (q.TargetId is { } targetId)
        {
            events = events.Where(x => x.TargetId == targetId);
        }

        return events;
    }

    /// <summary>Quotes a CSV field and defuses spreadsheet formulas (user agents are client-supplied).</summary>
    public static string CsvField(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");

    private static AuditEventResponse ToResponse(AuditEvent e) => new(
        e.Id, e.OccurredAt, e.ActorType, e.ActorId, e.Action, e.TargetType, e.TargetId,
        e.IpAddress, e.UserAgent, e.CorrelationId, e.DetailsJson);
}
