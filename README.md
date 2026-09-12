# MeddlingIdiot.Greenlight.Sdk

Read live build and pull-request status from the [Greenlight](https://github.com/meddlingidiot/MeddlingIdiot.Greenlight)
running on the same machine, and react to it — so your own desktop app can go red when a
pipeline breaks, without talking to Azure DevOps or GitHub itself and without holding a
token of its own.

```csharp
await using var greenlight = new GreenlightClient();

greenlight.Changed += (_, e) => Paint(e.Snapshot.Status, e.Snapshot.IsBuilding);
greenlight.AvailabilityChanged += (_, e) => ShowConnectionState(e.Availability);

await greenlight.StartAsync();
```

## Greenlight not running is a normal state, not an error

Nothing in this package throws because Greenlight is absent. An app that starts before
Greenlight, runs while Greenlight updates and restarts underneath it, and outlives it, sees
`Unavailable → Connected → Unavailable → Connected` and nothing else. There is no ordering
to respect and nothing to retry by hand.

| `Availability` | What it means | What to show |
|---|---|---|
| `Unavailable` | No Greenlight is serving the local API | A disabled state. It reconnects on its own. |
| `Connecting` | An attempt is in flight | Same as above |
| `Connected` | Attached, and `Current` is live | The real thing |
| `Disabled` | Greenlight is running, but the user switched the local API off | Say so — this one they can fix, in Greenlight's settings |
| `Incompatible` | The host speaks a protocol version this package does not | One of the two needs updating |

`Current` is null unless `Availability` is `Connected`. Commands return a `CommandResult`
rather than throwing.

> **Events are raised on a background thread.** Marshal to your UI thread before touching
> anything the UI owns — this is the easiest thing to get wrong.

## What a snapshot carries

```csharp
var snapshot = greenlight.Current;          // null when nothing is attached

snapshot.Status;            // Unknown | Green | Yellow | Red — Greenlight's own verdict
snapshot.IsBuilding;        // a build is running right now; flash rather than recolour
snapshot.Reason;            // "2 pipelines' last completed builds failed."
snapshot.Details;           // the specific builds or PRs responsible
snapshot.Builds;            // every retained run: pipeline, branch, result, times, URL
snapshot.PullRequests;      // including IsReviewRequestedFromMe, the flag that turns it yellow
snapshot.Connections;       // configured remotes, and whether each one's last poll worked
```

`Status` follows the rule Greenlight's own lamps use: a broken pipeline stays red while it
rebuilds, and "something is happening" is said by `IsBuilding`, not by the colour.
`Reason` is Greenlight's own reasoning, so you never have to re-derive its rules.

Tokens, credentials, licence details and organization URLs are never sent.

## Commands

Subject to the user's settings — they can allow reading but not acting.

```csharp
await greenlight.RefreshNowAsync();                     // poll the remotes now
await greenlight.AcknowledgeBuildsAsync([12345]);       // dismiss runs
await greenlight.AcknowledgePipelineAsync("Web", "CI"); // dismiss a whole pipeline
await greenlight.ShowDashboardAsync();                  // surface Greenlight's window
```

Check `greenlight.SupportedCommands` to grey a button out rather than discovering the
refusal on click. A `CommandResult` that is not `IsOk` carries an `Error` worth matching on:

| `Error` | Meaning |
|---|---|
| `commands_disabled` | The user allows reading only |
| `rate_limited` | `refresh_now` has a five-second floor shared by every attached app. Not worth showing a user. |
| `unknown_command` | This Greenlight does not have it |
| `bad_request` | The arguments were wrong |

Nothing here can add a connection, touch a token, or change a setting.

## Other shapes

```csharp
// A stream instead of an event. Yields the current snapshot first, then each new one,
// and goes quiet — rather than ending — while Greenlight is away.
await foreach (var snapshot in greenlight.WatchAsync(cancellationToken))
    Paint(snapshot.Status);

// A one-shot probe. For anything ongoing, start a client and watch Availability.
if (GreenlightClient.IsRunning()) { }
```

With dependency injection, register it yourself — this package deliberately has no
dependencies:

```csharp
services.AddSingleton<IGreenlightStatus>(_ => new GreenlightClient());
// then StartAsync it once at startup, and DisposeAsync on shutdown
```

## How it connects

A named pipe, per Windows account. Nothing listens on a network port, no firewall prompt
appears, and the pipe's ACL admits only the account Greenlight runs as — so this can only
ever reach *your* Greenlight on *this* machine. The user can switch the whole thing off,
or restrict it to reading, in Greenlight's settings.

Targets `net8.0` and `net10.0`. .NET Framework is not supported.

## A worked example

[CarsClient](https://github.com/meddlingidiot/MeddlingIdiot.Greenlight.CarsClient) is a
complete app built on this package: little cars that drive along your taskbar, flow on
green, crawl and honk on yellow, and pile up at a red light when a pipeline breaks. Strip
out the drawing and the tray icon and the integration is about twenty lines.

## Licence

MIT — see [LICENSE](LICENSE). Greenlight itself is a separate product and is not open
source; this package is the client half of its local API, and it is open so that anything
can be built against it.
