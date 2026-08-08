namespace AShareRadar.Contracts.Review;

public sealed record LongTermTrackingQueryDto(
    DateOnly? FromDate,
    DateOnly? ToDate,
    string? Symbol,
    string? StrategyCode,
    string? Status,
    string? SortBy,
    bool Descending,
    int Count);

public sealed record LongTermTrackingQueryResultDto(
    int TotalCount,
    DateTimeOffset? LastHitAt,
    IReadOnlyList<LongTermTrackingItemDto> Items);

public sealed record LongTermTrackingBackfillResultDto(
    DateTimeOffset BackfilledAt,
    int ItemCount,
    int EventCount);

public sealed record LongTermTrackingItemDto(
    Guid Id,
    string Symbol,
    string Name,
    string StrategyCode,
    string StrategyName,
    DateTimeOffset FirstHitAt,
    DateTimeOffset LastHitAt,
    int HitCount,
    decimal? HitPrice,
    decimal? CurrentPrice,
    decimal? ReturnFromHit,
    decimal LatestScore,
    decimal BestScore,
    string LatestReason,
    string? LatestRisk,
    string Status,
    int ManualPriority,
    string? Note,
    string? Tags,
    Guid? LatestEventId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record LongTermTrackingTimelineItemDto(
    Guid EventId,
    DateTimeOffset EventTime,
    string Symbol,
    string Name,
    string StrategyCode,
    string StrategyName,
    decimal Score,
    decimal? Price,
    string Reason,
    string? Risk);

public sealed record UpdateLongTermTrackingStatusRequest(string Status);

public sealed record UpdateLongTermTrackingNoteRequest(string? Note);
