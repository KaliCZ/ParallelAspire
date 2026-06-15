using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace ParallelAspire;

/// <summary>
/// Lets multiple AppHost instances (e.g. git worktrees) run in parallel by giving each its own
/// Aspire dashboard / OTLP / resource-service ports — plus any extras — instead of the fixed
/// defaults that collide.
/// <para>
/// <c>ReserveAsync</c> takes a machine-wide lock, probes for free ports, and points Aspire
/// at them through env vars. The lock is held by the returned object: keep it alive across the
/// app's startup and dispose it once the app has started, so a sibling can't pick the same ports
/// while we're still binding them. Set <see cref="PortReservationOptions.OffsetEnvironmentVariable"/>
/// to pin deterministic ports and skip the lock entirely.
/// </para>
/// <code>
/// DistributedApplication app;
/// using (var ports = await PortReservation.ReserveAsync(1))
/// {
///     var builder = DistributedApplication.CreateBuilder(args);
///     builder.AddRedis("redis", port: ports.ExtraPorts[0]);
///     app = builder.Build();
///     await app.StartAsync();   // infra ports are bound by the time this returns
/// }                             // lock released here, after startup
/// await app.WaitForShutdownAsync();
/// </code>
/// </summary>
public sealed class PortReservation : IDisposable
{
    // Width of each probe lane: a port type is probed in [base, base + LaneWidth) and lanes sit
    // LaneWidth apart, so independent probes never stray into a neighbour's range.
    private const int LaneWidth = 64;

    private readonly FileStream? _lockHandle;   // null in pinned mode (no lock taken)
    private bool _disposed;

    private PortReservation(FileStream? lockHandle) => _lockHandle = lockHandle;

    /// <summary>The reserved extra ports, in the order requested via <see cref="PortReservationOptions.ExtraPortCount"/>.</summary>
    public IReadOnlyList<int> ExtraPorts { get; private set; } = [];

    /// <summary>Reserve the Aspire ports (and any extras) for this AppHost instance.</summary>
    /// <param name="configure">Optional tweaks; see <see cref="PortReservationOptions"/>.</param>
    /// <param name="cancellationToken">Cancels waiting for the lock.</param>
    // Non-async + NoInlining so GetCallingAssembly resolves the real caller, not the async state
    // machine; the actual awaiting happens in ReserveCoreAsync.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Task<PortReservation> ReserveAsync(
        Action<PortReservationOptions>? configure = null, CancellationToken cancellationToken = default)
    {
        var options = new PortReservationOptions();
        configure?.Invoke(options);
        var caller = Assembly.GetCallingAssembly();
        return ReserveCoreAsync(options, caller, cancellationToken);
    }

    /// <summary>Reserve the Aspire ports plus <paramref name="extraPortCount"/> extras, leaving everything else default.</summary>
    /// <param name="extraPortCount">Extra ports to reserve beyond Aspire's own; see <see cref="ExtraPorts"/>.</param>
    /// <param name="cancellationToken">Cancels waiting for the lock.</param>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Task<PortReservation> ReserveAsync(
        int extraPortCount, CancellationToken cancellationToken = default)
    {
        var caller = Assembly.GetCallingAssembly();
        return ReserveCoreAsync(new PortReservationOptions { ExtraPortCount = extraPortCount }, caller, cancellationToken);
    }

    private static async Task<PortReservation> ReserveCoreAsync(
        PortReservationOptions options, Assembly caller, CancellationToken cancellationToken)
    {
        // OTLP block lanes: gRPC, HTTP, resource-service, then one lane per extra port.
        var otlpHttpBase = options.OtlpBase + LaneWidth;
        var resourceBase = options.OtlpBase + 2 * LaneWidth;
        var extraBase = options.OtlpBase + 3 * LaneWidth;

        // Pinned mode: deterministic ports, no lock or probing.
        if (options.OffsetEnvironmentVariable is { } offsetVar
            && int.TryParse(Environment.GetEnvironmentVariable(offsetVar), out var offset))
        {
            var pinned = new PortReservation(null)
            {
                ExtraPorts = PinnedExtras(options.ExtraPortCount, extraBase, offset),
            };
            SetEnv(
                options.DashboardBase + offset,
                options.OtlpBase + offset,
                otlpHttpBase + offset,
                resourceBase + offset);
            return pinned;
        }

        var lockName = options.LockName ?? caller.GetName().Name ?? "ParallelAspire";
        var lockHandle = await AcquireLockAsync(lockName, options.Logger ?? Console.WriteLine, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            SetEnv(
                FindFreePortFrom(options.DashboardBase),
                FindFreePortFrom(options.OtlpBase),
                FindFreePortFrom(otlpHttpBase),
                FindFreePortFrom(resourceBase));

            var extras = new int[options.ExtraPortCount];
            for (var i = 0; i < extras.Length; i++)
                extras[i] = FindFreePortFrom(extraBase + i * LaneWidth);

            return new PortReservation(lockHandle) { ExtraPorts = extras };
        }
        catch
        {
            lockHandle.Dispose();   // don't leak the lock if probing throws
            throw;
        }
    }

    // Holds the lock as an exclusive (FileShare.None) file handle — no thread affinity (so it can be
    // disposed on whatever thread startup resumed on), cross-platform, and auto-released by the OS if
    // the process dies. A sibling that can't open it polls until we release.
    private static async Task<FileStream> AcquireLockAsync(
        string lockName, Action<string> log, CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Sanitize(lockName)}.lock");
        var poll = TimeSpan.FromMilliseconds(200);
        var heartbeat = TimeSpan.FromSeconds(5);
        var waited = TimeSpan.Zero;
        var nextBeat = heartbeat;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) { /* held by another instance — wait and retry */ }

            await Task.Delay(poll, cancellationToken).ConfigureAwait(false);
            waited += poll;
            if (waited >= nextBeat)
            {
                log($"[ParallelAspire] Still waiting for the port-reservation lock ({path}) — " +
                    $"{waited.TotalSeconds:F0}s elapsed. Another AppHost instance is starting.");
                nextBeat += heartbeat;
            }
        }
    }

    private static int[] PinnedExtras(int count, int extraBase, int offset)
    {
        var extras = new int[count];
        for (var i = 0; i < count; i++)
            extras[i] = extraBase + i * LaneWidth + offset;
        return extras;
    }

    // Probe (bind then immediately release) to step past ports an unrelated process is using. We
    // don't hold the port: the lock keeps siblings out until our host binds it for real, and a
    // non-AppHost process grabbing it in that gap is unpreventable and out of scope.
    private static int FindFreePortFrom(int start)
    {
        for (var port = start; port < start + LaneWidth; port++)
        {
            var probe = new TcpListener(IPAddress.Loopback, port);
            try
            {
                probe.Start();
            }
            catch (SocketException)
            {
                continue;   // in use — try the next port
            }
            probe.Stop();   // free it immediately — we don't hold the port
            return port;
        }
        throw new InvalidOperationException($"No free port found in [{start}, {start + LaneWidth}).");
    }

    private static void SetEnv(int dashboard, int otlp, int otlpHttp, int resource)
    {
        Environment.SetEnvironmentVariable("ASPNETCORE_URLS", $"http://localhost:{dashboard}");
        Environment.SetEnvironmentVariable("ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL", $"http://localhost:{otlp}");
        Environment.SetEnvironmentVariable("ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL", $"http://localhost:{otlpHttp}");
        Environment.SetEnvironmentVariable("ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL", $"http://localhost:{resource}");
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    /// <summary>Releases the lock so a waiting sibling can proceed. Call once the app has started.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lockHandle?.Dispose();
    }
}
