using System.Text.Json;
using MediatR;
using Mazda.Application.Common.Abstractions;
using Mazda.Application.Common.Tracing;
using Mazda.Domain.Common;

namespace Mazda.Application.Modules.Traces;

// Traces: where the time went in a function check, a gather + plan, a run, a round trip.

public record TraceViewDto(Guid Id, string Kind, string Label, Guid SubjectId, DateTime CreatedAt, DateTime UpdatedAt,
    TraceDoc Trace, TraceSummary Summary);

public record GetTraceQuery(Guid Id) : IRequest<Result<TraceViewDto>>;

public class GetTraceHandler(ITraceStore store, ICurrentUser me) : IRequestHandler<GetTraceQuery, Result<TraceViewDto>>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<Result<TraceViewDto>> Handle(GetTraceQuery q, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<TraceViewDto>(Error.Validation("Not authenticated."));
        var row = await store.FindAsync(q.Id, me.UserId.Value, ct);
        if (row is null) return Result.Failure<TraceViewDto>(Error.NotFound("Trace"));
        TraceDoc? doc;
        try { doc = JsonSerializer.Deserialize<TraceDoc>(row.TraceJson, Json); }
        catch (JsonException) { doc = null; }
        doc ??= new TraceDoc(row.Kind, row.Label, 0, row.TotalMs, new(), new());
        return Result.Success(new TraceViewDto(row.Id, row.Kind, row.Label, row.SubjectId, row.CreatedAt, row.UpdatedAt,
            doc, TraceAnalyzer.Summarize(doc)));
    }
}

/// <summary>Traces about one subject (a function, a generation, a round trip), newest first.</summary>
public record ListTracesQuery(Guid SubjectId) : IRequest<Result<IReadOnlyList<TraceListItem>>>;

public class ListTracesHandler(ITraceStore store, ICurrentUser me) : IRequestHandler<ListTracesQuery, Result<IReadOnlyList<TraceListItem>>>
{
    public async Task<Result<IReadOnlyList<TraceListItem>>> Handle(ListTracesQuery q, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<IReadOnlyList<TraceListItem>>(Error.Validation("Not authenticated."));
        return Result.Success(await store.ListAsync(q.SubjectId, me.UserId.Value, ct));
    }
}
