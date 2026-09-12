using Greenlight.Sdk;
using Greenlight.Sdk.Protocol;

namespace Greenlight.Sdk.UnitTests;

/// <summary>
/// The client's state machine, driven over a scripted transport.
/// </summary>
/// <remarks>
/// The theme running through these: <b>an absent Greenlight is a normal operating mode.</b>
/// A consumer app has to work when Greenlight was never installed, when it is mid-restart
/// under a Velopack update, and when the user has switched the local API off — without
/// catching anything and without being started in a particular order.
/// </remarks>
public class GreenlightClientTests
{
    private static GreenlightSnapshot Snapshot(GreenlightStatus status, bool building = false) => new(
        status, building, $"{status}.", [], [], [], [], DateTimeOffset.UnixEpoch, "1.0.0-test");

    [Fact]
    public async Task With_no_Greenlight_running_it_reports_unavailable_and_throws_nothing()
    {
        var transport = new FakeGreenlightTransport();   // nothing scripted: nothing listening
        await using var client = new GreenlightClient(TestTiming.FastOptions(transport));

        await client.StartAsync();

        await TestTiming.Until(() => transport.Attempts >= 2, "the client has retried");
        Assert.Equal(GreenlightAvailability.Unavailable, client.Availability);
        Assert.Null(client.Current);
    }

    [Fact]
    public async Task Commands_are_refused_without_throwing_when_nothing_is_attached()
    {
        var transport = new FakeGreenlightTransport();
        await using var client = new GreenlightClient(TestTiming.FastOptions(transport));
        await client.StartAsync();

        // No try/catch anywhere — if a consumer had to wrap these, the API would be wrong.
        Assert.Equal(CommandOutcome.Unavailable, (await client.RefreshNowAsync()).Outcome);
        Assert.Equal(CommandOutcome.Unavailable, (await client.ShowDashboardAsync()).Outcome);
        Assert.Equal(CommandOutcome.Unavailable, (await client.AcknowledgeBuildsAsync([1])).Outcome);
        Assert.Equal(CommandOutcome.Unavailable, (await client.AcknowledgePipelineAsync("p", "ci")).Outcome);
    }

    [Fact]
    public async Task It_attaches_and_surfaces_the_first_snapshot()
    {
        var transport = new FakeGreenlightTransport();
        var host = transport.Expect();
        await using var client = new GreenlightClient(TestTiming.FastOptions(transport));

        GreenlightSnapshot? raised = null;
        client.Changed += (_, e) => raised = e.Snapshot;

        await client.StartAsync();
        host.SendHello(app: "9.9.9");
        host.SendSnapshot(Snapshot(GreenlightStatus.Red, building: true));

        // Waits on the event, not on Current: the client sets Current first and raises
        // Changed second — on purpose, so a handler reading Current sees the snapshot it was
        // just told about — which leaves a window where Current is set and `raised` is not.
        await TestTiming.Until(() => raised is not null, "the first snapshot arrives");

        Assert.Equal(GreenlightAvailability.Connected, client.Availability);
        Assert.Equal(GreenlightStatus.Red, client.Current!.Status);
        Assert.True(client.Current.IsBuilding);
        Assert.Equal("9.9.9", client.HostVersion);
        Assert.Equal(raised, client.Current);
    }

    [Fact]
    public async Task It_reports_only_the_commands_the_host_offers()
    {
        var transport = new FakeGreenlightTransport();
        var host = transport.Expect();
        await using var client = new GreenlightClient(TestTiming.FastOptions(transport));

        await client.StartAsync();
        host.SendHello(GreenlightProtocol.Version, "1.0.0-test", CommandName.RefreshNow);

        await TestTiming.Until(() => client.Availability == GreenlightAvailability.Connected, "attached");

        // What a consumer greys a button on, rather than waiting for the refusal.
        Assert.Equal([CommandName.RefreshNow], client.SupportedCommands);
    }

    [Fact]
    public async Task A_protocol_version_it_cannot_speak_is_reported_as_incompatible()
    {
        var transport = new FakeGreenlightTransport();
        var host = transport.Expect();
        await using var client = new GreenlightClient(TestTiming.FastOptions(transport));

        await client.StartAsync();
        host.SendHello(version: GreenlightProtocol.Version + 1);

        await TestTiming.Until(() => client.Availability == GreenlightAvailability.Incompatible,
            "the mismatch is reported");

        // Guessing at a format we do not know would be worse than saying so.
        Assert.Null(client.Current);
    }

    [Fact]
    public async Task A_host_that_says_nothing_is_abandoned_rather_than_waited_on_forever()
    {
        var transport = new FakeGreenlightTransport();
        transport.Expect();                              // connects, never sends hello
        await using var client = new GreenlightClient(TestTiming.FastOptions(transport));

        await client.StartAsync();

        await TestTiming.Until(() => transport.Attempts >= 2, "the handshake times out and it retries");
        Assert.Equal(GreenlightAvailability.Unavailable, client.Availability);
    }

    [Fact]
    public async Task When_the_host_disappears_it_reconnects_on_its_own()
    {
        var transport = new FakeGreenlightTransport();
        var first = transport.Expect();
        var second = transport.Expect();
        await using var client = new GreenlightClient(TestTiming.FastOptions(transport));

        List<GreenlightAvailability> seen = [];
        client.AvailabilityChanged += (_, e) => { lock (seen) seen.Add(e.Availability); };

        await client.StartAsync();
        first.SendHello();
        first.SendSnapshot(Snapshot(GreenlightStatus.Green));
        await TestTiming.Until(() => client.Current?.Status == GreenlightStatus.Green, "the first snapshot arrives");

        // Greenlight restarting under a Velopack update looks exactly like this.
        first.Drop();

        second.SendHello();
        second.SendSnapshot(Snapshot(GreenlightStatus.Yellow));
        await TestTiming.Until(() => client.Current?.Status == GreenlightStatus.Yellow, "it reattaches");

        Assert.Equal(GreenlightAvailability.Connected, client.Availability);
        lock (seen) Assert.Contains(GreenlightAvailability.Connected, seen);
    }

    [Fact]
    public async Task Losing_the_host_clears_the_snapshot_rather_than_leaving_stale_state_on_screen()
    {
        var transport = new FakeGreenlightTransport();
        var host = transport.Expect();
        await using var client = new GreenlightClient(TestTiming.FastOptions(transport));

        await client.StartAsync();
        host.SendHello();
        host.SendSnapshot(Snapshot(GreenlightStatus.Green));
        await TestTiming.Until(() => client.Current is not null, "attached with data");

        host.Drop();

        // A consumer showing green from four minutes ago, for a Greenlight that is gone, is
        // worse than one showing nothing.
        await TestTiming.Until(() => client.Current is null, "the stale snapshot is dropped");
        Assert.Equal(GreenlightAvailability.Unavailable, client.Availability);
    }

    [Fact]
    public async Task A_host_that_says_the_api_is_switched_off_is_distinguished_from_one_that_is_absent()
    {
        var transport = new FakeGreenlightTransport();
        var host = transport.Expect();
        await using var client = new GreenlightClient(TestTiming.FastOptions(transport));

        await client.StartAsync();
        host.SendHello();
        await TestTiming.Until(() => client.Availability == GreenlightAvailability.Connected, "attached");

        host.SendByeAndDrop(ByeReason.Disabled);

        // The pipe is gone from here on, so every retry fails — but "the user turned this
        // off" is something they can fix, and it must not decay into a plain "not running".
        await TestTiming.Until(() => client.Availability == GreenlightAvailability.Disabled, "it reports disabled");
        await TestTiming.Until(() => transport.Attempts >= 3, "it has kept retrying");
        Assert.Equal(GreenlightAvailability.Disabled, client.Availability);
    }

    [Fact]
    public async Task A_shutdown_goodbye_is_plain_unavailability()
    {
        var transport = new FakeGreenlightTransport();
        var host = transport.Expect();
        await using var client = new GreenlightClient(TestTiming.FastOptions(transport));

        await client.StartAsync();
        host.SendHello();
        await TestTiming.Until(() => client.Availability == GreenlightAvailability.Connected, "attached");

        host.SendByeAndDrop(ByeReason.Shutdown);

        await TestTiming.Until(() => client.Availability == GreenlightAvailability.Unavailable, "it reports unavailable");
    }

    [Fact]
    public async Task Reattaching_clears_a_previous_disabled_state()
    {
        var transport = new FakeGreenlightTransport();
        var first = transport.Expect();
        var second = transport.Expect();
        await using var client = new GreenlightClient(TestTiming.FastOptions(transport));

        await client.StartAsync();
        first.SendHello();
        await TestTiming.Until(() => client.Availability == GreenlightAvailability.Connected, "attached");
        first.SendByeAndDrop(ByeReason.Disabled);
        await TestTiming.Until(() => client.Availability == GreenlightAvailability.Disabled, "it reports disabled");

        second.SendHello();
        second.SendSnapshot(Snapshot(GreenlightStatus.Green));

        await TestTiming.Until(() => client.Current?.Status == GreenlightStatus.Green, "the user switched it back on");
        Assert.Equal(GreenlightAvailability.Connected, client.Availability);
    }

    [Fact]
    public async Task Junk_on_the_wire_is_skipped_and_the_session_survives_it()
    {
        var transport = new FakeGreenlightTransport();
        var host = transport.Expect();
        await using var client = new GreenlightClient(TestTiming.FastOptions(transport));

        await client.StartAsync();
        host.SendHello();
        await TestTiming.Until(() => client.Availability == GreenlightAvailability.Connected, "attached");

        host.Send("not json");
        host.Send("{\"t\":\"invented_later\",\"payload\":{}}");   // a newer host's new message
        host.Send("{\"t\":\"snapshot\",\"seq\":");                // truncated mid-flush
        host.SendSnapshot(Snapshot(GreenlightStatus.Yellow));

        await TestTiming.Until(() => client.Current?.Status == GreenlightStatus.Yellow,
            "the good snapshot after the junk still lands");
        Assert.Equal(GreenlightAvailability.Connected, client.Availability);
    }

    [Fact]
    public async Task A_command_is_sent_and_its_acknowledgement_returned()
    {
        var transport = new FakeGreenlightTransport();
        var host = transport.Expect();
        await using var client = new GreenlightClient(TestTiming.FastOptions(transport));

        await client.StartAsync();
        host.SendHello();
        await TestTiming.Until(() => client.Availability == GreenlightAvailability.Connected, "attached");

        var pending = client.AcknowledgeBuildsAsync([11, 22]);

        var command = await host.ReceiveCommandAsync();
        Assert.Equal(CommandName.AcknowledgeBuilds, command.Name);
        Assert.Equal([11L, 22L], command.Args!.BuildIds!);

        host.Send(new AckMessage(command.Id, true));

        Assert.True((await pending).IsOk);
    }

    [Fact]
    public async Task A_refusal_comes_back_with_the_reason_the_host_gave()
    {
        var transport = new FakeGreenlightTransport();
        var host = transport.Expect();
        await using var client = new GreenlightClient(TestTiming.FastOptions(transport));

        await client.StartAsync();
        host.SendHello();
        await TestTiming.Until(() => client.Availability == GreenlightAvailability.Connected, "attached");

        var pending = client.RefreshNowAsync();
        var command = await host.ReceiveCommandAsync();
        host.Send(new AckMessage(command.Id, false, CommandError.RateLimited, "Polled 2s ago."));

        var result = await pending;

        Assert.Equal(CommandOutcome.Refused, result.Outcome);
        // Consumers match on the code; the message is for a log or a tooltip.
        Assert.Equal(CommandError.RateLimited, result.Error);
        Assert.Equal("Polled 2s ago.", result.Message);
    }

    [Fact]
    public async Task Two_commands_in_flight_get_their_own_answers()
    {
        var transport = new FakeGreenlightTransport();
        var host = transport.Expect();
        await using var client = new GreenlightClient(TestTiming.FastOptions(transport));

        await client.StartAsync();
        host.SendHello();
        await TestTiming.Until(() => client.Availability == GreenlightAvailability.Connected, "attached");

        var refresh = client.RefreshNowAsync();
        var show = client.ShowDashboardAsync();

        var first = await host.ReceiveCommandAsync();
        var second = await host.ReceiveCommandAsync();
        Assert.NotEqual(first.Id, second.Id);

        // Answered out of order on purpose: correlation is by id, not by arrival.
        host.Send(new AckMessage(second.Id, false, CommandError.Failed, "no"));
        host.Send(new AckMessage(first.Id, true));

        var byName = new Dictionary<string, Task<CommandResult>>
        {
            [CommandName.RefreshNow] = refresh,
            [CommandName.ShowDashboard] = show,
        };

        Assert.True((await byName[first.Name]).IsOk);
        Assert.Equal(CommandOutcome.Refused, (await byName[second.Name]).Outcome);
    }

    [Fact]
    public async Task A_command_whose_host_dies_mid_flight_reports_unavailable_not_a_timeout()
    {
        var transport = new FakeGreenlightTransport();
        var host = transport.Expect();
        await using var client = new GreenlightClient(TestTiming.FastOptions(transport));

        await client.StartAsync();
        host.SendHello();
        await TestTiming.Until(() => client.Availability == GreenlightAvailability.Connected, "attached");

        var pending = client.RefreshNowAsync();
        await host.ReceiveCommandAsync();
        host.Drop();                                     // never answers

        // The distinction matters to a caller: "Greenlight went away" is retryable in a way
        // "Greenlight is ignoring me" is not, and waiting out the full command timeout to
        // learn the connection had already gone would be a needless stall.
        Assert.Equal(CommandOutcome.Unavailable, (await pending).Outcome);
    }

    [Fact]
    public async Task A_command_that_is_never_answered_times_out()
    {
        var transport = new FakeGreenlightTransport();
        var host = transport.Expect();
        await using var client = new GreenlightClient(TestTiming.FastOptions(transport));

        await client.StartAsync();
        host.SendHello();
        await TestTiming.Until(() => client.Availability == GreenlightAvailability.Connected, "attached");

        var result = await client.RefreshNowAsync();

        Assert.Equal(CommandOutcome.TimedOut, result.Outcome);
    }

    [Fact]
    public async Task Acknowledging_nothing_asks_the_host_nothing()
    {
        var transport = new FakeGreenlightTransport();
        var host = transport.Expect();
        await using var client = new GreenlightClient(TestTiming.FastOptions(transport));

        await client.StartAsync();
        host.SendHello();
        await TestTiming.Until(() => client.Availability == GreenlightAvailability.Connected, "attached");

        Assert.True((await client.AcknowledgeBuildsAsync([])).IsOk);

        var sentAnything = client.RefreshNowAsync();
        // The next thing the host sees is the refresh, not an empty acknowledgement.
        Assert.Equal(CommandName.RefreshNow, (await host.ReceiveCommandAsync()).Name);
        await sentAnything;
    }

    [Fact]
    public async Task Watch_yields_the_current_snapshot_first_then_each_new_one()
    {
        var transport = new FakeGreenlightTransport();
        var host = transport.Expect();
        await using var client = new GreenlightClient(TestTiming.FastOptions(transport));

        await client.StartAsync();
        host.SendHello();
        host.SendSnapshot(Snapshot(GreenlightStatus.Green));
        await TestTiming.Until(() => client.Current is not null, "attached with data");

        using var stop = new CancellationTokenSource(TestTiming.Timeout);
        List<GreenlightStatus> seen = [];

        var watching = Task.Run(async () =>
        {
            await foreach (var snapshot in client.WatchAsync(stop.Token))
            {
                seen.Add(snapshot.Status);
                if (seen.Count == 2) return;
            }
        }, stop.Token);

        await TestTiming.Until(() => seen.Count == 1, "the watcher catches up with the current state");
        host.SendSnapshot(Snapshot(GreenlightStatus.Red), seq: 2);
        await watching;

        Assert.Equal([GreenlightStatus.Green, GreenlightStatus.Red], seen);
    }

    [Fact]
    public async Task Disposing_stops_the_reconnect_loop()
    {
        var transport = new FakeGreenlightTransport();
        var client = new GreenlightClient(TestTiming.FastOptions(transport));

        await client.StartAsync();
        await TestTiming.Until(() => transport.Attempts >= 2, "the loop is running");

        await client.DisposeAsync();
        var attemptsAtDispose = transport.Attempts;

        await Task.Delay(100);

        Assert.Equal(attemptsAtDispose, transport.Attempts);
        Assert.Equal(GreenlightAvailability.Unavailable, client.Availability);
    }

    [Fact]
    public async Task Starting_twice_is_a_programming_error_and_says_so()
    {
        var transport = new FakeGreenlightTransport();
        await using var client = new GreenlightClient(TestTiming.FastOptions(transport));
        await client.StartAsync();

        // Unlike an absent Greenlight, this one is the consumer's bug, so it throws.
        Assert.Throws<InvalidOperationException>(() => client.StartAsync().GetAwaiter().GetResult());
    }
}
