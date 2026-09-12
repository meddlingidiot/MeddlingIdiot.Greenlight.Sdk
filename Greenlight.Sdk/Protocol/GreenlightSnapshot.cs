namespace Greenlight.Sdk.Protocol;

/// <summary>
/// Everything Greenlight currently knows, as of <see cref="CapturedAt"/>. A consumer gets
/// one of these on connect and one on every change thereafter.
/// </summary>
/// <remarks>
/// These types are the SDK's own, mapped from Greenlight's internal models by the host.
/// The indirection is deliberate: renaming something inside Greenlight should break a
/// mapping test, not an app somebody already shipped. Nothing secret is ever mapped in —
/// no tokens, no credential keys, no licence details.
/// </remarks>
/// <param name="Status">The aggregate colour every Greenlight indicator is wearing.</param>
/// <param name="IsBuilding">
/// True while any watched build is actually running. Separate from <paramref name="Status"/>
/// on purpose — a lamp flashes the colour while this is set and holds it steady otherwise.
/// </param>
/// <param name="Reason">
/// Why the colour came out that way, in plain English — "2 pipelines' last completed builds
/// failed.". Greenlight's own reasoning, so a consumer never has to re-derive the rules.
/// </param>
/// <param name="Details">One line per item responsible for <paramref name="Reason"/>, newest first, capped.</param>
/// <param name="Builds">Every build inside the retention window, acknowledged or not.</param>
/// <param name="PullRequests">Every pull request Greenlight is currently tracking.</param>
/// <param name="Connections">The configured remotes and whether each one's last poll succeeded.</param>
/// <param name="CapturedAt">When the host built this snapshot.</param>
/// <param name="AppVersion">The version of the Greenlight that produced it.</param>
public sealed record GreenlightSnapshot(
    GreenlightStatus Status,
    bool IsBuilding,
    string Reason,
    IReadOnlyList<string> Details,
    IReadOnlyList<GreenlightBuild> Builds,
    IReadOnlyList<GreenlightPullRequest> PullRequests,
    IReadOnlyList<GreenlightConnection> Connections,
    DateTimeOffset CapturedAt,
    string AppVersion)
{
    /// <summary>What a consumer sees before anything has been received. Never sent over the wire.</summary>
    public static GreenlightSnapshot Empty { get; } = new(
        GreenlightStatus.Unknown, false, "No data.", [], [], [], [],
        DateTimeOffset.MinValue, string.Empty);
}

/// <summary>One pipeline run.</summary>
/// <param name="Id">The provider's own build id. Stable, and what <c>AcknowledgeBuilds</c> takes.</param>
/// <param name="IsAcknowledged">
/// Whether the user has dismissed this run in Greenlight. Dismissed runs stop holding the
/// colour red, so an app that draws its own list usually wants to hide them too.
/// </param>
public sealed record GreenlightBuild(
    long Id,
    string Pipeline,
    string Project,
    string Branch,
    GreenlightBuildStatus Status,
    GreenlightBuildResult Result,
    DateTimeOffset QueuedAt,
    DateTimeOffset? FinishedAt,
    string TriggeredBy,
    string WebUrl,
    GreenlightProvider Provider,
    bool IsAcknowledged);

/// <summary>One pull request.</summary>
/// <param name="MyReviewStatus">The vote cast by the identity Greenlight is authenticated as.</param>
/// <param name="IsReviewRequestedFromMe">
/// Whether this PR is waiting on that identity. This is the flag that turns the aggregate
/// colour yellow.
/// </param>
public sealed record GreenlightPullRequest(
    int Id,
    string Title,
    string Repo,
    string Project,
    string Author,
    string SourceBranch,
    string TargetBranch,
    GreenlightPullRequestStatus Status,
    GreenlightReviewStatus MyReviewStatus,
    bool IsReviewRequestedFromMe,
    DateTimeOffset CreatedAt,
    string WebUrl,
    GreenlightProvider Provider);

/// <summary>A configured remote, and whether Greenlight can currently reach it.</summary>
/// <remarks>
/// Carries no URL and no credentials — only enough to tell a consumer that some of the
/// picture is missing, and which remote is responsible.
/// </remarks>
public sealed record GreenlightConnection(
    Guid Id,
    string Name,
    GreenlightProvider Provider,
    bool Healthy);
