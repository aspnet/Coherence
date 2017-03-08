using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NuGet.Packaging;

namespace CoherenceBuild
{
    public class CoherenceBuild
    {
        private const int SuccessExitCode = 0;
        private const int FailureExitCode = 1;

        private List<RepositoryInfo> _reposToProcess;
        private readonly string _dropFolder;
        private readonly string _buildBranch;
        private readonly string _outputPath;
        private readonly string _nugetPublishFeed;
        private readonly string _apiKey;

        public CoherenceBuild(
            List<RepositoryInfo> reposToProcess,
            string dropFolder,
            string buildBranch,
            string outputPath,
            string nugetPublishFeed,
            string apiKey)
        {
            _reposToProcess = reposToProcess;
            _dropFolder = dropFolder;
            _buildBranch = buildBranch;
            _outputPath = outputPath;
            _nugetPublishFeed = nugetPublishFeed;
            _apiKey = apiKey;
        }

        public CoherenceVerifyBehavior VerifyBehavior { get; set; } = CoherenceVerifyBehavior.All;

        public int Execute()
        {
            if (!Directory.Exists(_dropFolder))
            {
                Log.WriteError($"Drop share {_dropFolder} does not exist");
                return FailureExitCode;
            }

            Directory.CreateDirectory(_outputPath);
            var processedPackages = new ConcurrentBag<PackageInfo>();
            foreach (var repo in _reposToProcess)
            {
                Program.Retry(() =>
                {
                    ProcessRepo(processedPackages, repo);
                });
            }

            var coherenceVerify = new CoherenceVerifier(processedPackages, VerifyBehavior);
            if (!coherenceVerify.VerifyAll())
            {
                return FailureExitCode;
            }

            if (!string.IsNullOrEmpty(_nugetPublishFeed))
            {
                PackagePublisher.PublishToFeed(processedPackages, _nugetPublishFeed, _apiKey);
            }

            return SuccessExitCode;
        }

        private void ProcessRepo(ConcurrentBag<PackageInfo> processedPackages, RepositoryInfo repo)
        {
            var repoDirectory = Path.Combine(_dropFolder, repo.Name, _buildBranch);
            if (string.IsNullOrEmpty(repo.BuildNumber))
            {
                repo.BuildNumber = FindLatest(repoDirectory);
            }

            repoDirectory = Path.Combine(repoDirectory, repo.BuildNumber);

            var buildDirectory = Path.Combine(repoDirectory, repo.BuildDirectory);
            var packageTargetDir = Path.Combine(_outputPath, repo.PackagesDestinationDirectory);
            var symbolsTargetDir = Path.Combine(_outputPath, "symbols");
            var buildTargetDirectory = Path.Combine(_outputPath, "build");

            Directory.CreateDirectory(symbolsTargetDir);
            Directory.CreateDirectory(packageTargetDir);
            Directory.CreateDirectory(buildTargetDirectory);

            Parallel.ForEach(Directory.GetFiles(buildDirectory, "*"), file =>
            {
                var fileName = Path.GetFileName(file);

                if (file.EndsWith(".symbols.nupkg", StringComparison.OrdinalIgnoreCase))
                {
                    var targetPath = Path.Combine(symbolsTargetDir, fileName);
                    File.Copy(file, targetPath, overwrite: true);
                    return;
                }

                var buildTargetPath = Path.Combine(buildTargetDirectory, fileName);
                File.Copy(file, buildTargetPath, overwrite: true);
                if (file.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
                {
                    var targetPath = Path.Combine(packageTargetDir, fileName);
                    File.Copy(file, targetPath, overwrite: true);

                    var packageInfo = new PackageInfo
                    {
                        IsPartnerPackage = repo.Name == "CoreCLR",
                        PackagePath = targetPath,
                    };
                    using (var fileStream = File.OpenRead(packageInfo.PackagePath))
                    using (var reader = new PackageArchiveReader(fileStream))
                    {
                        packageInfo.Identity = reader.GetIdentity();
                        packageInfo.PackageDependencyGroups = reader.GetPackageDependencies();
                    }

                    processedPackages.Add(packageInfo);
                }
            });
        }

        private static string FindLatest(string repoDirectory)
        {
            if (!Directory.Exists(repoDirectory))
            {
                return null;
            }

            return new DirectoryInfo(repoDirectory)
                .EnumerateDirectories()
                .Select(d =>
                {
                    int buildNumber;
                    if (!int.TryParse(d.Name, out buildNumber))
                    {
                        buildNumber = int.MinValue;
                    }

                    return new
                    {
                        DirectoryInfo = d,
                        BuildNumber = buildNumber
                    };
                })
                .OrderByDescending(r => r.BuildNumber)
                .Select(r => r.DirectoryInfo.Name)
                .FirstOrDefault();
        }

    }
}
