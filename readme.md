# Kalicz.Aspire

[![build](https://github.com/KaliCZ/Kalicz.Aspire/actions/workflows/build.yml/badge.svg)](https://github.com/KaliCZ/Kalicz.Aspire/actions/workflows/build.yml)
[![NuGet](https://img.shields.io/nuget/v/Kalicz.Aspire.svg)](https://www.nuget.org/packages/Kalicz.Aspire)

Parallel-safe local ports for [.NET Aspire](https://learn.microsoft.com/dotnet/aspire/) AppHosts.

Running more than one AppHost at once — say, one per git worktree — fails because they all
grab the same fixed dashboard / OTLP / resource-service ports. `Kalicz.Aspire` reserves a free
set of ports for each instance under a machine-wide mutex, points Aspire at them via the standard
env vars, and holds them until the host binds them — so siblings never collide.

## Install

```sh
dotnet add package Kalicz.Aspire
```

## Usage

Reserve at the very top of your AppHost, release right before `app.Run()`:

```csharp
using Kalicz.Aspire;

var ports = AspirePortReservation.Reserve();

var builder = DistributedApplication.CreateBuilder(args);
// ... wire up your resources ...

var app = builder.Build();
ports.ReleaseForStartup();   // free the reserved ports right before the host binds them
app.Run();
```

That's it — every instance now gets its own dashboard and OTLP ports.

### Extra ports

Need pinned host ports for your own resources (Redis, RabbitMQ, …)? Ask for extras and read
them back in order:

```csharp
var ports = AspirePortReservation.Reserve(o => o.ExtraPortCount = 2);

builder.AddRedis("redis").WithHostPort(ports.ExtraPorts[0]);
builder.AddRabbitMQ("rabbit").WithHostPort(ports.ExtraPorts[1]);
```

### Options

All optional:

| Option | Default | Purpose |
| --- | --- | --- |
| `MutexName` | calling assembly name | Coordinates only with *your* AppHost's siblings, not unrelated apps. |
| `DashboardBase` | `16000` | First port scanned for the dashboard frontend. |
| `OtlpBase` | `19000` | First port of the OTLP block (gRPC, HTTP, resource-service, then extras). |
| `ExtraPortCount` | `0` | Extra ports to reserve beyond Aspire's own. |
| `OffsetEnvironmentVariable` | `null` | Name of an env var holding an integer offset; when set, ports are pinned to `base + offset` deterministically (no scanning). |

```csharp
var ports = AspirePortReservation.Reserve(o =>
{
    o.MutexName = "MyApp-Aspire-Ports";
    o.DashboardBase = 16036;
    o.OtlpBase = 19600;
    o.ExtraPortCount = 1;
    o.OffsetEnvironmentVariable = "MYAPP_PORT_OFFSET";
});
```

Set `MYAPP_PORT_OFFSET=10` to pin every port to its base + 10 — handy for stable, reproducible
ports in scripts or CI.

## License

[MIT](license.txt)
