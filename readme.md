# Kalicz.Aspire

[![build](https://github.com/KaliCZ/Aspire/actions/workflows/build.yml/badge.svg)](https://github.com/KaliCZ/Aspire/actions/workflows/build.yml)
[![NuGet](https://img.shields.io/nuget/v/Kalicz.Aspire.svg)](https://www.nuget.org/packages/Kalicz.Aspire)

Parallel-safe local ports for [.NET Aspire](https://learn.microsoft.com/dotnet/aspire/) AppHosts.

Running more than one AppHost at once — say, one per git worktree — fails because they all
grab the same fixed dashboard / OTLP / resource-service ports. `Kalicz.Aspire` probes for a free
set of ports for each instance, points Aspire at them via the standard env vars, and serializes
startup with a cross-process lock so siblings never pick the same ports.

## Install

```sh
dotnet add package Kalicz.Aspire
```

## Usage

Reserve at the very top of your AppHost, hold the lock across startup, and release it once the
app has started:

```csharp
using Kalicz.Aspire;

DistributedApplication app;
using (var ports = await AspirePortReservation.ReserveAsync())
{
    var builder = DistributedApplication.CreateBuilder(args);
    // ... wire up your resources ...
    app = builder.Build();
    await app.StartAsync();   // dashboard + OTLP ports are bound by the time this returns
}                             // lock released here, after startup
await app.WaitForShutdownAsync();
```

That's it — every instance now gets its own dashboard and OTLP ports.

**Why the split instead of `app.Run()`?** The lock has to outlive port-binding but *not* the whole
session. `StartAsync()` returns once the host has bound its ports; releasing the lock then lets a
waiting sibling proceed, while `WaitForShutdownAsync()` keeps the app running unlocked. A sibling
AppHost blocks on the lock until you exit the `using`, so it always sees your ports as taken and
steps to its own. If a sibling is mid-startup, `ReserveAsync` waits and logs a heartbeat every 5
seconds so you can tell it's alive, not hung.

### Extra ports

Need pinned host ports for your own resources (Redis, RabbitMQ, …)? Pass the count — there's an
overload that takes just the number of extra ports — and read them back in order:

```csharp
using (var ports = await AspirePortReservation.ReserveAsync(2))   // 2 extra ports
{
    var builder = DistributedApplication.CreateBuilder(args);
    builder.AddRedis("redis", port: ports.ExtraPorts[0]);
    builder.AddRabbitMQ("rabbit", port: ports.ExtraPorts[1]);

    // Aspire logs the dashboard URL itself on startup, but not your extra ports — so log those:
    Console.WriteLine($"redis → {ports.ExtraPorts[0]}, rabbit → {ports.ExtraPorts[1]}");

    app = builder.Build();
    await app.StartAsync();
}   // (app declared before the using, as in the Usage example above)
```

For anything beyond the count (a custom `LockName`, different bases, pinned mode) use the
`ReserveAsync(o => …)` overload instead — see [Options](#options).

#### One port, one resource

`ExtraPorts` is a plain list — reading an element has no side effect, so **each port is yours to
assign exactly once**. If you pin the *same* port to two resources:

```csharp
builder.AddRedis("redis",     port: ports.ExtraPorts[0]);
builder.AddRabbitMQ("rabbit", port: ports.ExtraPorts[0]);   // same port — don't
```

…the first binds and the **second container fails to start** with a Docker bind error
(`port is already allocated`), shown as failed in the dashboard. Aspire does **not** silently
reassign — pinning a host port means exactly that port. (If you wanted auto-assignment, omit the
`port:` argument and don't use `ExtraPorts` at all.) Reading past what you reserved —
`ports.ExtraPorts[2]` when `ExtraPortCount` was `2` — throws `ArgumentOutOfRangeException`.

### Options

All optional:

| Option | Default | Purpose |
| --- | --- | --- |
| `LockName` | calling assembly name | Coordinates only with *your* AppHost's siblings (a lock file under temp), not unrelated apps. |
| `DashboardBase` | `16000` | First port probed for the dashboard frontend. |
| `OtlpBase` | `19000` | First port of the OTLP block (gRPC, HTTP, resource-service, then extras). |
| `ExtraPortCount` | `0` | Extra ports to reserve beyond Aspire's own. |
| `OffsetEnvironmentVariable` | `null` | Env var holding an integer offset; when set, ports are pinned to `base + offset` deterministically — no lock, no probing. |
| `Logger` | `Console.WriteLine` | Where the every-5-seconds "still waiting for the lock" heartbeat goes. |

```csharp
var ports = await AspirePortReservation.ReserveAsync(o =>
{
    o.LockName = "MyApp-Aspire-Ports";
    o.DashboardBase = 16036;
    o.OtlpBase = 19600;
    o.ExtraPortCount = 1;
    o.OffsetEnvironmentVariable = "MYAPP_PORT_OFFSET";
});
```

Set `MYAPP_PORT_OFFSET=10` to pin every port to its base + 10 — deterministic and lock-free, handy
for stable, reproducible ports in scripts or CI.

## License

[MIT](license.txt)
