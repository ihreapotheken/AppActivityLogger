namespace ReportService.Options;

/// <summary>
/// Configuration for the v2 analytics pipeline (ingestion, storage, aggregation). Bound from the
/// <c>Analytics</c> configuration section. Defaults are tuned for the same single-process
/// deployment model as the report service.
/// </summary>
public sealed record RSCAnalyticsOptions
{
    public const string SectionName = "Analytics";

    /// <summary>Master switch. When <c>false</c>, the endpoint returns <c>503</c> and the
    /// aggregation worker stays idle. Lets operators dark-launch the service without exposing it.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>SQLite file holding analytics events, sessions, and rollups. Anchored under
    /// <c>ReportsRoot</c> when relative — same convention as <c>SqliteDbPath</c>.</summary>
    public string SqliteDbPath { get; init; } = "analytics.db";

    /// <summary>The major schema version this server speaks. Batches whose
    /// <c>schemaVersion</c> falls outside <c>[MinAcceptedSchemaVersion, MaxAcceptedSchemaVersion]</c>
    /// are rejected to the dead-letter queue. Minor (additive) skew within the range is tolerated
    /// — unknown fields land in the event's properties bag.</summary>
    public int MinAcceptedSchemaVersion { get; init; } = 1;

    /// <summary>Upper end of accepted schema versions. Bumped only when the server is taught the
    /// new shape; an SDK that ships ahead of the server is rejected on purpose so a forward-incompat
    /// payload doesn't get silently truncated.</summary>
    public int MaxAcceptedSchemaVersion { get; init; } = 1;

    /// <summary>Max events per batch. Batches over this are rejected whole — the SDK should split
    /// before sending. Default 250 matches the typical mobile flush size.</summary>
    public int MaxEventsPerBatch { get; init; } = 250;

    /// <summary>Hard cap on a single event's <c>Properties</c> count. Anything richer is a schema
    /// bug, not legitimate use.</summary>
    public int MaxPropertiesPerEvent { get; init; } = 64;

    /// <summary>Max length of any single property value, in characters. Excess is rejected (not
    /// truncated) so SDK authors notice the silent-data-loss bug at the source.</summary>
    public int MaxPropertyValueLength { get; init; } = 2048;

    /// <summary>Max length of a property key. Long keys are almost always typos or leakage.</summary>
    public int MaxPropertyKeyLength { get; init; } = 64;

    /// <summary>Max permitted clock skew, in seconds, between the event's <c>occurredAt</c> and
    /// the server's wall clock at receive time. Anything beyond either direction is DLQ'd. Mobile
    /// clocks drift; this is the threshold that says "we no longer trust this timestamp".</summary>
    public int MaxClockSkewSeconds { get; init; } = 86400;

    /// <summary>Hashing pepper for <c>anonymousId</c> and <c>clientId</c>. NEVER store user IDs
    /// verbatim. A change here invalidates every retention cohort that crosses the change
    /// boundary — the aggregation worker writes the active hash version into <c>analytics_user_days</c>
    /// so post-rotation rebuilds are at least possible.</summary>
    public string IdentifierHashPepper { get; init; } = string.Empty;

    /// <summary>Active identifier-hash version. Bumped together with <see cref="IdentifierHashPepper"/>
    /// on rotation. Stored alongside every hashed identifier in the user-days table.</summary>
    public int IdentifierHashVersion { get; init; } = 1;

    /// <summary>Property keys the validator forbids outright (case-insensitive). Catches obvious
    /// PII leakage from SDK authors — the right home for an email address is the report-problem
    /// flow, not analytics. Anything matched here DLQs the event with reason
    /// <c>pii_key_forbidden</c> so it's visible on the Health page.</summary>
    public string[] ForbiddenPropertyKeys { get; init; } = new[]
    {
        "email", "email_address", "emailaddress",
        "phone", "phone_number", "phonenumber", "msisdn",
        "password", "passwd", "secret", "token", "api_key", "apikey",
        "iban", "credit_card", "creditcard", "card_number",
        "ssn", "social_security",
    };

    /// <summary>Extra platforms accepted on the analytics ingestion path only (never the
    /// problem-report path), for events that originate server-side rather than from a mobile SDK —
    /// see <c>POST /api/v2/analytics/server-events</c>. Unioned with
    /// <see cref="RSCReportServiceOptions.AllowedPlatforms"/> by the validator, so a backend may
    /// also attribute events to <c>android</c>/<c>ios</c> when it knows the user's device.</summary>
    public string[] ServerPlatforms { get; init; } = new[] { Models.RSCAnalyticsPlatforms.Backend };

    /// <summary>How long an aggregation tick sleeps between runs. Floored at 5s.</summary>
    public int AggregationIntervalSeconds { get; init; } = 30;

    /// <summary>Hard cap on events processed by one aggregation tick. Limits I/O bursts and
    /// keeps the worker preemptable on shutdown.</summary>
    public int AggregationBatchSize { get; init; } = 5000;

    /// <summary>Retention, in days, for the raw <c>analytics_events</c> rows. Rollup tables are
    /// retained indefinitely (they're tiny). Aligns with <c>RetentionMaxAgeDays</c> on the report
    /// side so operators can reason about one number.</summary>
    public int RawEventRetentionDays { get; init; } = 30;

    /// <summary>Retention, in days, for dead-letter rows. They're operational signal, not
    /// long-term storage — surfaced on <c>/Analytics/Health</c> while alive.</summary>
    public int DeadLetterRetentionDays { get; init; } = 14;

    /// <summary>Per-statement SQLite command timeout for the analytics DB.</summary>
    public int SqliteCommandTimeoutSeconds { get; init; } = 10;

    /// <summary>Cadence for <see cref="ReportService.Analytics.RSCAnalyticsCohortWorker"/> — the
    /// retention recompute is bounded but does a full scan of <c>analytics_user_days</c>, so the
    /// default is once an hour. Floor 5s (dev uses 15s).</summary>
    public int CohortIntervalSeconds { get; init; } = 3600;

    /// <summary>Cadence for <see cref="ReportService.Analytics.RSCAnalyticsFunnelWorker"/>.
    /// Walks unobserved (per-funnel) events in <c>analytics_events</c> and writes
    /// <c>analytics_funnel_steps</c> entries. Floor 5s (dev uses 15s).</summary>
    public int FunnelIntervalSeconds { get; init; } = 600;

    /// <summary>Funnel definitions to seed into <c>analytics_funnel_definitions</c> on startup.
    /// Operator-overridable via the admin page; the seed only inserts a row when the funnel key
    /// is missing, so an operator edit isn't reverted at the next restart.</summary>
    public RSCAnalyticsFunnelSeed[] SeedFunnels { get; init; } = new[]
    {
        new RSCAnalyticsFunnelSeed
        {
            FunnelKey = "otc_purchase",
            DisplayName = "OTC purchase funnel",
            Steps = new[]
            {
                new RSCAnalyticsFunnelSeedStep { Name = "search",      EventName = "otc_search_submitted", EventType = "action" },
                new RSCAnalyticsFunnelSeedStep { Name = "view_item",   EventName = "otc_product_view",    EventType = "screen" },
                new RSCAnalyticsFunnelSeedStep { Name = "add_to_cart", EventName = "add_to_cart",         EventType = "ecommerce" },
                new RSCAnalyticsFunnelSeedStep { Name = "purchase",    EventName = "purchase",            EventType = "ecommerce" }
            }
        },
        new RSCAnalyticsFunnelSeed
        {
            FunnelKey = "cardlink_activation",
            DisplayName = "CardLink activation",
            Steps = new[]
            {
                new RSCAnalyticsFunnelSeedStep { Name = "start",   EventName = "cardlink_start",         EventType = "action" },
                new RSCAnalyticsFunnelSeedStep { Name = "consent", EventName = "cardlink_consent_given", EventType = "action" },
                new RSCAnalyticsFunnelSeedStep { Name = "auth",    EventName = "cardlink_auth_started",  EventType = "action" },
                new RSCAnalyticsFunnelSeedStep { Name = "success", EventName = "cardlink_success",       EventType = "action" }
            }
        }
    };

    /// <summary>
    /// Self-validates the numeric and schema-version invariants the pipeline assumes but doesn't
    /// otherwise enforce. Returns one human-readable message per violated invariant (empty when the
    /// options are coherent). An inverted schema range or a non-positive cap would otherwise silently
    /// dead-letter every event (the only symptom being DLQ growth on the Health page) rather than
    /// failing loudly — call this from the host's options pipeline
    /// (<c>AddOptions&lt;RSCAnalyticsOptions&gt;().Validate(o =&gt; o.Validate().Count == 0).ValidateOnStart()</c>)
    /// to fail fast at startup. Wiring that into Program.cs is owned by the infra/boot packet.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (MinAcceptedSchemaVersion > MaxAcceptedSchemaVersion)
            errors.Add($"MinAcceptedSchemaVersion ({MinAcceptedSchemaVersion}) must be <= MaxAcceptedSchemaVersion ({MaxAcceptedSchemaVersion}); an inverted range dead-letters every batch as schema_version_unsupported.");
        if (MaxEventsPerBatch <= 0)
            errors.Add($"MaxEventsPerBatch ({MaxEventsPerBatch}) must be > 0; otherwise every non-empty batch is rejected as batch_too_large.");
        if (MaxPropertiesPerEvent <= 0)
            errors.Add($"MaxPropertiesPerEvent ({MaxPropertiesPerEvent}) must be > 0.");
        if (MaxPropertyValueLength <= 0)
            errors.Add($"MaxPropertyValueLength ({MaxPropertyValueLength}) must be > 0.");
        if (MaxPropertyKeyLength <= 0)
            errors.Add($"MaxPropertyKeyLength ({MaxPropertyKeyLength}) must be > 0.");
        if (MaxClockSkewSeconds <= 0)
            errors.Add($"MaxClockSkewSeconds ({MaxClockSkewSeconds}) must be > 0; otherwise every event is rejected as clock_skew.");
        if (AggregationBatchSize <= 0)
            errors.Add($"AggregationBatchSize ({AggregationBatchSize}) must be > 0.");
        if (RawEventRetentionDays < 0)
            errors.Add($"RawEventRetentionDays ({RawEventRetentionDays}) must be >= 0.");
        if (DeadLetterRetentionDays < 0)
            errors.Add($"DeadLetterRetentionDays ({DeadLetterRetentionDays}) must be >= 0.");

        return errors;
    }
}

/// <summary>Plain-old-config DTO for a seed funnel. Materialised into an
/// <c>RSCAnalyticsFunnelDefinition</c> at startup if no row exists for the given <c>FunnelKey</c>.</summary>
public sealed class RSCAnalyticsFunnelSeed
{
    public string FunnelKey { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public RSCAnalyticsFunnelSeedStep[] Steps { get; init; } = Array.Empty<RSCAnalyticsFunnelSeedStep>();
}

public sealed class RSCAnalyticsFunnelSeedStep
{
    public string Name { get; init; } = string.Empty;
    public string EventName { get; init; } = string.Empty;
    public string? EventType { get; init; }
}
