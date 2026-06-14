using Kalicz.Aspire;

namespace Kalicz.Aspire.Tests;

public class AspirePortReservationTests
{
    // The four Aspire ports are communicated only through env vars; extras come back on the object.
    private static int[] CapturePorts(AspirePortReservation reservation) =>
    [
        PortOf("ASPNETCORE_URLS"),
        PortOf("ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"),
        PortOf("ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL"),
        PortOf("ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL"),
        .. reservation.ExtraPorts,
    ];

    private static int PortOf(string envVar) =>
        new Uri(Environment.GetEnvironmentVariable(envVar)
                ?? throw new InvalidOperationException($"{envVar} not set")).Port;

    [Fact]
    public void TwoLiveReservations_ProduceCompletelyDistinctPorts()
    {
        // Distinct mutex names so the test never depends on OS-specific named-mutex
        // reentrancy: distinctness here is driven purely by the held listeners — while
        // the first reservation is alive it keeps its ports bound, forcing the second
        // to scan past them. Same bases on purpose, so a regression that stopped holding
        // ports would make the two sets collide and fail this test.
        using var first = AspirePortReservation.Reserve(o =>
        {
            o.MutexName = "Kalicz.Aspire.Tests.A";
            o.ExtraPortCount = 2;
        });
        var firstPorts = CapturePorts(first);

        using var second = AspirePortReservation.Reserve(o =>
        {
            o.MutexName = "Kalicz.Aspire.Tests.B";
            o.ExtraPortCount = 2;
        });
        var secondPorts = CapturePorts(second);

        Assert.Equal(6, firstPorts.Length);
        Assert.Equal(6, secondPorts.Length);
        Assert.Equal(firstPorts.Length, firstPorts.Distinct().Count());   // unique within itself
        Assert.Equal(secondPorts.Length, secondPorts.Distinct().Count());
        Assert.Empty(firstPorts.Intersect(secondPorts));                  // no overlap across the two
    }

    [Fact]
    public void PinnedMode_ResolvesPortsToBasePlusOffset_DeterministicallyAndDistinctPerOffset()
    {
        const string offsetVar = "KALICZ_ASPIRE_TEST_OFFSET";
        void Reserve(int offset, out int[] ports)
        {
            Environment.SetEnvironmentVariable(offsetVar, offset.ToString());
            // Pinned mode holds no listeners and takes no mutex, so nothing to dispose.
            var r = AspirePortReservation.Reserve(o =>
            {
                o.DashboardBase = 30000;
                o.OtlpBase = 31000;
                o.ExtraPortCount = 1;
                o.OffsetEnvironmentVariable = offsetVar;
            });
            ports = CapturePorts(r);
        }

        try
        {
            Reserve(0, out var atZero);
            // Dashboard=30000, OTLP=31000, OTLP-HTTP=31064, resource=31128, extra=31192.
            Assert.Equal([30000, 31000, 31064, 31128, 31192], atZero);

            Reserve(5, out var atFive);
            Assert.Equal([30005, 31005, 31069, 31133, 31197], atFive);

            Assert.Empty(atZero.Intersect(atFive));
        }
        finally
        {
            Environment.SetEnvironmentVariable(offsetVar, null);
        }
    }
}
