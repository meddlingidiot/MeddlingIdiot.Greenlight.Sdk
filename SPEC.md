# Build a Greenlight client

Greenlight watches your Azure DevOps and GitHub pipelines and pull requests, and shows one
colour. This is the contract for reading that colour out of it, so your own app can react —
without talking to Azure DevOps or GitHub itself, and without holding a token of its own.

Greenlight does the polling, the authentication and the reasoning. You get the verdict.

- **Package:** `MeddlingIdiot.Greenlight.Sdk` (MIT, no dependencies, `net8.0`/`net10.0`)
- **Transport:** a named pipe, per Windows account, on this machine only
- **Protocol version:** 1
- **Source of truth:** this file, in [the SDK repo](https://github.com/meddlingidiot/MeddlingIdiot.Greenlight.Sdk)

If you are writing .NET, use the SDK — [the complete client](#a-complete-client) below is the
whole integration. [Appendix A](#appendix-a--the-wire-protocol) documents the pipe itself, for
any other language.

---

## A complete client

Top to bottom. A console app, because it fits on a page; the integration is identical in a
tray app or a game.

```csharp
// dotnet new console && dotnet add package MeddlingIdiot.Greenlight.Sdk
using Greenlight.Sdk;
using Greenlight.Sdk.Protocol;

await using var greenlight = new GreenlightClient();

greenlight.AvailabilityChanged += (_, e) => Console.WriteLine(e.Availability switch
{
    GreenlightAvailability.Connected    => "attached",
    GreenlightAvailability.Disabled     => "Greenlight is running, but its local API is switched off",
    GreenlightAvailability.Incompatible => "this client and that Greenlight speak different protocol versions",
    _                                   => "waiting for Greenlight",   // Unavailable, Connecting
});

greenlight.Changed += (_, e) => Paint(e.Snapshot);

await greenlight.StartAsync();      // returns at once, and never throws if Greenlight is absent

Console.WriteLine("Running. Ctrl+C to stop.");
await Task.Delay(Timeout.Infinite);

static void Paint(GreenlightSnapshot s)
{
    Console.ForegroundColor = s.Status switch
    {
        GreenlightStatus.Green  => ConsoleColor.Green,
        GreenlightStatus.Yellow => ConsoleColor.Yellow,
        GreenlightStatus.Red    => ConsoleColor.Red,
        _                       => ConsoleColor.DarkGray,       // Unknown
    };
    Console.WriteLine($"{s.Status}{(s.IsBuilding ? " (building)" : "")} — {s.Reason}");
    Console.ResetColor();
}
```

That is the entire contract exercised: construct, subscribe, start. There is no connect step to
sequence, no retry to write, and no error path for "Greenlight isn't running".

Prefer a loop to events? `WatchAsync` yields the current snapshot first, then each new one, and
goes **quiet rather than ending** while Greenlight is away:

```csharp
await foreach (var snapshot in greenlight.WatchAsync(cancellationToken))
    Paint(snapshot);
```

---

## The four states

`snapshot.Status` is the aggregate colour every Greenlight indicator is wearing — tray icon,
header dot, hardware lamps, and now you.

| `GreenlightStatus` | Means |
|---|---|
| `Unknown` | Nothing polled yet, or every connection's last poll failed. Greenlight's grey. |
| `Green` | Every pipeline's last completed run passed, and no PR is waiting on you. |
| `Yellow` | A build is queued, a pipeline has no result yet, or a PR wants your review. |
| `Red` | At least one pipeline's last completed build failed. |

Two rules worth copying rather than reinventing:

**A broken pipeline stays `Red` while it rebuilds.** It does not go hopefully yellow. "Something
is happening right now" is said by `snapshot.IsBuilding`, which is independent of the colour —
so flash the colour you have, don't change it.

**`snapshot.Reason` is Greenlight's own reasoning, in plain English** — *"2 pipelines' last
completed builds failed."* — with `snapshot.Details` giving a line per item responsible. Show
those instead of re-deriving why. When Greenlight's rules change, your app is already right.

---

## Availability: Greenlight being absent is a normal state

Nothing in the SDK throws because Greenlight is not there. An app that starts before Greenlight,
runs while Velopack updates and restarts it underneath, and outlives it, sees
`Unavailable → Connected → Unavailable → Connected` and nothing else. The reconnect loop backs
off 1 s → 30 s and retries forever until you stop it.

| `GreenlightAvailability` | What it means | What to show |
|---|---|---|
| `Unavailable` | No Greenlight is serving the local API | A disabled state. It reconnects on its own. |
| `Connecting` | An attempt is in flight | Same as above |
| `Connected` | Attached, and `Current` is live | The real thing |
| `Disabled` | Greenlight is running, but the user switched the local API off | Say so — **this one they can fix**, in Greenlight's settings |
| `Incompatible` | The host speaks a protocol version this SDK does not | One of the two needs updating |

`Current` is null unless `Availability` is `Connected`. `GreenlightSnapshot.Empty` exists for
binding a UI before anything has arrived.

`Disabled` is best-effort: it is inferred from the host's parting message, so a Greenlight that
was never reachable in the first place reads as `Unavailable`. Once the toggle is off the pipe is
gone, so the state survives every later failed attempt rather than decaying to `Unavailable`.

---

## What a snapshot carries

```csharp
public sealed record GreenlightSnapshot(
    GreenlightStatus Status,                            // Unknown | Green | Yellow | Red
    bool IsBuilding,                                    // something is running right now
    string Reason,                                      // "2 pipelines' last completed builds failed."
    IReadOnlyList<string> Details,                      // one line per item responsible, newest first
    IReadOnlyList<GreenlightBuild> Builds,              // every run in the retention window
    IReadOnlyList<GreenlightPullRequest> PullRequests,
    IReadOnlyList<GreenlightConnection> Connections,    // configured remotes, and whether each is healthy
    DateTimeOffset CapturedAt,
    string AppVersion);

public sealed record GreenlightBuild(
    long Id, string Pipeline, string Project, string Branch,
    GreenlightBuildStatus Status,           // Unknown | NotStarted | InProgress | Cancelling | Completed
    GreenlightBuildResult Result,           // Unknown | Succeeded | PartiallySucceeded | Failed | Cancelled
    DateTimeOffset QueuedAt, DateTimeOffset? FinishedAt,
    string TriggeredBy, string WebUrl,
    GreenlightProvider Provider,            // Unknown | AzureDevOps | GitHub
    bool IsAcknowledged);                   // dismissed in Greenlight — usually hide these too

public sealed record GreenlightPullRequest(
    int Id, string Title, string Repo, string Project, string Author,
    string SourceBranch, string TargetBranch,
    GreenlightPullRequestStatus Status,     // Unknown | Active | Completed | Abandoned
    GreenlightReviewStatus MyReviewStatus,  // NoVote | Approved | ApprovedWithSuggestions | WaitingForAuthor | Rejected
    bool IsReviewRequestedFromMe,           // the flag that turns the aggregate colour yellow
    DateTimeOffset CreatedAt, string WebUrl, GreenlightProvider Provider);

public sealed record GreenlightConnection(Guid Id, string Name, GreenlightProvider Provider, bool Healthy);
```

`Result` is only meaningful once `Status` is `Completed`. Acknowledged runs stop holding the
colour red, so an app drawing its own list usually hides them.

**Never crosses the pipe:** personal access tokens, credential-manager keys, licence details,
organization URLs. A `GreenlightConnection` carries a name and a health flag and nothing else —
enough to tell a user part of the picture is missing, and which remote is responsible. A test in
Greenlight asserts the serialized snapshot contains no field whose name matches the
secret-bearing set.

---

## Commands

You can nudge Greenlight. You can never reconfigure it — there is no command that adds a
connection, touches a token, or changes a setting.

```csharp
await greenlight.RefreshNowAsync();                      // poll every remote now
await greenlight.AcknowledgeBuildsAsync([12345, 12346]); // dismiss specific runs
await greenlight.AcknowledgePipelineAsync("Web", "CI");  // dismiss a whole pipeline
await greenlight.ShowDashboardAsync();                   // surface Greenlight's own window
await greenlight.HoldIndicatorsAsync(GreenlightStatus.Red, building: true); // a drill
await greenlight.ReleaseIndicatorsAsync();               // back to the real build state
```

`HoldIndicatorsAsync` is the developer page's buttons, over the pipe: every indicator
Greenlight drives — tray, desktop stoplight, hardware lamps, every attached app including
yours — holds at the colour and/or blinks as though a build were running, until released.
That is how you photograph your client red without breaking a build. Three things to know:

- The snapshot you get back says so — `Reason` reads *"Held at Red by your-app."* — so
  nothing downstream can mistake the drill for the real thing.
- **The host drops the hold when your connection ends.** Crash mid-drill and the user's light
  goes back to telling the truth.
- `GreenlightStatus.Unknown` is the grey "off"; `status: null` holds only the blink.

Commands return a `CommandResult` rather than throwing:

```csharp
public sealed record CommandResult(CommandOutcome Outcome, string? Error = null, string? Message = null)
{
    public bool IsOk => Outcome == CommandOutcome.Ok;
}

public enum CommandOutcome { Ok, Unavailable, Refused, TimedOut }
```

The user can allow reading but not acting. **Check `greenlight.SupportedCommands`** — the host
advertises exactly what it will accept — and grey the button out, rather than discovering the
refusal on click. On `Refused`, match on `Error`, never on `Message`:

| `Error` | Meaning |
|---|---|
| `commands_disabled` | The user allows reading only |
| `rate_limited` | `refresh_now` has a five-second floor **shared by every attached app**. Not worth showing a user. |
| `unknown_command` | This Greenlight does not have it — check `SupportedCommands` |
| `bad_request` | The arguments were missing or malformed |
| `failed` | Understood, and went wrong anyway |

The rate limit is deliberate: a rogue client must not be able to hammer Azure DevOps or GitHub
into throttling the user.

---

## The two things that actually catch people

Everything else in this document is descriptive. These two are the ones that produce bugs.

### 1. Your app must be happy with no Greenlight at all

It is not an error, it is not a startup ordering problem, and it is not yours to retry. Do not
write a connect-with-retry loop, do not block startup on `IsRunning()`, and do not treat
`Current == null` as a failure. Render a disabled state and wait — `AvailabilityChanged` will
tell you when to come alive. `GreenlightClient.IsRunning()` exists for a genuine one-shot probe
(an installer check, say); for anything ongoing, start a client and watch `Availability`.

### 2. Events are raised on a background thread

`Changed` and `AvailabilityChanged` both. Marshal before touching anything the UI owns — this is
the single most likely way to get this wrong, and the failure is an exception from deep inside
your UI framework that says nothing about Greenlight.

```csharp
// Avalonia
greenlight.Changed += (_, e) => Dispatcher.UIThread.Post(() => Paint(e.Snapshot));

// WPF
greenlight.Changed += (_, e) => Application.Current.Dispatcher.BeginInvoke(() => Paint(e.Snapshot));

// WinForms
greenlight.Changed += (_, e) => form.BeginInvoke(() => Paint(e.Snapshot));
```

Snapshots are immutable, so a reference you capture stays consistent while newer ones arrive —
posting one to another thread is safe.

---

## Dependency injection

The package has no dependencies on purpose, so there is no `AddGreenlight()`. Register it
yourself:

```csharp
services.AddSingleton<IGreenlightStatus>(_ => new GreenlightClient());
```

Then `StartAsync` it once at startup and `DisposeAsync` on shutdown — an `IHostedService` of
four lines, or your app's existing lifetime hooks. `IGreenlightStatus` is the interface to stub
in your own tests.

## Knobs

Every one has a working default; you are unlikely to need any of them.

| `GreenlightClientOptions` | Default | |
|---|---|---|
| `MinReconnectDelay` | 1 s | First gap after a failed connect; doubles toward the max |
| `MaxReconnectDelay` | 30 s | Greenlight restarting under an update should be picked up in seconds |
| `HandshakeTimeout` | 5 s | How long the host has to say `hello` |
| `CommandTimeout` | 10 s | How long to wait for an ack |
| `PipeName` | the per-user pipe | For tests, against an isolated name |
| `Transport` | the named pipe | For tests, to run the client's state machine with no Greenlight at all |

---

## Starting from something that runs

[CarsClient](https://github.com/meddlingidiot/MeddlingIdiot.Greenlight.CarsClient) (MIT) is a
complete, released app built on this package: little cars that drive along your taskbar, flow on
green, crawl and honk on yellow, and pile up at a red light when a pipeline breaks. It was
vibe-coded in an evening.

Clone it, delete `TrafficCanvas`, `TrafficSimulation` and `DesktopStrip`, and what is left is the
integration — about twenty lines, plus a tray icon. Then draw whatever you like.

---

## Appendix A — the wire protocol

For languages that are not .NET. The SDK is the supported contract; this is the same thing one
level down, and it is stable — the protocol version bumps only for a breaking change.

### Finding the pipe

Named pipes are machine-global, so the account goes in the name, or two Windows users each
running Greenlight would collide:

```
\\.\pipe\greenlight.localapi.v1.{first 8 bytes of sha256(IDENTITY) as lowercase hex}
```

`IDENTITY` is the current user's Windows SID, **upper-cased**, hashed as UTF-8. (The SDK falls
back to `DOMAIN\USERNAME`, upper-cased, where a SID cannot be had — so a non-Windows build still
computes a name — but only Windows has a Greenlight to connect to.)

```python
import hashlib
key  = hashlib.sha256(sid.upper().encode("utf-8")).hexdigest()[:16]
pipe = rf"\\.\pipe\greenlight.localapi.v1.{key}"
```

The name is disambiguation only. What actually keeps other accounts out is the ACL: the server
grants `FullControl` to its own user SID and nothing to anyone else — including administrators.
So this can only ever reach *your* Greenlight on *this* machine. Nothing listens on a network
port and no firewall prompt appears.

Open it byte-mode, read/write, async. A connect failure — no pipe, every instance busy, or a
pipe of that name owned by another account — all mean the same thing: there is no Greenlight
here. Retry with backoff; do not treat it as an error.

### Framing

UTF-8 newline-delimited JSON, `\n`, no BOM. One object per line, every object an envelope with a
`t` discriminator. **A receiver that meets a `t` it does not know must skip that line, not
fail** — that is what lets a newer host add a message without breaking older clients. Same for
an unrecognised field, and an unrecognised enum name reads as that enum's zero member.

Field names are camelCase. Enum **values** are their member name in PascalCase (`"Green"`,
`"AzureDevOps"`), read back case-insensitively; numbers are accepted as a courtesy. Timestamps
are ISO-8601. Null-valued fields are omitted.

| Direction | Line |
|---|---|
| → client | `{"t":"hello","v":1,"app":"1.4.2","commands":["refresh_now","show_dashboard"]}` |
| → client | `{"t":"snapshot","seq":7,"data":{ …GreenlightSnapshot… }}` |
| client → | `{"t":"command","id":"c1","name":"refresh_now"}` |
| client → | `{"t":"command","id":"c2","name":"acknowledge_builds","args":{"buildIds":[12345]}}` |
| client → | `{"t":"command","id":"c3","name":"acknowledge_pipeline","args":{"project":"Web","pipeline":"CI"}}` |
| client → | `{"t":"command","id":"c4","name":"hold_indicators","args":{"status":"Red","building":true}}` |
| client → | `{"t":"command","id":"c5","name":"hold_indicators"}` — no `args` releases the hold |
| → client | `{"t":"ack","id":"c1","ok":true}` |
| → client | `{"t":"ack","id":"c1","ok":false,"error":"rate_limited","message":"…"}` |
| → client | `{"t":"bye","reason":"disabled"}` · `{"t":"bye","reason":"shutdown"}` |

### The handshake

The server speaks first, and the client sends nothing until it has read `hello`. Compare `v`
against the version you implement; on a mismatch, report incompatible and disconnect rather than
guessing at a format you do not know. `app` is for display. `commands` is exactly what this host
will accept right now — a host with commands switched off sends an empty list.

### Snapshots

Pushed on connect and on every state change, debounced 100 ms and deduped against the last one
sent — so an idle poll pushes nothing. **Full snapshots, never deltas:** the payload is tens of
builds and PRs, and delta reconciliation is a bug farm for no measurable gain. `seq` is monotonic
per connection, so you can spot a gap in your own logging.

The server serializes its writes, so a snapshot push never interleaves mid-line with an ack.

### Commands and `bye`

One `ack` comes back per command, always, correlated by `id` — which need only be unique within
the connection. Errors are the same five codes listed [above](#commands).

`bye` is the part worth implementing properly. "The user turned this off" and "the process
vanished" are the same silence on a pipe, and very different things to show a user: `disabled` is
fixable by the user in Greenlight's settings, `shutdown` is not. After `disabled`, keep reporting
disabled while you retry — the pipe is gone, so every subsequent attempt fails, and decaying to
"not running" throws away the one thing the user can act on.

---

## Licence

This document and the SDK are MIT. Greenlight itself is a separate product and is not open
source; this is the client half of its local API, open so that anything can be built against it.
