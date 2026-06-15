namespace ParallelAspire;

/// <summary>
/// Tunables for <see cref="PortReservation"/>. All optional — the defaults give a working
/// reservation; override only what collides with another local Aspire app.
/// </summary>
public sealed class PortReservationOptions
{
    /// <summary>
    /// Name of the cross-process lock that serializes reservation across AppHost instances (it
    /// becomes a lock file under the temp directory). When null (default), the calling assembly's
    /// name is used, so each AppHost coordinates with its own siblings — e.g. worktrees of the same
    /// repo — but not with unrelated apps.
    /// </summary>
    public string? LockName { get; set; }

    /// <summary>First port probed for the Aspire dashboard frontend (<c>ASPNETCORE_URLS</c>).</summary>
    public int DashboardBase { get; set; } = 16000;

    /// <summary>
    /// First port of the OTLP block. The OTLP gRPC, OTLP HTTP, resource-service, and any
    /// <see cref="ExtraPortCount"/> ports are probed in adjacent lanes starting here.
    /// </summary>
    public int OtlpBase { get; set; } = 19000;

    /// <summary>
    /// Extra application ports to reserve beyond Aspire's own (e.g. one for Redis, one for
    /// RabbitMQ). Exposed in order via <see cref="PortReservation.ExtraPorts"/>.
    /// </summary>
    public int ExtraPortCount { get; set; }

    /// <summary>
    /// Optional env var holding an integer offset. When set and parseable, ports are pinned to
    /// <c>base + offset</c> deterministically with no lock or probing — handy for stable,
    /// reproducible ports in scripts/CI. Null (default) disables pinned mode.
    /// </summary>
    public string? OffsetEnvironmentVariable { get; set; }

    /// <summary>
    /// Sink for diagnostic messages — currently the every-5-seconds "still waiting for the lock"
    /// heartbeat emitted while another AppHost instance is mid-startup. Defaults to
    /// <see cref="System.Console.WriteLine(string)"/>.
    /// </summary>
    public Action<string>? Logger { get; set; }
}
