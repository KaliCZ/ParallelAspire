namespace Kalicz.Aspire;

/// <summary>
/// Tunables for <see cref="AspirePortReservation.Reserve"/>. All optional — the defaults give a
/// working reservation; override only what collides with another local Aspire app.
/// </summary>
public sealed class AspirePortOptions
{
    /// <summary>
    /// Name of the machine-wide mutex that serializes reservation across processes. When null
    /// (default), the calling assembly's name is used, so each AppHost coordinates with its own
    /// siblings (worktrees) but not with unrelated apps.
    /// </summary>
    public string? MutexName { get; set; }

    /// <summary>First port scanned for the Aspire dashboard frontend (<c>ASPNETCORE_URLS</c>).</summary>
    public int DashboardBase { get; set; } = 16000;

    /// <summary>
    /// First port of the OTLP block. The OTLP gRPC, OTLP HTTP, resource-service, and any
    /// <see cref="ExtraPortCount"/> ports are scanned in adjacent lanes starting here.
    /// </summary>
    public int OtlpBase { get; set; } = 19000;

    /// <summary>
    /// Extra application ports to reserve beyond Aspire's own (e.g. one for Redis, one for
    /// RabbitMQ). Exposed in order via <see cref="AspirePortReservation.ExtraPorts"/>.
    /// </summary>
    public int ExtraPortCount { get; set; }

    /// <summary>
    /// Optional env var holding an integer offset. When set and parseable, ports are pinned to
    /// <c>base + offset</c> deterministically with no scanning or holding — handy for stable,
    /// reproducible ports in scripts/CI. Null (default) disables pinned mode.
    /// </summary>
    public string? OffsetEnvironmentVariable { get; set; }
}
