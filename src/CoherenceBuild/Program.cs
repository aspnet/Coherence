using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.CommandLineUtils;
using NuGet;

namespace CoherenceBuild
{
    class Program
    {
        // Will store the path to the network share used
        private static readonly IDictionary<string, string> repositories = new[]
        {
            "aspnet.xunit",
            "CoreCLR",
            "DNX",
            "libuv-build-windows",
            "Roslyn",
            "SignalR-Client-Cpp",
            "UniverseCoherence",
        }.ToDictionary(r => r, r => (string)null);

        // Extra files and folders that we want from each repository
        private static readonly IDictionary<string, FileSystemDependency[]> fileDependencies = new Dictionary<string, FileSystemDependency[]>
        {
            ["CoreCLR"] = new FileSystemDependency[]
            {
                new FolderDependecy("netcoresdk") { Optional = true }
            },
            ["UniverseCoherence"] = new FileSystemDependency[]
            {
                new FileDependency("commits") { Destination = "commits-universe" }
            },
        };

        static int Main(string[] args)
        {
            var app = new CommandLineApplication();
            var dropFolder = app.Option("--drop-folder", "Drop folder", CommandOptionType.SingleValue);
            var buildBranch = app.Option("--build-branch", "Build branch (dev \\ release)", CommandOptionType.SingleValue);
            var outputPath = app.Option("--output-path", "Output path", CommandOptionType.SingleValue);
            var nugetPublishFeed = app.Option("--nuget-publish-feed", "Feed to push packages to", CommandOptionType.SingleValue);
            var apiKey = app.Option("--apikey", "NuGet API Key", CommandOptionType.SingleValue);
            var ciVolatileShare = app.Option("--ci-volatile-share", "CI Volatile share", CommandOptionType.SingleValue);

            app.OnExecute(() =>
            {
                var buildFolderPath = Path.Combine(outputPath.Value(), "build");
                var symbolsFolderPath = Path.Combine(outputPath.Value(), "symbols");
                var volatileFolderPath = ciVolatileShare.HasValue() ?
                    ciVolatileShare.Value() :
                    Path.Combine(dropFolder.Value(), "latest-packages", buildBranch.Value());

                Log.WriteInformation("Build output folder: " + buildFolderPath);
                Log.WriteInformation("Symbolds output folder: " + symbolsFolderPath);
                Log.WriteInformation("Volatile folder: " + volatileFolderPath);

                var di = new DirectoryInfo(dropFolder.Value());

                if (!di.Exists)
                {
                    Log.WriteError("Drop share {0} does not exist", di.FullName);
                    return 1;
                }

                var processResult = ReadPackagesToProcess(di, buildBranch.Value());
                var disableCoherenceCheck = Environment.GetEnvironmentVariable("DISABLE_COHERENCE_CHECK") == "true";
                if (!disableCoherenceCheck && !CoherenceVerifier.VerifyAll(processResult))
                {
                    return 1;
                }

                CopyFileDependencies(outputPath.Value());
                WriteReposUsedFile(outputPath.Value());

                PackagePublisher.PublishToShare(processResult, buildFolderPath, symbolsFolderPath);

                if (nugetPublishFeed.HasValue() && !string.IsNullOrEmpty(nugetPublishFeed.Value()))
                {
                    PackagePublisher.PublishToFeed(processResult, nugetPublishFeed.Value(), apiKey.Value());
                }

                CIVolatileFeedPublisher.CleanupVolatileFeed(buildFolderPath, volatileFolderPath);

                return 0;
            });

            return app.Execute(args);
        }

        private static void WriteReposUsedFile(string destination)
        {
            var filePath = Path.Combine(destination, "packages-sources");
            var fileContent = string.Join("\n", repositories.Select(r => $"{r.Key}: {r.Value}"));
            File.WriteAllText(filePath, fileContent);
        }

        private static void CopyFileDependencies(string destination)
        {
            foreach (var repoFileDeps in fileDependencies)
            {
                var repoPath = repositories[repoFileDeps.Key];

                foreach (var fileDep in repoFileDeps.Value)
                {
                    fileDep.Copy(repoPath, destination);
                }
            }
        }

        private static ProcessResult ReadPackagesToProcess(DirectoryInfo di, string buildBranch)
        {
            var processResult = new ProcessResult();
            var dictionaryLock = new object();
            foreach (var repo in repositories.Keys.ToArray())
            {
                var repoDirectory = Path.Combine(di.FullName, repo, buildBranch);
                var latestPath = FindLatest(repoDirectory, buildBranch);

                if (latestPath == null)
                {
                    Log.WriteError("Couldn't find latest for {0}", latestPath);
                    continue;
                }

                Log.WriteInformation("Using {0}", latestPath);

                var build = new DirectoryInfo(Path.Combine(latestPath, "build"));

                if (!build.Exists)
                {
                    Log.WriteError("Can't find build dir for {0}", repo);
                    continue;
                }

                repositories[repo] = latestPath;

                var isCoreCLR = repo.Equals("CoreCLR", StringComparison.OrdinalIgnoreCase);
                var isCoherencePackage = repo.Equals("UniverseCoherence", StringComparison.OrdinalIgnoreCase);
                var isDnxPackage = repo.Equals("Dnx", StringComparison.OrdinalIgnoreCase);

                Parallel.ForEach(build.GetFiles("*.nupkg", SearchOption.AllDirectories), packageInfo =>
                {
                    if (packageInfo.FullName.EndsWith(".symbols.nupkg", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    if (packageInfo.Name.StartsWith("MusicStore", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    Log.WriteInformation("Processing " + packageInfo + "...");

                    string symbolsPath = Path.Combine(
                        packageInfo.Directory.FullName,
                        Path.GetFileNameWithoutExtension(packageInfo.Name) + ".symbols.nupkg");

                    Retry(() =>
                    {
                        var zipPackage = new ZipPackage(packageInfo.FullName);

                        var info = new PackageInfo
                        {
                            Package = zipPackage,
                            PackagePath = packageInfo.FullName,
                            SymbolsPath = symbolsPath,
                            IsCoreCLRPackage = isCoreCLR,
                            IsCoherencePackage = isCoherencePackage,
                            IsDnxPackage = isDnxPackage,
                        };

                        lock (dictionaryLock)
                        {
                            if (isCoreCLR)
                            {
                                processResult.CoreCLRPackages[zipPackage.Id] = info;
                            }
                            else
                            {
                                processResult.ProductPackages[zipPackage.Id] = info;
                            }

                            processResult.AllPackages[zipPackage.Id] = info;
                        }
                    });
                });
            }

            return processResult;
        }

        private static string FindLatest(string repoDirectory, string buildBranch)
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
                .Select(r => r.DirectoryInfo.FullName)
                .FirstOrDefault();
        }

        public static void Retry(Action action)
        {
            int attempts = 10;
            while (true)
            {
                try
                {
                    action();
                    break;
                }
                catch
                {
                    attempts--;

                    if (attempts == 0)
                    {
                        throw;
                    }

                    Log.WriteInformation("Retrying...");
                    Thread.Sleep(3000);
                }
            }
        }
    }
}
