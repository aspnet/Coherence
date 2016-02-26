using System;
using System.Collections.Generic;
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
        private static readonly string[] ReposToScan = new[]
        {
            "UniverseCoherence",
            "CoreCLR",
            "Roslyn",
            "libuv-build-windows",
            "SignalR-Client-Cpp",
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

                PackagePublisher.PublishToShare(processResult, outputPath.Value());

                if (nugetPublishFeed.HasValue() && !string.IsNullOrEmpty(nugetPublishFeed.Value()))
                {
                    PackagePublisher.PublishToFeed(processResult, nugetPublishFeed.Value(), apiKey.Value());
                }

                CIVolatileFeedPublisher.CleanupVolatileFeed(outputPath.Value(), ciVolatileShare.Value());

                return 0;
            });

            return app.Execute(args);
        }

        private static ProcessResult ReadPackagesToProcess(DirectoryInfo di, string buildBranch)
        {
            var processResult = new ProcessResult();
            var dictionaryLock = new object();
            foreach (var repo in ReposToScan)
            {
                var repoDirectory = Path.Combine(di.FullName, repo, buildBranch);
                var latestPath = FindLatest(repoDirectory, buildBranch);

                if (latestPath == null)
                {
                    Log.WriteError("Couldn't find latest for {0}", latestPath);
                    continue;
                }

                Console.WriteLine("Using {0}", latestPath);

                var build = new DirectoryInfo(Path.Combine(latestPath, "build"));

                if (!build.Exists)
                {
                    Log.WriteError("Can't find build dir for {0}", repo);
                    continue;
                }

                var isCoreCLR = repo.Equals("CoreCLR", StringComparison.OrdinalIgnoreCase);

                Parallel.ForEach(build.GetFiles("*.nupkg", SearchOption.AllDirectories),  packageInfo =>
                {
                    if (packageInfo.FullName.EndsWith(".symbols.nupkg", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    Console.WriteLine("Processing " + packageInfo + "...");

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
                            IsCoreCLRPackage = isCoreCLR
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

                    Console.WriteLine("Retrying...");
                    Thread.Sleep(3000);
                }
            }
        }
    }
}
