using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using CommandLine;
using Octokit;

namespace ReleaseTool
{
    /// <summary>
    ///     Runs the commands required for releasing a candidate.
    ///     * Merges the candidate branch into the release branch.
    ///     * Pushes the release branch.
    ///     * Creates a GitHub release draft.
    ///     * Creates a PR from the release-branch (defaults to release) branch into the source-branch (defaults to master).
    /// </summary>
    internal class ReleaseCommand
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

        private const string PullRequestNameTemplate = "Release {0} - Merge {1} into {2}";
        private const string pullRequestBody = "Merging {0} back into {1}. Please manually resolve merge conflicts.";

        private const string releaseAnnotationTemplate = "* Successfully created a [draft release]({0}) " +
           "in the repo `{1}`. Your human labour is now required to publish it.\n";
        private const string prAnnotationTemplate = "* Successfully created a [pull request]({0}) " +
            "in the repo `{1}` from `{2}` into `{3}`. " +
            "Your human labour is now required to merge these PRs.\n";

        // Changelog file configuration
        private const string ChangeLogFilename = "CHANGELOG.md";
        private const string CandidateCommitMessageTemplate = "{0}.";
        private const string ChangeLogReleaseHeadingTemplate = "## [`{0}`] - {1:yyyy-MM-dd}";

        // Names of the version files that live in the UnrealEngine repository.
        private const string UnrealGDKVersionFile = "UnrealGDKVersion.txt";
        private const string UnrealGDKExampleProjectVersionFile = "UnrealGDKExampleProjectVersion.txt";

        [Verb("release", HelpText = "Merge a release branch and create a github release draft.")]
        public class Options : GitHubClient.IGitHubOptions
        {
            [Value(0, MetaName = "version", HelpText = "The version that is being released.")]
            public string Version { get; set; }

            [Option('u', "pull-request-url", HelpText = "The link to the release candidate branch to merge.",
                Required = true)]
            public string PullRequestUrl { get; set; }

            [Option("source-branch", HelpText = "The source branch name from which we are cutting the candidate.", Required = true)]
            public string SourceBranch { get; set; }

            [Option("candidate-branch", HelpText = "The candidate branch name.", Required = true)]
            public string CandidateBranch { get; set; }

            [Option("release-branch", HelpText = "The name of the branch into which we are merging the candidate.", Required = true)]
            public string ReleaseBranch { get; set; }

            [Option("github-organization", HelpText = "The Github Organization that contains the targeted repository.", Required = true)]
            public string GithubOrgName { get; set; }

            [Option("engine-versions", HelpText = "An array containing every engine version source branch.", Required = false)]
            public string EngineVersions {get;set;}

            public string GitHubTokenFile { get; set; }

            public string GitHubToken { get; set; }

            public string MetadataFilePath { get; set; }
        }

        private readonly Options options;

        public ReleaseCommand(Options options)
        {
            this.options = options;
        }

        /*
         *     This tool is designed to execute most of the git operations required when releasing:
         *         1. Merge the RC PR into the release branch.
         *         2. Draft a GitHub release using the changelog notes.
         *         3. Open a PR from the release-branch into source-branch.
         */
        public int Run()
        {
            Common.VerifySemanticVersioningFormat(options.Version);
            var (repoName, pullRequestId) = ExtractPullRequestInfo(options.PullRequestUrl);
            var gitHubClient = new GitHubClient(options);
            var repoUrl = string.Format(Common.RepoUrlTemplate, options.GithubOrgName, repoName);
            var gitHubRepo = gitHubClient.GetRepositoryFromUrl(repoUrl);

            // Check if the PR has been merged already.
            // If it has, log the PR URL and move on.
            // This ensures the idempotence of the pipeline.
            if (gitHubClient.GetMergeState(gitHubRepo, pullRequestId) == GitHubClient.MergeState.AlreadyMerged)
            {
                Logger.Info("Candidate branch has already merged into release branch. No merge operation will be attempted.");

                // Check if a PR has already been opened from release branch into source branch.
                // If it has, log the PR URL and move on.
                // This ensures the idempotence of the pipeline.
                var githubOrg = options.GithubOrgName;
                var branchFrom = $"{options.CandidateBranch}-cleanup";
                var branchTo = options.SourceBranch;

                if (!gitHubClient.TryGetPullRequest(gitHubRepo, githubOrg, branchFrom, branchTo, out var pullRequest))
                {
                    try
                    {
                        using (var gitClient = GitClient.FromRemote(repoUrl))
                        {
                            gitClient.CheckoutRemoteBranch(options.ReleaseBranch);
                            gitClient.ForcePush(branchFrom);
                        }
                        pullRequest = gitHubClient.CreatePullRequest(gitHubRepo,
                        branchFrom,
                        branchTo,
                        string.Format(PullRequestNameTemplate, options.Version, options.ReleaseBranch, options.SourceBranch),
                        string.Format(pullRequestBody, options.ReleaseBranch, options.SourceBranch));
                    }
                    catch (Octokit.ApiValidationException e)
                    {
                            // Handles the case where source-branch (default master) and release-branch (default release) are identical, so there is no need to merge source-branch back into release-branch.
                            if (e.ApiError.Errors.Count>0 && e.ApiError.Errors[0].Message.Contains("No commits between"))
                            {
                                Logger.Info(e.ApiError.Errors[0].Message);
                                Logger.Info("No PR will be created.");
                                return 0;
                            }

                            throw;
                    }
                }

                else
                {
                    Logger.Info("A PR has already been opened from release branch into source branch: {0}", pullRequest.HtmlUrl);
                }

                var prAnnotation = string.Format(prAnnotationTemplate,
                    pullRequest.HtmlUrl, repoName, options.ReleaseBranch, options.SourceBranch);
                BuildkiteAgent.Annotate(AnnotationLevel.Info, "release-into-source-prs", prAnnotation, true);

                Logger.Info("Pull request available: {0}", pullRequest.HtmlUrl);
                Logger.Info("Successfully created PR from release branch into source branch.");
                Logger.Info("Merge hash: {0}", pullRequest.MergeCommitSha);

                return 0;
            }

            var remoteUrl = string.Format(Common.RepoUrlTemplate, options.GithubOrgName, repoName);
            try
            {
                // 1. Clones the source repo.
                using (var gitClient = GitClient.FromRemote(remoteUrl))
                {
                    // 2. Checks out the candidate branch, which defaults to 4.xx-SpatialOSUnrealGDK-x.y.z-rc in UnrealEngine and x.y.z-rc in all other repos.
                    gitClient.CheckoutRemoteBranch(options.CandidateBranch);

                    // 3. Makes repo-specific changes for prepping the release (e.g. updating version files, formatting the CHANGELOG).
                    switch (repoName)
                    {
                        case "UnrealEngine":
                            UpdateVersionFile(gitClient, options.Version, UnrealGDKVersionFile);
                            UpdateVersionFile(gitClient, options.Version, UnrealGDKExampleProjectVersionFile);
                            break;
                        case "UnrealGDK":
                            UpdateChangeLog(ChangeLogFilename, options, gitClient);

                            var releaseHashes = options.EngineVersions.Split(" ")
                                .Select(version => $"{version.Trim()}-release")
                                .Select(BuildkiteAgent.GetMetadata)
                                .Select(hash => $"UnrealEngine-{hash}")
                                .ToList();

                            UpdateUnrealEngineVersionFile(releaseHashes, gitClient);
                            break;
                        case "UnrealGDKExampleProject":
                            UpdateVersionFile(gitClient, options.Version, UnrealGDKVersionFile);
                            break;
                        case "UnrealGDKTestGyms":
                            UpdateVersionFile(gitClient, options.Version, UnrealGDKVersionFile);
                            break;
                        case "UnrealGDKEngineNetTest":
                            UpdateVersionFile(gitClient, options.Version, UnrealGDKVersionFile);
                            break;
                        case "TestGymBuildKite":
                            UpdateVersionFile(gitClient, options.Version, UnrealGDKVersionFile);
                            break;
                    }

                    // 4. Commit changes and push them to a remote candidate branch.
                    gitClient.Commit(string.Format(CandidateCommitMessageTemplate, options.Version));
                    gitClient.ForcePush(options.CandidateBranch);
                }

                // Since we've pushed changes, we need to wait for all checks to pass before attempting to merge it.
                var startTime = DateTime.Now;
                while (true)
                {
                    if (DateTime.Now.Subtract(startTime) > TimeSpan.FromHours(12))
                    {
                        throw new Exception($"Exceeded timeout waiting for PR to be mergeable: {options.PullRequestUrl}");
                    }

                    if (gitHubClient.GetMergeState(gitHubRepo, pullRequestId) == GitHubClient.MergeState.ReadyToMerge)
                    {
                        Logger.Info($"{options.PullRequestUrl} is mergeable. Attempting to merge.");
                        break;
                    }

                    Logger.Info($"{options.PullRequestUrl} is not in a mergeable state, will query mergeability again in one minute.");
                    Thread.Sleep(TimeSpan.FromMinutes(1));
                }

                PullRequestMerge mergeResult = null;
                while (true)
                {
                    // Merge into release
                    try
                    {
                        mergeResult = gitHubClient.MergePullRequest(gitHubRepo, pullRequestId, PullRequestMergeMethod.Merge);
                    }
                    catch (Octokit.PullRequestNotMergeableException e) {} // Will be covered by log below
                    if (DateTime.Now.Subtract(startTime) > TimeSpan.FromHours(12))
                    {
                        throw new Exception($"Exceeded timeout waiting for PR to be mergeable: {options.PullRequestUrl}");
                    }
                    if (!mergeResult.Merged)
                    {
                        Logger.Info($"Was unable to merge pull request at: {options.PullRequestUrl}. Received error: {mergeResult.Message}");
                        Logger.Info($"{options.PullRequestUrl} is not in a mergeable state, will query mergeability again in one minute.");
                        Thread.Sleep(TimeSpan.FromMinutes(1));
                    }
                    else
                    {
                        break;
                    }
                }

                Logger.Info($"{options.PullRequestUrl} had been merged.");

                // This uploads the commit hashes of the merge into release.
                // When run against UnrealGDK, the UnrealEngine hashes are used to update the unreal-engine.version file to include the UnrealEngine release commits.
                BuildkiteAgent.SetMetaData(options.ReleaseBranch, mergeResult.Sha);

                //TODO: UNR-3615 - Fix this so it does not throw Octokit.ApiValidationException: Reference does not exist.
                // Delete candidate branch.
                //gitHubClient.DeleteBranch(gitHubClient.GetRepositoryFromUrl(repoUrl), options.CandidateBranch);

                using (var gitClient = GitClient.FromRemote(repoUrl))
                {
                    // Create GitHub release in the repo
                    gitClient.Fetch();
                    gitClient.CheckoutRemoteBranch(options.ReleaseBranch);
                    var release = CreateRelease(gitHubClient, gitHubRepo, gitClient, repoName);

                    BuildkiteAgent.Annotate(AnnotationLevel.Info, "draft-releases",
                        string.Format(releaseAnnotationTemplate, release.HtmlUrl, repoName), true);

                    Logger.Info("Release Successful!");
                    Logger.Info("Release hash: {0}", gitClient.GetHeadCommit().Sha);
                    Logger.Info("Draft release: {0}", release.HtmlUrl);
                }

                // Check if a PR has already been opened from release branch into source branch.
                // If it has, log the PR URL and move on.
                // This ensures the idempotence of the pipeline.
                var githubOrg = options.GithubOrgName;
                var branchFrom = $"{options.CandidateBranch}-cleanup";
                var branchTo = options.SourceBranch;

                if (!gitHubClient.TryGetPullRequest(gitHubRepo, githubOrg, branchFrom, branchTo, out var pullRequest))
                {
                    try
                    {
                        using (var gitClient = GitClient.FromRemote(repoUrl))
                        {
                            gitClient.CheckoutRemoteBranch(options.ReleaseBranch);
                            gitClient.ForcePush(branchFrom);
                        }
                        pullRequest = gitHubClient.CreatePullRequest(gitHubRepo,
                        branchFrom,
                        branchTo,
                        string.Format(PullRequestNameTemplate, options.Version, options.ReleaseBranch, options.SourceBranch),
                        string.Format(pullRequestBody, options.ReleaseBranch, options.SourceBranch));
                    }
                    catch (Octokit.ApiValidationException e)
                    {
                        // Handles the case where source-branch (default master) and release-branch (default release) are identical, so there is no need to merge source-branch back into release-branch.
                        if (e.ApiError.Errors.Count > 0 && e.ApiError.Errors[0].Message.Contains("No commits between"))
                        {
                            Logger.Info(e.ApiError.Errors[0].Message);
                            Logger.Info("No PR will be created.");
                            return 0;
                        }

                        throw;
                    }
                }

                else
                {
                    Logger.Info("A PR has already been opened from release branch into source branch: {0}", pullRequest.HtmlUrl);
                }

                var prAnnotation = string.Format(prAnnotationTemplate,
                    pullRequest.HtmlUrl, repoName, options.ReleaseBranch, options.SourceBranch);
                BuildkiteAgent.Annotate(AnnotationLevel.Info, "release-into-source-prs", prAnnotation, true);

                Logger.Info("Pull request available: {0}", pullRequest.HtmlUrl);
                Logger.Info($"Successfully created PR for merging {options.ReleaseBranch} into {options.SourceBranch}.");
            }
            catch (Exception e)
            {
                Logger.Error(e, $"ERROR: Unable to merge {options.CandidateBranch} into {options.ReleaseBranch} and/or clean up by merging {options.ReleaseBranch} into {options.SourceBranch}. Error: {0}", e);
                return 1;
            }

            return 0;
        }
        internal static void UpdateChangeLog(string ChangeLogFilePath, Options options, GitClient gitClient)
        {
            using (new WorkingDirectoryScope(gitClient.RepositoryPath))
            {
                if (File.Exists(ChangeLogFilePath))
                {
                    Logger.Info("Updating {0}...", ChangeLogFilePath);
                    var changelog = File.ReadAllLines(ChangeLogFilePath).ToList();
                    var releaseHeading = string.Format(ChangeLogReleaseHeadingTemplate, options.Version,
                        DateTime.Now);
                    var releaseIndex = changelog.FindIndex(line => IsMarkdownHeading(line, 2, $"[`{options.Version}`] - "));
                    // If we already have a changelog entry for this release, replace it.
                    if (releaseIndex != -1)
                    {
                        changelog[releaseIndex] = releaseHeading;
                    }
                    else
                    {
                        // Add the new release heading under the "## Unreleased" one.
                        // Assuming that this is the first heading.
                        var unreleasedIndex = changelog.FindIndex(line => IsMarkdownHeading(line, 2));
                        changelog.InsertRange(unreleasedIndex + 1, new[]
                        {
                            string.Empty,
                            releaseHeading
                        });
                    }
                    File.WriteAllLines(ChangeLogFilePath, changelog);
                    gitClient.StageFile(ChangeLogFilePath);
                }
            }
        }

        private Release CreateRelease(GitHubClient gitHubClient, Repository gitHubRepo, GitClient gitClient, string repoName)
        {
            var headCommit = gitClient.GetHeadCommit().Sha;

            var engineVersion = options.SourceBranch.Trim();

            string name;
            string releaseBody;

            switch (repoName)
            {
                case "UnrealGDK":
                    string changelog;
                    using (new WorkingDirectoryScope(gitClient.RepositoryPath))
                    {
                        changelog = GetReleaseNotesFromChangeLog();
                    }
                    name = $"GDK for Unreal Release {options.Version}";
                    releaseBody =
$@"The release notes are published in both English and Chinese. To view the Chinese version, scroll down a bit for details. Thanks!

Release notes 将同时提供中英文。要浏览中文版本，向下滚动页面查看详情。感谢！

# English version

**Unreal GDK version {options.Version} is go!**

## Release Notes

* **Release sheriff:** Your human labour is required to populate this section with the headline new features and breaking changes from the CHANGELOG.

## Upgrading

* You can find the corresponding UnrealEngine version(s) [here](https://github.com/improbableio/UnrealEngine/releases).
* You can find the corresponding UnrealGDKExampleProject version [here](https://github.com/spatialos/UnrealGDKExampleProject/releases).

Follow **[these](https://documentation.improbable.io/gdk-for-unreal/docs/keep-your-gdk-up-to-date)** steps to upgrade your GDK, Engine fork and Example Project to the latest release.

You can read the full release notes [here](https://github.com/spatialos/UnrealGDK/blob/release/CHANGELOG.md) or below.

Join the community on our [forums](https://forums.improbable.io/), or on [Discord](https://discordapp.com/invite/vAT7RSU).

Happy developing,

*The GDK team*

---

{changelog}

# 中文版本

**[虚幻引擎开发套件 (GDK) {options.Version} 版本已发布！**

## Release Notes

* **Tech writer:** Your human labour is required to translate the above and include it here.

";
                    break;
                case "UnrealEngine":
                    name = $"{engineVersion}-{options.Version}";
                    releaseBody =
$@"Unreal GDK version {options.Version} is go!

* This Engine version corresponds to GDK version: [{options.Version}](https://github.com/spatialos/UnrealGDK/releases).
* You can find the corresponding UnrealGDKExampleProject version [here](https://github.com/spatialos/UnrealGDKExampleProject/releases).

Follow [these steps](https://documentation.improbable.io/gdk-for-unreal/docs/keep-your-gdk-up-to-date) to upgrade your GDK, Unreal Engine fork and your Project to the latest release.

You can read the full release notes [here](https://github.com/spatialos/UnrealGDK/blob/release/CHANGELOG.md).

Join the community on our [forums](https://forums.improbable.io/), or on [Discord](https://discordapp.com/invite/vAT7RSU).

Happy developing!<br>
GDK team";
                    break;
                case "UnrealGDKTestGyms":
                    name = $"{options.Version}";
                    releaseBody =
$@"Unreal GDK version {options.Version} is go!

* This UnrealGDKTestGyms version corresponds to GDK version: [{options.Version}](https://github.com/spatialos/UnrealGDK/releases).
* You can find the corresponding UnrealGDKExampleProject version [here](https://github.com/spatialos/UnrealGDKExampleProject/releases).
* You can find the corresponding UnrealEngine version(s) [here](https://github.com/improbableio/UnrealEngine/releases).

Follow [these steps](https://documentation.improbable.io/gdk-for-unreal/docs/keep-your-gdk-up-to-date) to upgrade your GDK, Unreal Engine fork and your Project to the latest release.

You can read the full release notes [here](https://github.com/spatialos/UnrealGDK/blob/release/CHANGELOG.md).

Join the community on our [forums](https://forums.improbable.io/), or on [Discord](https://discordapp.com/invite/vAT7RSU).

Happy developing!<br>
GDK team";
                    break;
                case "UnrealGDKEngineNetTest":
                    name = $"{options.Version}";
                    releaseBody =
$@"Unreal GDK version {options.Version} is go!

* This UnrealGDKEngineNetTest version corresponds to GDK version: [{options.Version}](https://github.com/spatialos/UnrealGDK/releases).
* You can find the corresponding UnrealGDKTestGyms version [here](https://github.com/improbable/UnrealGDKTestGyms/releases).
* You can find the corresponding UnrealGDKExampleProject version [here](https://github.com/spatialos/UnrealGDKExampleProject/releases).
* You can find the corresponding UnrealEngine version(s) [here](https://github.com/improbableio/UnrealEngine/releases).

Follow [these steps](https://documentation.improbable.io/gdk-for-unreal/docs/keep-your-gdk-up-to-date) to upgrade your GDK, Unreal Engine fork and your Project to the latest release.

You can read the full release notes [here](https://github.com/spatialos/UnrealGDK/blob/release/CHANGELOG.md).

Join the community on our [forums](https://forums.improbable.io/), or on [Discord](https://discordapp.com/invite/vAT7RSU).

Happy developing!<br>
GDK team";
                    break;
                case "TestGymBuildKite":
                    name = $"{options.Version}";
                    releaseBody =
$@"Unreal GDK version {options.Version} is go!

* This TestGymBuildKite version corresponds to GDK version: [{options.Version}](https://github.com/spatialos/UnrealGDK/releases).
* You can find the corresponding UnrealGDKTestGyms version [here](https://github.com/improbable/UnrealGDKTestGyms/releases).
* You can find the corresponding UnrealGDKExampleProject version [here](https://github.com/spatialos/UnrealGDKExampleProject/releases).
* You can find the corresponding UnrealEngine version(s) [here](https://github.com/improbableio/UnrealEngine/releases).

Follow [these steps](https://documentation.improbable.io/gdk-for-unreal/docs/keep-your-gdk-up-to-date) to upgrade your GDK, Unreal Engine fork and your Project to the latest release.

You can read the full release notes [here](https://github.com/spatialos/UnrealGDK/blob/release/CHANGELOG.md).

Join the community on our [forums](https://forums.improbable.io/), or on [Discord](https://discordapp.com/invite/vAT7RSU).

Happy developing!<br>
GDK team";
                    break;
                case "UnrealGDKExampleProject":
                    name = $"{options.Version}";
                    releaseBody =
$@"Unreal GDK version {options.Version} is go!

* This UnrealGDKExampleProject version corresponds to GDK version: [{options.Version}](https://github.com/spatialos/UnrealGDK/releases).
* You can find the corresponding UnrealEngine version(s) [here](https://github.com/improbableio/UnrealEngine/releases).

Follow [these steps](https://documentation.improbable.io/gdk-for-unreal/docs/keep-your-gdk-up-to-date) to upgrade your GDK, Unreal Engine fork and your Project to the latest release.

You can read the full release notes [here](https://github.com/spatialos/UnrealGDK/blob/release/CHANGELOG.md).

Join the community on our [forums](https://forums.improbable.io/), or on [Discord](https://discordapp.com/invite/vAT7RSU).

Happy developing!<br>
GDK team";
                    break;
                default:
                    throw new ArgumentException("Unsupported repository.", nameof(repoName));
            }

            return gitHubClient.CreateDraftRelease(gitHubRepo, options.Version, releaseBody, name, headCommit);
        }

        private static void UpdateVersionFile(GitClient gitClient, string fileContents, string relativeFilePath)
        {
            using (new WorkingDirectoryScope(gitClient.RepositoryPath))
            {
                Logger.Info("Updating contents of version file '{0}' to '{1}'...", relativeFilePath, fileContents);

                if (!File.Exists(relativeFilePath))
                {
                    throw new InvalidOperationException("Could not update the version file as the file " +
                        $"'{relativeFilePath}' does not exist.");
                }

                File.WriteAllText(relativeFilePath, $"{fileContents}");

                gitClient.StageFile(relativeFilePath);
            }
        }

        private static bool IsMarkdownHeading(string markdownLine, int level, string startTitle = null)
        {
            var heading = $"{new string('#', level)} {startTitle ?? string.Empty}";

            return markdownLine.StartsWith(heading);
        }

        private static (string, int) ExtractPullRequestInfo(string pullRequestUrl)
        {
            const string regexString = "github\\.com\\/.*\\/(.*)\\/pull\\/([0-9]*)";

            var match = Regex.Match(pullRequestUrl, regexString);

            if (!match.Success)
            {
                throw new ArgumentException($"Malformed pull request url: {pullRequestUrl}");
            }

            if (match.Groups.Count < 3)
            {
                throw new ArgumentException($"Malformed pull request url: {pullRequestUrl}");
            }

            var repoName = match.Groups[1].Value;
            var pullRequestIdStr = match.Groups[2].Value;

            if (!int.TryParse(pullRequestIdStr, out int pullRequestId))
            {
                throw new Exception(
                    $"Parsing pull request URL failed. Expected number for pull request id, received: {pullRequestIdStr}");
            }

            return (repoName, pullRequestId);
        }

        private static string GetReleaseNotesFromChangeLog()
        {
            if (!File.Exists(ChangeLogFilename))
            {
                throw new InvalidOperationException("Could not get draft release notes, as the change log file, " +
                    $"{ChangeLogFilename}, does not exist.");
            }

            Logger.Info("Reading {0}...", ChangeLogFilename);

            var releaseBody = new StringBuilder();
            var changedSection = 0;

            using (var reader = new StreamReader(ChangeLogFilename))
            {
                while (!reader.EndOfStream)
                {
                    // Here we target the second Heading2 ("##") section.
                    // The first section will be the "Unreleased" section. The second will be the correct release notes.
                    var line = reader.ReadLine();
                    if (line.StartsWith("## "))
                    {
                        changedSection += 1;

                        if (changedSection == 3)
                        {
                            break;
                        }

                        continue;
                    }

                    if (changedSection == 2)
                    {
                        releaseBody.AppendLine(line);
                    }
                }
            }

            return releaseBody.ToString();
        }

        private static void UpdateUnrealEngineVersionFile(List<string> versions, GitClient client)
        {
            const string unrealEngineVersionFile = "ci/unreal-engine.version";

            using (new WorkingDirectoryScope(client.RepositoryPath))
            {
                File.WriteAllLines(unrealEngineVersionFile, versions);
                client.StageFile(unrealEngineVersionFile);
            }
        }
    }
}
