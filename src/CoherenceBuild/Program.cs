using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.CommandLineUtils;

namespace CoherenceBuild
{
    class Program
    {
        static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--debug")
            {
                args = args.Skip(1).ToArray();
                System.Diagnostics.Debugger.Launch();
            }

            var app = new CommandLineApplication();
            var dropFolder = app.Option("--drop-folder", "The CI drop share.", CommandOptionType.SingleValue);
            var buildBranch = app.Option("--build-branch", "Build branch (dev \\ release)", CommandOptionType.SingleValue);
            var outputPath = app.Option("--output-path", "Output path", CommandOptionType.SingleValue);
            var nugetPublishFeed = app.Option("--nuget-publish-feed", "Feed to push packages to", CommandOptionType.SingleValue);
            var apiKey = app.Option("--api-key", "NuGet API Key", CommandOptionType.SingleValue);
            var ciVolatileShare = app.Option("--ci-volatile-share", "CI Volatile share", CommandOptionType.SingleValue);
            var universeCoherenceDropDir = app.Option(
                "--universecoherence-build",
                "Build number for UniverseCoherence.",
                CommandOptionType.SingleValue);

            var coreCLRDropDir = app.Option(
                "--coreclr-build",
                "Build number for CoreCLR",
                CommandOptionType.SingleValue);

            var disableProductPackageVerification = app.Option(
                "--disable-product-package-verification",
                "Enable sanity verification on AspNetCore packages",
                CommandOptionType.NoValue);

            var disablePartnerPackageVerification = app.Option(
                "--disable-partner-package-verification",
                "Enable sanity verification for partner packages",
                CommandOptionType.NoValue);

            app.OnExecute(() =>
            {
                var requiredFields = new[] { dropFolder, buildBranch, outputPath, universeCoherenceDropDir, coreCLRDropDir };

                var nullField = requiredFields.FirstOrDefault(f => !f.HasValue());
                if (nullField != null)
                {
                    Log.WriteError($"Missing argument value {nullField.Template}.");
                    app.ShowHelp();
                    return 1;
                }

                var repoInfos = new List<RepositoryInfo>
                {
                    new RepositoryInfo("UniverseCoherence", universeCoherenceDropDir.Value())
                    {
                        FileSystemDependencies =
                        {
                            new FileDependency("commits") { Destination = "commits-universe" },
                            new FolderDependecy(".build"),
                        },
                    },
                    new RepositoryInfo("CoreCLR", coreCLRDropDir.Value())
                    {
                        PackageDestinationDir = "ext",
                    },
                };

                var coherenceBuild = new CoherenceBuild(
                    repoInfos,
                    dropFolder.Value().Trim(),
                    buildBranch.Value(),
                    outputPath.Value().Trim(),
                    nugetPublishFeed.Value(),
                    apiKey.Value());

                if (disableProductPackageVerification.HasValue())
                {
                    coherenceBuild.VerifyBehavior &= CoherenceVerifyBehavior.All ^ CoherenceVerifyBehavior.ProductPackages;
                }

                if (disablePartnerPackageVerification.HasValue())
                {
                    coherenceBuild.VerifyBehavior &= CoherenceVerifyBehavior.All ^ CoherenceVerifyBehavior.PartnerPackages;
                }

                return coherenceBuild.Execute();
            });

            try
            {
                return app.Execute(args);
            }
            catch (Exception ex)
            {
                Log.WriteError(ex.ToString());
                return 1;
            }
        }

        public static void Retry(Action action)
        {
            var attempts = 10;
            while (true)
            {
                try
                {
                    action();
                    break;
                }
                catch (Exception ex) when (attempts-- > 1)
                {
                    Log.WriteInformation($"Attempt {(10 - attempts)} failed.{Environment.NewLine}{ex}{Environment.NewLine}Retrying...");
                    Thread.Sleep(3000);
                }
            }
        }
    }
}
