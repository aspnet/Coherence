using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Dnx.Runtime.Common.CommandLine;
using NuGet;

namespace CoherenceBuild
{
    class Program
    {
        private static readonly string[] ReposToSkip = new[]
        {
            "Coherence",
            "Coherence-External",
            "Coherence-Signed",
            "Coherence-Signed-External",
            "Data",
            "DiagnosticsPages",
            "dnvm",
            "DNX-Darwin",
            "DNX-Linux",
            "DnxTools",
            "docfx",
            "docfx-signed",
            "Entropy",
            "Glimpse",
            "Helios",
            "HttpClient",
            "IBC",
            "latest-dev",
            "latest-packages",
            "DataCommon.SQLite",
            "MusicStore",
            "NuGet.Packaging",
            "NuGet.Versioning",
            "ServerTests",
            "Setup",
            "Setup-Osx-Pkg",
            "SqlClient",
            "Stress",
            "System.Data.Common",
            "Templates",
            "WebHooks",
            "WebHooks-Signed",
            "WebSocketAbstractions",
            "xunit-performance"
        };

        static int Main(string[] args)
        {
            var app = new CommandLineApplication();
            var dropFolder = app.Option("--drop-folder", "Drop folder", CommandOptionType.SingleValue);
            var buildBranch = app.Option("--build-branch", "Build branch (dev \\ release)", CommandOptionType.SingleValue);
            var outputPath = app.Option("--output-path", "Output path", CommandOptionType.SingleValue);
            var symbolSourcePath = app.Option("--symbols-source-path", "Symbol source path", CommandOptionType.SingleValue);
            var symbolsOutputPath = app.Option("--symbols-output-path", "Symbols output path", CommandOptionType.SingleValue);
            var symbolsNuGetExe = app.Option("--symbols-nuget-exe", "Symbols NuGet exe", CommandOptionType.SingleValue);
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

                PackagePublisher.PublishSymbolsPackages(
                    outputPath.Value(),
                    symbolsOutputPath.Value(),
                    symbolSourcePath.Value(),
                    symbolsNuGetExe.Value(),
                    processResult);

                if (nugetPublishFeed.HasValue())
                {
                    PackagePublisher.PublishNuGetPackages(processResult, nugetPublishFeed.Value(), apiKey.Value());
                }

                CIVolatileFeedPublisher.CleanupVolatileFeed(processResult, ciVolatileShare.Value());

                return 0;
            });

            return app.Execute(args);
        }

        private static ProcessResult ReadPackagesToProcess(DirectoryInfo di, string buildBranch)
        {
            var processResult = new ProcessResult();

            foreach (var projectFolder in di.EnumerateDirectories())
            {
                if (ReposToSkip.Contains(projectFolder.Name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var latestPath = FindLatest(projectFolder, buildBranch);

                if (!Directory.Exists(latestPath))
                {
                    Log.WriteError("Couldn't find latest for {0}", latestPath);
                    continue;
                }

                Console.WriteLine("Using {0}", latestPath);

                var build = new DirectoryInfo(Path.Combine(latestPath, "build"));

                if (!build.Exists)
                {
                    Log.WriteError("Can't find build dir for {0}", projectFolder.Name);
                    continue;
                }

                var isCoreCLR = projectFolder.Name.Equals("CoreCLR", StringComparison.OrdinalIgnoreCase);

                foreach (var packageInfo in build.EnumerateFiles("*.nupkg"))
                {
                    if (packageInfo.FullName.EndsWith(".symbols.nupkg", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    Console.WriteLine("Processing " + packageInfo + "...");

                    string symbolsPath = Path.Combine(packageInfo.Directory.FullName,
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

                        if (isCoreCLR)
                        {
                            List<PackageInfo> clrPackages;
                            if (!processResult.CoreCLRPackages.TryGetValue(zipPackage.Id, out clrPackages))
                            {
                                clrPackages = new List<PackageInfo>();
                                processResult.CoreCLRPackages[zipPackage.Id] = clrPackages;
                            }

                            clrPackages.Add(info);
                        }
                        else
                        {
                            processResult.ProductPackages[zipPackage.Id] = info;
                        }
                    });
                }
            }

            return processResult;
        }

        private static string FindLatest(DirectoryInfo projectFolder, string buildBranch)
        {
            var latestPath = Path.Combine(projectFolder.FullName, buildBranch);

            if (!Directory.Exists(latestPath))
            {
                return latestPath;
            }

            return new DirectoryInfo(latestPath)
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
