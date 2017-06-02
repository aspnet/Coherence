using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
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

            GenerateDependenciesFile();

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

        private void GenerateDependenciesFile()
        {
            var project = new XElement("Project");
            var propertyGroup = new XElement("PropertyGroup");
            project.Add(propertyGroup);

            var universeCoherence = _reposToProcess.First(repo => string.Equals(repo.Name, "UniverseCoherence", StringComparison.OrdinalIgnoreCase));
            var buildDirectory = GetBuildDirectory(universeCoherence);
            foreach (var nugetPackageFile in Directory.GetFiles(buildDirectory, "microsoft.aspnetcore.mvc.core*.nupkg"))
            {
                if (!nugetPackageFile.EndsWith(".symbols.nupkg", StringComparison.OrdinalIgnoreCase))
                {
                    var packageInfo = GetPackageIdAndVersion(nugetPackageFile);
                    var element = new XElement("AspNetCoreVersion", packageInfo.Item2);
                    propertyGroup.Add(element);
                    break;
                }
            }

            var coreClr = _reposToProcess.First(repo => string.Equals(repo.Name, "CoreCLR", StringComparison.OrdinalIgnoreCase));
            buildDirectory = GetBuildDirectory(coreClr);
            foreach (var nugetPackageFile in Directory.GetFiles(buildDirectory, "*.nupkg"))
            {
                if (!nugetPackageFile.EndsWith(".symbols.nupkg", StringComparison.OrdinalIgnoreCase))
                {
                    var packageInfo = GetPackageIdAndVersion(nugetPackageFile);

                    // Even though having a '.' is valid in an xml element, dotnet restore does not like it, so
                    // replace it.
                    var elementName = packageInfo.Item1.Replace(".", "-");
                    var element = new XElement(elementName, packageInfo.Item2);
                    propertyGroup.Add(element);
                }
            }

            using (var streamWriter = new StreamWriter(File.Create(Path.Combine(_outputPath, "dependencies.props"))))
            {
                streamWriter.WriteLine(project.ToString());
            }
        }

        private string GetBuildDirectory(RepositoryInfo repo)
        {
            var repoDirectory = Path.Combine(_dropFolder, repo.Name, _buildBranch);
            if (string.IsNullOrEmpty(repo.BuildNumber))
            {
                repo.BuildNumber = FindLatest(repoDirectory);
            }
            repoDirectory = Path.Combine(repoDirectory, repo.BuildNumber);
            return Path.Combine(repoDirectory, repo.BuildDirectory);
        }

        private Tuple<string, string> GetPackageIdAndVersion(string nugetPackageFile)
        {
            using (var fileStream = File.OpenRead(nugetPackageFile))
            using (var reader = new PackageArchiveReader(fileStream))
            {
                var packageIdentity = reader.GetIdentity();
                return new Tuple<string, string>(packageIdentity.Id, packageIdentity.Version.ToNormalizedString());
            }
        }
    }
}
