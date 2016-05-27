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

            var expandDirectory = Path.Combine(_outputPath, "packages-expanded");
            Log.WriteInformation($"Expanding packages to {expandDirectory}");
            Directory.CreateDirectory(expandDirectory);
            PackagePublisher.ExpandPackageFiles(processedPackages, expandDirectory);

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
            foreach (var dependency in repo.FileSystemDependencies)
            {
                dependency.Copy(repoDirectory, _outputPath);
            }

            var packageSourceDir = Path.Combine(repoDirectory, repo.PackageSourceDir);

            var packageTargetDir = Path.Combine(_outputPath, repo.PackageDestinationDir);
            var symbolsTargetDir = Path.Combine(_outputPath, "symbols");
            Directory.CreateDirectory(symbolsTargetDir);
            Directory.CreateDirectory(packageTargetDir);

            Parallel.ForEach(Directory.GetFiles(packageSourceDir, "*.nupkg"), packagePath =>
            {
                var packageFileName = Path.GetFileName(packagePath);
                var packageInfo = new PackageInfo
                {
                    IsPartnerPackage = repo.Name == "CoreCLR",
                    PackagePath = Path.Combine(packageTargetDir, packageFileName),
                };

                using (var fileStream = File.OpenRead(packagePath))
                using (var reader = new PackageArchiveReader(fileStream))
                {
                    packageInfo.Identity = reader.GetIdentity();
                    packageInfo.PackageDependencyGroups = reader.GetPackageDependencies();
                }

                if (repo.PackagesToSkip.Contains(packageInfo.Identity.Id))
                {
                    Log.WriteInformation($"Skipping package {packagePath}");
                    return;
                }

                if (packagePath.EndsWith(".symbols.nupkg"))
                {
                    var targetPath = Path.Combine(symbolsTargetDir, packageFileName);
                    File.Copy(packagePath, targetPath, overwrite: true);
                }
                else
                {
                    File.Copy(packagePath, packageInfo.PackagePath, overwrite: true);
                    processedPackages.Add(packageInfo);
                }
            });
        }

        private void WriteReposUsedFile()
        {
            var filePath = Path.Combine(_outputPath, "packages-sources");
            var fileContent = string.Join("\n", _reposToProcess.Select(r => $"{r.Name}: {r.BuildNumber}"));
            File.WriteAllText(filePath, fileContent);
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
