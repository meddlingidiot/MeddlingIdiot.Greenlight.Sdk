using Fallout.Common;
using Fallout.Solutions;
using Automation.Fallout.Components;
using Automation.Fallout.Components.Components;
using Automation.Fallout.Components.DefaultBuilds;
using Automation.Fallout.Components.Parameters;

/// <summary>
/// Build configuration for PackageBuild
/// </summary>

public class Build : GitHubActionsBuild, IShowVersion, IClean, ICompile, IRestore, IScanForSecrets, IRunUnitTests, IRunIntegrationTests, IGenerateCoverageReport, ITest, IUpdateChangelog, INuGetPublish, ITagRelease, IAnnounceRelease
{

    public static int Main() => Execute<Build>(
        x => ((INuGetPublish)x).PublishNuGet);

    // The SDK is public and MIT, and the whole point of publishing it is that a stranger can
    // restore it: nuget.org, not GitHub Packages, which needs a token even when public.
    string? IHasNuGetOrg.NuGetOwner => "themeddlingidiot";

    // The only publish step here, so it is the one that tags.
    bool INuGetPublish.TagsReleasesFromNuGet => true;

    int IHasTests.MinCoverageThreshold => 30;
    bool IHasTests.BreakBuildOnSecretLeaks => false;
}
