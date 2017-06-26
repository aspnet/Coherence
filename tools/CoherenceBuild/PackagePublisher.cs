using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Common;
using NuGet.Packaging;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;

namespace CoherenceBuild
{
    public static class PackagePublisher
    {
        private const int _maxRetryCount = 5;
        private const int _maxParallelPackagePushes = 4;
        private static readonly TimeSpan _packagePushTimeout = TimeSpan.FromSeconds(180);
        private static readonly CancellationTokenSource _packagePushCancellationTokenSource = new CancellationTokenSource();

        public static void PublishToFeed(IEnumerable<PackageInfo> processedPackages, string feed, string apiKey)
        {
            PublishToFeedAsync(processedPackages, feed, apiKey).Wait();
        }

        public static void ExpandPackageFiles(IEnumerable<PackageInfo> processedPackages, string expandDirectory)
        {
            Parallel.ForEach(processedPackages, package =>
            {
                Log.WriteInformation($"Expanding {package.Identity}.");
                using (var inputStream = File.OpenRead(package.PackagePath))
                {
                    var versionFolderPathContext = new VersionFolderPathContext(
                        package.Identity,
                        expandDirectory,
                        NullLogger.Instance,
                        packageSaveMode: PackageSaveMode.Nupkg | PackageSaveMode.Nuspec,
                        xmlDocFileSaveMode: XmlDocFileSaveMode.Skip);

                    PackageExtractor.InstallFromSourceAsync(
                        inputStream.CopyToAsync,
                        versionFolderPathContext,
                        default(CancellationToken)).Wait();
                }
            });
        }

        private static async Task PublishToFeedAsync(
            IEnumerable<PackageInfo> processedPackages,
            string feed,
            string apiKey)
        {
            var sourceRepository = Repository.Factory.GetCoreV3(feed, FeedType.HttpV3);
            var metadataResource = await sourceRepository.GetResourceAsync<MetadataResource>();
            var packageUpdateResource = await sourceRepository.GetResourceAsync<PackageUpdateResource>();

            // Group packages to push by degree.
            var packageGroups = processedPackages.GroupBy(p => p.Degree).OrderBy(g => g.Key);

            var concurrentBag = new ConcurrentBag<PackageInfo>();
            var tasks = new Task[_maxParallelPackagePushes];
            foreach (var packageGroup in packageGroups)
            {
                foreach (var package in packageGroup)
                {
                    concurrentBag.Add(package);
                }

                for (var i = 0; i < tasks.Length; i++)
                {
                    tasks[i] = PushPackagesAsync(packageUpdateResource, concurrentBag, apiKey);
                }
                await Task.WhenAll(tasks);
            }
        }

        private static async Task PushPackagesAsync(
            PackageUpdateResource packageUpdateResource,
            ConcurrentBag<PackageInfo> concurrentBag,
            string apiKey)
        {
            while (concurrentBag.TryTake(out var package))
            {
                await PushPackageAsync(packageUpdateResource, package, apiKey);
            }
        }

        private static async Task PushPackageAsync(
            PackageUpdateResource packageUpdateResource,
            PackageInfo package,
            string apiKey)
        {
            for (var attempt = 1; attempt <= _maxRetryCount; attempt++)
            {
                // Fail fast if a parallel push operation has already failed
                _packagePushCancellationTokenSource.Token.ThrowIfCancellationRequested();

                Log.WriteInformation($"Attempting to publish package {package.Identity} (Attempt: {attempt})");

                try
                {
                    await packageUpdateResource.Push(
                        package.PackagePath,
                        symbolSource: null,
                        timeoutInSecond: (int)_packagePushTimeout.TotalSeconds,
                        disableBuffering: false,
                        getApiKey: _ => apiKey,
                        getSymbolApiKey: _ => null,
                        log: NullLogger.Instance);
                    Log.WriteInformation($"Done publishing package {package.Identity}");
                    return;
                }
                catch (Exception ex) when (attempt < _maxRetryCount) // allow exception to be thrown at the last attempt
                {
                    // Write in a single call as multiple WriteLine statements can get interleaved causing
                    // confusion when reading logs.
                    Log.WriteInformation(
                        $"Attempt {attempt} failed to publish package {package.Identity}." +
                        Environment.NewLine +
                        ex.ToString() +
                        Environment.NewLine +
                        "Retrying...");
                }
                catch
                {
                    _packagePushCancellationTokenSource.Cancel();
                    throw;
                }
            }
        }
    }
}
