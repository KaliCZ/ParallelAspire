using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Kalicz.Aspire;

/// <summary>
/// Lets multiple AppHost instances (e.g. git worktrees) run in parallel by giving each its own
/// Aspire dashboard / OTLP / resource-service ports — plus any extras — instead of the fixed
/// defaults that collide.
/// <para>
/// <see cref="Reserve"/> finds free ports — serialized machine-wide via a named mutex so two
/// simultaneous starts can't pick the same one — points Aspire at them through env vars, and
/// holds the ports (open listeners) until <see cref="ReleaseForStartup"/> is called right before
/// the host binds them. Set <see cref="AspirePortOptions.OffsetEnvironmentVariable"/> to pin
/// deterministic ports and skip scanning.
/// </para>
/// </summary>
public sealed class AspirePortReservation : IDisposable
{
    // Width of each scan lane: a port type scans [base, base + LaneWidth) and lanes sit LaneWidth
    // apart, so independent scans never stray into a neighbour's range.
    private const int LaneWidth = 64;

    private readonly Mutex? _mutex;
    private readonly List<TcpListener> _held = [];
    private bool _released;

    private AspirePortReservation(Mutex? mutex) => _mutex = mutex;

    /// <summary>The reserved extra ports, in the order requested via <see cref="AspirePortOptions.ExtraPortCount"/>.</summary>
    public IReadOnlyList<int> ExtraPorts { get; private set; } = [];

    /// <summary>Reserve the Aspire ports (and any extras) for this AppHost instance.</summary>
    /// <param name="configure">Optional tweaks; see <see cref="AspirePortOptions"/>.</param>
    [MethodImpl(MethodImplOptions.NoInlining)] // keep GetCallingAssembly pointing at the real caller
    public static AspirePortReservation Reserve(Action<AspirePortOptions>? configure = null)
    {
        var options = new AspirePortOptions();
        configure?.Invoke(options);

        // OTLP block lanes: gRPC, HTTP, resource-service, then one lane per extra port.
        var otlpHttpBase = options.OtlpBase + LaneWidth;
        var resourceBase = options.OtlpBase + 2 * LaneWidth;
        var extraBase = options.OtlpBase + 3 * LaneWidth;

        // Pinned mode: deterministic ports, no scan/hold needed.
        if (options.OffsetEnvironmentVariable is { } offsetVar
            && int.TryParse(Environment.GetEnvironmentVariable(offsetVar), out var offset))
        {
            var pinned = new AspirePortReservation(null)
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

        var mutexName = options.MutexName
            ?? Assembly.GetCallingAssembly().GetName().Name
            ?? "Kalicz.Aspire";

        var mutex = new Mutex(false, mutexName);
        try { mutex.WaitOne(TimeSpan.FromSeconds(30)); }
        catch (AbandonedMutexException) { /* previous holder died mid-reservation; we own it now */ }

        var reservation = new AspirePortReservation(mutex);
        SetEnv(
            reservation.ReserveFreePortFrom(options.DashboardBase),
            reservation.ReserveFreePortFrom(options.OtlpBase),
            reservation.ReserveFreePortFrom(otlpHttpBase),
            reservation.ReserveFreePortFrom(resourceBase));

        var extras = new int[options.ExtraPortCount];
        for (var i = 0; i < extras.Length; i++)
            extras[i] = reservation.ReserveFreePortFrom(extraBase + i * LaneWidth);
        reservation.ExtraPorts = extras;

        return reservation;
    }

    private static int[] PinnedExtras(int count, int extraBase, int offset)
    {
        var extras = new int[count];
        for (var i = 0; i < count; i++)
            extras[i] = extraBase + i * LaneWidth + offset;
        return extras;
    }

    private int ReserveFreePortFrom(int start)
    {
        for (var port = start; port < start + LaneWidth; port++)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                _held.Add(listener);   // hold it open so a sibling instance sees the port taken
                return port;
            }
            catch (SocketException) { /* in use — try the next port */ }
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

    /// <summary>Release the held ports and the mutex, right before the host binds the ports itself.</summary>
    public void ReleaseForStartup() => Dispose();

    /// <summary>Releases any still-held ports and the mutex. Idempotent; safe to call more than once.</summary>
    public void Dispose()
    {
        if (_released) return;
        _released = true;
        foreach (var listener in _held)
        {
            try { listener.Stop(); } catch { /* best effort */ }
        }
        _held.Clear();
        if (_mutex is not null)
        {
            try { _mutex.ReleaseMutex(); } catch { /* not held / abandoned */ }
            _mutex.Dispose();
        }
    }
}
