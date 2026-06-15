using System.Net;
using System.Net.Sockets;
using ParallelAspire;

// These tests set process-global env vars and bind ports, so they must not run concurrently.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ParallelAspire.Tests;

public class PortReservationTests
{
    private static int PortOf(string envVar) =>
        new Uri(Environment.GetEnvironmentVariable(envVar)
                ?? throw new InvalidOperationException($"{envVar} not set")).Port;

    [Fact]
    public async Task SecondReservation_BlocksUntilFirstIsDisposed()
    {
        const string lockName = "ParallelAspire.Tests.Lock";

        var first = await PortReservation.ReserveAsync(o =>
        {
            o.LockName = lockName;
            o.DashboardBase = 41000;
            o.OtlpBase = 42000;
        });

        var secondTask = PortReservation.ReserveAsync(o =>
        {
            o.LockName = lockName;
            o.DashboardBase = 41000;
            o.OtlpBase = 42000;
        });

        // While the first reservation holds the lock, the second must not acquire it.
        var winner = await Task.WhenAny(secondTask, Task.Delay(500));
        Assert.NotSame(secondTask, winner);

        // Releasing the first lets the second proceed.
        first.Dispose();
        using var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(second);
    }

    [Fact]
    public async Task Probe_StepsPastPortAlreadyInUse()
    {
        const int dashboardBase = 43000;
        using var occupied = new TcpListener(IPAddress.Loopback, dashboardBase);
        occupied.Start();   // hold the base port so the reservation must step past it

        using var ports = await PortReservation.ReserveAsync(o =>
        {
            o.LockName = "ParallelAspire.Tests.Probe";
            o.DashboardBase = dashboardBase;
            o.OtlpBase = 44000;
        });

        Assert.True(PortOf("ASPNETCORE_URLS") > dashboardBase,
            "dashboard port should have stepped past the occupied base port");
    }

    [Fact]
    public async Task WhileWaiting_LogsHeartbeatRepeatedlyUntilTheLockIsReleased()
    {
        const string lockName = "ParallelAspire.Tests.Heartbeat";
        var messages = new System.Collections.Concurrent.ConcurrentQueue<string>();

        // First reservation holds the lock.
        var first = await PortReservation.ReserveAsync(o =>
        {
            o.LockName = lockName;
            o.DashboardBase = 45000;
            o.OtlpBase = 46000;
        });

        // Second waits behind it, heart-beating fast so the test doesn't crawl.
        var secondTask = PortReservation.ReserveAsync(o =>
        {
            o.LockName = lockName;
            o.DashboardBase = 45000;
            o.OtlpBase = 46000;
            o.HeartbeatInterval = TimeSpan.FromMilliseconds(150);
            o.Logger = messages.Enqueue;
        });

        // Wait until it has logged at least twice, then let it through.
        for (var i = 0; i < 100 && messages.Count < 2; i++)
            await Task.Delay(50);

        first.Dispose();
        using var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(messages.Count >= 2, $"expected the wait to be logged at least twice, got {messages.Count}");
        Assert.All(messages, m => Assert.Contains("waiting", m, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ReserveAsync_WithCountOverload_ReservesThatManyDistinctExtraPorts()
    {
        using var ports = await PortReservation.ReserveAsync(3);
        Assert.Equal(3, ports.ExtraPorts.Count);
        Assert.Equal(3, ports.ExtraPorts.Distinct().Count());
    }

    [Fact]
    public async Task PinnedMode_ResolvesPortsToBasePlusOffset_DeterministicallyAndDistinctPerOffset()
    {
        const string offsetVar = "PARALLEL_ASPIRE_TEST_OFFSET";
        async Task<int[]> ReserveAt(int offset)
        {
            Environment.SetEnvironmentVariable(offsetVar, offset.ToString());
            // Pinned mode takes no lock and holds nothing, so nothing to dispose.
            var r = await PortReservation.ReserveAsync(o =>
            {
                o.DashboardBase = 30000;
                o.OtlpBase = 31000;
                o.ExtraPortCount = 1;
                o.OffsetEnvironmentVariable = offsetVar;
            });
            return
            [
                PortOf("ASPNETCORE_URLS"),
                PortOf("ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"),
                PortOf("ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL"),
                PortOf("ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL"),
                .. r.ExtraPorts,
            ];
        }

        try
        {
            var atZero = await ReserveAt(0);
            var atFive = await ReserveAt(5);
            // Dashboard=30000, OTLP=31000, OTLP-HTTP=31064, resource=31128, extra=31192.
            Assert.Equal(new[] { 30000, 31000, 31064, 31128, 31192 }, atZero);
            Assert.Equal(new[] { 30005, 31005, 31069, 31133, 31197 }, atFive);
        }
        finally
        {
            Environment.SetEnvironmentVariable(offsetVar, null);
        }
    }
}
