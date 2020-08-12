﻿using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Konmaripo.Web.Models;
using LibGit2Sharp;
using Microsoft.Extensions.Options;
using Octokit;
using Serilog;
using TimeZoneConverter;
using Branch = LibGit2Sharp.Branch;
using FileMode = System.IO.FileMode;

namespace Konmaripo.Web.Services
{
    public class GitHubService : IGitHubService
    {
        private readonly IGitHubClient _githubClient;
        private readonly GitHubSettings _gitHubSettings;
        private readonly ILogger _logger;
        const string REMOTE_NAME = "origin"; // hard-coded since this will be the default when cloned from GitHub.

        public GitHubService(IGitHubClient githubClient, IOptions<GitHubSettings> githubSettings, ILogger logger)
        {
            _githubClient = githubClient ?? throw new ArgumentNullException(nameof(githubClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _gitHubSettings = githubSettings?.Value ?? throw new ArgumentNullException(nameof(githubSettings));
        }

        public async Task<List<GitHubRepo>> GetRepositoriesForOrganizationAsync()
        {
            var orgName = _gitHubSettings.OrganizationName;

            var repos = await _githubClient.Repository.GetAllForOrg(orgName);

            return repos.Select(x => new GitHubRepo(x.Id, x.Name, x.StargazersCount, x.Archived, x.ForksCount, x.OpenIssuesCount, x.CreatedAt, x.UpdatedAt, x.Description, x.Private, x.PushedAt, x.HtmlUrl)).ToList();
        }

        public async Task<ExtendedRepoInformation> GetExtendedRepoInformationFor(long repoId)
        {
            var watchers = await _githubClient.Activity.Watching.GetAllWatchers(repoId);
            var views = await _githubClient.Repository.Traffic.GetViews(repoId, new RepositoryTrafficRequest(TrafficDayOrWeek.Week));
            var commitActivity = await _githubClient.Repository.Statistics.GetCommitActivity(repoId);

            var commitActivityInLast4Weeks = commitActivity.Activity.OrderByDescending(x => x.WeekTimestamp).Take(4).Sum(x => x.Total);
            var extendedRepoInfo = new ExtendedRepoInformation(repoId, watchers.Count, views.Count, commitActivityInLast4Weeks);

            return extendedRepoInfo;
        }

        public async Task CreateArchiveIssueInRepo(long repoId, string currentUser)
        {
            var newIssue = new NewIssue("This repository is being archived")
            {
                Body = @$"Archive process initiated by {currentUser} via the Konmaripo tool."
            };

            try
            {
                await _githubClient.Issue.Create(repoId, newIssue);
            }
            catch(ApiException ex)
            {
                _logger.Warning("Issues are disabled for repository ID '{RepositoryID}'; could not create archive issue.", repoId);
                if (ex.Message != "Issues are disabled for this repo")
                {
                    throw;
                }
            }
        }

        public async Task ArchiveRepository(long repoId, string repoName)
        {
            var makeArchived = new RepositoryUpdate(repoName)
            {
                Archived = true
            };

            await _githubClient.Repository.Edit(repoId, makeArchived);
        }

        public async Task<RepoQuota> GetRepoQuotaForOrg()
        {
            var org = await _githubClient.Organization.Get(_gitHubSettings.OrganizationName);
            return new RepoQuota(org.Plan.PrivateRepos, org.OwnedPrivateRepos);
        }

        public FileStream ZippedRepositoryStream(string repoName)
        {

            var url = $"https://github.com/{_gitHubSettings.OrganizationName}/{repoName}.git".ToLowerInvariant();
            var creds = new UsernamePasswordCredentials
            {
                Username = _gitHubSettings.AccessToken,
                Password = string.Empty
            };

            var fetchOptions = new FetchOptions()
            {
                TagFetchMode = TagFetchMode.All,
                CredentialsProvider = (_url, _user, _cred) => creds,
            };

            var options = new CloneOptions
            {
                Checkout = true,
                IsBare = false,
                RecurseSubmodules = true,
                // ReSharper disable InconsistentNaming
                CredentialsProvider = (_url, _user, _cred) => creds,FetchOptions = fetchOptions
                // ReSharper enable InconsistentNaming
            };
            
            var startPath = "./Data";
            var destinationArchiveFileName = Path.Combine(startPath, $"{repoName}.zip");
            var clonePath = Path.Combine(startPath, repoName);

            // TODO: Make async
            var pathToRepoGitFile = LibGit2Sharp.Repository.Clone(url, clonePath, options);
            
            // This ensures all branches and tags get fetched as well.
            using (var repo = new LibGit2Sharp.Repository(pathToRepoGitFile))
            {
                var remoteBranches = repo.Branches.Where(x=>x.IsRemote && !x.IsTracking).ToList();
                
                var nonExistingRemoteBranches = remoteBranches.Where(x =>
                {
                    var localBranchName = GenerateLocalBranchName(x);
                    return repo.Branches[localBranchName] == null;
                }).ToList();

                foreach (var remoteBranch in nonExistingRemoteBranches)
                {
                    var localBranchName = GenerateLocalBranchName(remoteBranch);

                    var localCreatedBranch = repo.CreateBranch(localBranchName, remoteBranch.Tip);
                    repo.Branches.Update(localCreatedBranch, b => b.TrackedBranch = remoteBranch.CanonicalName);
                }
                repo.Network.Fetch(REMOTE_NAME, new[] { $"+refs/heads/*:refs/remotes/origin/*" }, fetchOptions);
                var mergeOptions = new MergeOptions
                {
                    FastForwardStrategy = FastForwardStrategy.Default,
                    CommitOnSuccess = true,
                    FailOnConflict = true,
                    MergeFileFavor = MergeFileFavor.Theirs
                };
                var pullOptions = new PullOptions { FetchOptions = fetchOptions, MergeOptions = mergeOptions };
                var sig = new LibGit2Sharp.Signature("Konmaripo Tool", "konmaripo@excella.com", DateTimeOffset.UtcNow);
                Commands.Pull(repo, sig, pullOptions);
            }


            var pathToFullRepo = pathToRepoGitFile.Replace(".git/", ""); // Directory.GetParent didn't work for this, maybe due to the period in the directory name.
            // TODO: Make async
            ZipFile.CreateFromDirectory(pathToFullRepo, destinationArchiveFileName, CompressionLevel.Fastest, false);

            Directory.Delete(clonePath, true);

            return new FileStream(destinationArchiveFileName, FileMode.Open, FileAccess.Read, FileShare.None, 4096, FileOptions.DeleteOnClose);
        }

        private string GenerateLocalBranchName(Branch x)
        {
            return x.FriendlyName.Replace($"{REMOTE_NAME}/", string.Empty);
        }

        public int RemainingAPIRequests()
        {
            return _githubClient.GetLastApiInfo().RateLimit.Remaining;
        }

        public Task CreateIssueInRepo(NewIssue issue, long repoId)
        {
            return _githubClient.Issue.Create(repoId, issue);
        }

        public DateTimeOffset APITokenResetTime()
        {
            var reset = _githubClient.GetLastApiInfo().RateLimit.Reset;
            var timeZoneToConvertTo = TZConvert.GetTimeZoneInfo(_gitHubSettings.TimeZoneDisplayId);

            var resultingTime = TimeZoneInfo.ConvertTime(reset, timeZoneToConvertTo);

            return resultingTime;
        }
    }
}
