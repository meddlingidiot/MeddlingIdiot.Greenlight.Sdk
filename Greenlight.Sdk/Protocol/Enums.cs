using System.Text.Json.Serialization;

namespace Greenlight.Sdk.Protocol;

/// <summary>
/// The aggregate colour Greenlight is showing: the same verdict its tray icon, header dot
/// and hardware lamps wear. <see cref="Unknown"/> is Greenlight's grey — nothing polled
/// yet, or every connection's last poll failed.
/// </summary>
/// <remarks>
/// A pipeline that is broken stays <see cref="Red"/> while it rebuilds rather than going
/// hopefully yellow; "something is happening right now" is said by
/// <see cref="GreenlightSnapshot.IsBuilding"/>, not by the colour.
/// </remarks>
[JsonConverter(typeof(TolerantEnumConverter<GreenlightStatus>))]
public enum GreenlightStatus
{
    /// <summary>Nothing known yet, or every connection is failing.</summary>
    Unknown = 0,

    /// <summary>Every pipeline's last completed run passed, and no PR is waiting on you.</summary>
    Green,

    /// <summary>A build is queued, a pipeline has no result yet, or a PR wants your review.</summary>
    Yellow,

    /// <summary>At least one pipeline's last completed build failed.</summary>
    Red,
}

/// <summary>Where an item came from.</summary>
[JsonConverter(typeof(TolerantEnumConverter<GreenlightProvider>))]
public enum GreenlightProvider
{
    /// <summary>A provider this version of the SDK does not know about.</summary>
    Unknown = 0,

    /// <summary>Azure DevOps — pipelines and repos.</summary>
    AzureDevOps,

    /// <summary>GitHub — checks and pull requests.</summary>
    GitHub,
}

/// <summary>How far through its life a build run is.</summary>
[JsonConverter(typeof(TolerantEnumConverter<GreenlightBuildStatus>))]
public enum GreenlightBuildStatus
{
    /// <summary>A state this version of the SDK does not know about.</summary>
    Unknown = 0,

    /// <summary>Queued, waiting for an agent.</summary>
    NotStarted,

    /// <summary>Running now.</summary>
    InProgress,

    /// <summary>A cancellation has been requested and has not taken effect yet.</summary>
    Cancelling,

    /// <summary>Finished — for whatever value of finished. See <see cref="GreenlightBuild.Result"/>.</summary>
    Completed,
}

/// <summary>
/// How a build run turned out. Only meaningful once <see cref="GreenlightBuild.Status"/>
/// is <see cref="GreenlightBuildStatus.Completed"/>.
/// </summary>
[JsonConverter(typeof(TolerantEnumConverter<GreenlightBuildResult>))]
public enum GreenlightBuildResult
{
    /// <summary>No result recorded — the run has not finished.</summary>
    Unknown = 0,

    /// <summary>Everything passed.</summary>
    Succeeded,

    /// <summary>Finished with something non-fatal wrong — a failed non-blocking task, say.</summary>
    PartiallySucceeded,

    /// <summary>Broken. This is what holds the aggregate colour red.</summary>
    Failed,

    /// <summary>Stopped by a person or a policy before it finished.</summary>
    Cancelled,
}

/// <summary>Whether a pull request is still open.</summary>
[JsonConverter(typeof(TolerantEnumConverter<GreenlightPullRequestStatus>))]
public enum GreenlightPullRequestStatus
{
    /// <summary>A state this version of the SDK does not know about.</summary>
    Unknown = 0,

    /// <summary>Open.</summary>
    Active,

    /// <summary>Merged.</summary>
    Completed,

    /// <summary>Closed without merging.</summary>
    Abandoned,
}

/// <summary>A reviewer's vote on a pull request.</summary>
[JsonConverter(typeof(TolerantEnumConverter<GreenlightReviewStatus>))]
public enum GreenlightReviewStatus
{
    /// <summary>No vote cast. Also what an unrecognised vote reads as.</summary>
    NoVote = 0,

    /// <summary>Approved outright.</summary>
    Approved,

    /// <summary>Approved, with comments the author may take or leave.</summary>
    ApprovedWithSuggestions,

    /// <summary>Waiting on the author — Azure DevOps' soft rejection.</summary>
    WaitingForAuthor,

    /// <summary>Changes requested.</summary>
    Rejected,
}
