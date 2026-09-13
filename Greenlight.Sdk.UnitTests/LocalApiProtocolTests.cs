using System.Text.Json;
using Greenlight.Sdk.Protocol;

namespace Greenlight.Sdk.UnitTests;

/// <summary>
/// The wire contract. These tests exist because the protocol is the one part of the SDK
/// that cannot be fixed by shipping a new version — a consumer's copy is already installed
/// on somebody's machine, and it has to keep understanding a newer Greenlight.
/// </summary>
public class LocalApiProtocolTests
{
    private static GreenlightSnapshot SampleSnapshot() => new(
        GreenlightStatus.Red,
        IsBuilding: true,
        Reason: "1 pipeline's last completed build failed.",
        Details: ["Web/CI #42 (main) — Failed"],
        Builds:
        [
            new GreenlightBuild(42, "CI", "Web", "main", GreenlightBuildStatus.Completed,
                GreenlightBuildResult.Failed, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(3),
                "Luke", "https://example/42", GreenlightProvider.AzureDevOps, IsAcknowledged: false),
        ],
        PullRequests:
        [
            new GreenlightPullRequest(7, "Fix the thing", "web", "Web", "Luke", "topic", "main",
                GreenlightPullRequestStatus.Active, GreenlightReviewStatus.WaitingForAuthor,
                IsReviewRequestedFromMe: true, DateTimeOffset.UnixEpoch, "https://example/pr/7",
                GreenlightProvider.GitHub),
        ],
        Connections: [new GreenlightConnection(Guid.Empty, "Work", GreenlightProvider.AzureDevOps, Healthy: true)],
        CapturedAt: DateTimeOffset.UnixEpoch,
        AppVersion: "1.2.3");

    [Fact]
    public void Snapshot_survives_a_round_trip_intact()
    {
        var original = SampleSnapshot();

        var line = GreenlightProtocol.Serialize(new SnapshotMessage(3, original));
        var received = GreenlightProtocol.Deserialize<SnapshotMessage>(line);

        Assert.NotNull(received);
        Assert.Equal(3, received.Seq);

        // Compared as wire text, not with Equals: these are records, but a record's generated
        // Equals compares its IReadOnlyList members by reference, so two snapshots holding
        // identical builds are unequal. Serializing both covers every field — including ones
        // added to the contract later — without relying on equality that does not mean what
        // it looks like it means. (Whatever dedupes snapshots host-side has to compare them
        // the same way, for the same reason.)
        Assert.Equal(line, GreenlightProtocol.Serialize(new SnapshotMessage(3, received.Data)));
        Assert.Equal(original.Builds[0].Id, received.Data.Builds[0].Id);
        Assert.Equal(original.PullRequests[0].MyReviewStatus, received.Data.PullRequests[0].MyReviewStatus);
        Assert.Equal(original.Builds[0].FinishedAt, received.Data.Builds[0].FinishedAt);
    }

    [Theory]
    [InlineData(MessageType.Hello)]
    [InlineData(MessageType.Snapshot)]
    [InlineData(MessageType.Command)]
    [InlineData(MessageType.Ack)]
    [InlineData(MessageType.Bye)]
    public void Every_message_carries_its_own_discriminator(string expected)
    {
        var line = expected switch
        {
            MessageType.Hello => GreenlightProtocol.Serialize(new HelloMessage(1, "1.0.0", [])),
            MessageType.Snapshot => GreenlightProtocol.Serialize(new SnapshotMessage(1, SampleSnapshot())),
            MessageType.Command => GreenlightProtocol.Serialize(new CommandMessage("1", CommandName.RefreshNow)),
            MessageType.Ack => GreenlightProtocol.Serialize(new AckMessage("1", true)),
            _ => GreenlightProtocol.Serialize(new ByeMessage(ByeReason.Shutdown)),
        };

        Assert.Equal(expected, GreenlightProtocol.PeekType(line));
    }

    [Fact]
    public void Command_round_trips_with_its_arguments()
    {
        var line = GreenlightProtocol.Serialize(
            new CommandMessage("c9", CommandName.AcknowledgeBuilds, new CommandArgs(BuildIds: [1, 2, 3])));

        var received = GreenlightProtocol.Deserialize<CommandMessage>(line);

        Assert.Equal("c9", received!.Id);
        Assert.Equal(CommandName.AcknowledgeBuilds, received.Name);
        Assert.Equal([1L, 2L, 3L], received.Args!.BuildIds!);
        Assert.Null(received.Args.Project);
    }

    [Fact]
    public void Hold_indicators_carries_a_colour_and_a_pretend_build_on_the_wire()
    {
        var line = GreenlightProtocol.Serialize(
            new CommandMessage("c10", CommandName.HoldIndicators, new CommandArgs(Status: GreenlightStatus.Red, Building: true)));

        // Spelled out because a non-.NET client writes this by hand from the spec.
        Assert.Contains("\"status\":\"Red\"", line);
        Assert.Contains("\"building\":true", line);

        var received = GreenlightProtocol.Deserialize<CommandMessage>(line);
        Assert.Equal(GreenlightStatus.Red, received!.Args!.Status);
        Assert.True(received.Args.Building);
    }

    [Fact]
    public void Hold_indicators_with_no_arguments_is_a_release()
    {
        var line = GreenlightProtocol.Serialize(new CommandMessage("c11", CommandName.HoldIndicators));
        var received = GreenlightProtocol.Deserialize<CommandMessage>(line);

        Assert.Equal(CommandName.HoldIndicators, received!.Name);
        Assert.True(received.Args is null || (received.Args.Status is null && received.Args.Building is null));
    }

    [Fact]
    public void Messages_are_a_single_line_each()
    {
        // The framing is newline-delimited, so an indented serializer would silently break
        // every reader on both ends.
        var line = GreenlightProtocol.Serialize(new SnapshotMessage(1, SampleSnapshot()));

        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
    }

    [Fact]
    public void Enums_go_over_the_wire_as_names()
    {
        // Numbers would let a reordered enum change meaning silently between versions.
        var line = GreenlightProtocol.Serialize(new SnapshotMessage(1, SampleSnapshot()));

        Assert.Contains("\"Red\"", line);
        Assert.Contains("\"AzureDevOps\"", line);
        Assert.DoesNotContain("\"status\":3", line);
    }

    [Fact]
    public void An_unrecognised_enum_value_reads_as_unknown_rather_than_destroying_the_snapshot()
    {
        // The forward-compatibility case: a newer Greenlight learns a provider this SDK has
        // never heard of. Losing that one field is fine; losing the whole snapshot — and with
        // it the colour, which is what most consumers are here for — is not.
        var line = GreenlightProtocol
            .Serialize(new SnapshotMessage(1, SampleSnapshot()))
            .Replace("\"AzureDevOps\"", "\"Bitbucket\"");

        var received = GreenlightProtocol.Deserialize<SnapshotMessage>(line);

        Assert.NotNull(received);
        Assert.Equal(GreenlightProvider.Unknown, received.Data.Builds[0].Provider);
        Assert.Equal(GreenlightStatus.Red, received.Data.Status);
        Assert.Equal(42, received.Data.Builds[0].Id);
    }

    [Fact]
    public void An_undefined_numeric_enum_value_also_reads_as_unknown()
    {
        var line = GreenlightProtocol
            .Serialize(new SnapshotMessage(1, SampleSnapshot()))
            .Replace("\"Red\"", "99");

        Assert.Equal(GreenlightStatus.Unknown, GreenlightProtocol.Deserialize<SnapshotMessage>(line)!.Data.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"t\":")]
    [InlineData("[1,2,3]")]
    [InlineData("\"just a string\"")]
    [InlineData("{\"nope\":1}")]
    [InlineData("{\"t\":7}")]
    public void A_line_with_no_usable_type_is_reported_as_such_rather_than_throwing(string line)
    {
        // Whoever reads the wire must be able to ask "what is this?" of any bytes at all,
        // including a half-written line from a host that died mid-flush.
        Assert.Null(GreenlightProtocol.PeekType(line));
    }

    [Fact]
    public void A_message_type_from_the_future_is_named_but_not_understood()
    {
        // Readers switch on the type and ignore what they do not know, which is what lets a
        // newer host add a message without breaking installed consumers.
        Assert.Equal("something_new", GreenlightProtocol.PeekType("{\"t\":\"something_new\",\"x\":1}"));
    }

    [Fact]
    public void Unknown_fields_on_a_known_message_are_ignored()
    {
        var line = "{\"t\":\"ack\",\"id\":\"c1\",\"ok\":true,\"somethingAddedLater\":{\"a\":1}}";

        var ack = GreenlightProtocol.Deserialize<AckMessage>(line);

        Assert.True(ack!.Ok);
        Assert.Equal("c1", ack.Id);
    }

    [Fact]
    public void Deserializing_a_malformed_line_throws_for_the_caller_to_skip()
    {
        // Deliberately not swallowed here: the reader decides, and both readers skip. Making
        // the parser return null would hide a genuine contract break behind a shrug.
        Assert.Throws<JsonException>(() => GreenlightProtocol.Deserialize<AckMessage>("{\"t\":\"ack\","));
    }

    [Fact]
    public void Null_fields_are_left_off_the_wire()
    {
        var line = GreenlightProtocol.Serialize(new AckMessage("c1", true));

        Assert.DoesNotContain("error", line);
        Assert.DoesNotContain("null", line);
    }

    [Fact]
    public void The_pipe_name_is_stable_and_scoped_to_this_account()
    {
        var first = GreenlightProtocol.PipeNameForCurrentUser();
        var second = GreenlightProtocol.PipeNameForCurrentUser();

        Assert.Equal(first, second);
        Assert.StartsWith("greenlight.localapi.v1.", first);

        // The account suffix is a hash: the point is that two users differ, not that anyone
        // can read the SID back out of a pipe name that is visible machine-wide.
        var suffix = first["greenlight.localapi.v1.".Length..];
        Assert.Equal(16, suffix.Length);
        Assert.DoesNotContain(Environment.UserName, first, StringComparison.OrdinalIgnoreCase);
    }
}
