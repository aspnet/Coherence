using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Packaging.Core;
using NuGet.Protocol.Core.Types;
using NuGet.Protocol.Core.v3;

namespace CoherenceBuild
{
    public static class PackagePublisher
    {
        public static void PublishToShare(
            ProcessResult processResult,
            string outputPath,
            string symbolsOutputPath)
        {
            Directory.CreateDirectory(outputPath);
            Directory.CreateDirectory(symbolsOutputPath);
            var packagesToCopy = processResult.AllPackages.Values;

            Parallel.ForEach(packagesToCopy, new ParallelOptions { MaxDegreeOfParallelism = 4 }, packageInfo =>
            {
                var packagePath = Path.Combine(outputPath, Path.GetFileName(packageInfo.PackagePath));

                Program.Retry(() =>
                {
                    File.Copy(packageInfo.PackagePath, packagePath, overwrite: true);
                    // Update package path to point to local share.
                    packageInfo.PackagePath = packagePath;
                });

                if (File.Exists(packageInfo.SymbolsPath))
                {
                    Program.Retry(() =>
                    {
                        File.Copy(
                            packageInfo.SymbolsPath,
                            Path.Combine(symbolsOutputPath, Path.GetFileName(packageInfo.SymbolsPath)),
                            overwrite: true);
                    });
                }

                Log.WriteInformation("Copied to {0}", packagePath);
            });
        }

        public static void PublishToFeed(ProcessResult processResult, string feed, string apiKey)
        {
            PublishToFeedAsync(processResult, feed, apiKey).Wait();
        }

        private static async Task PublishToFeedAsync(ProcessResult processResult, string feed, string apiKey)
        {
            var packagesToPushInOrder = Enumerable.Concat(
                 processResult.CoreCLRPackages.Values,
                 processResult.ProductPackages.OrderBy(p => p.Value.Degree).Select(p => p.Value));

            using (var semaphore = new SemaphoreSlim(4))
            {
                var resource = await GetMetadataResourceAsync(feed);
                var packageUpdateResource = new PackageUpdateResource(feed, httpSource: null);
                var tasks = packagesToPushInOrder.Select(async package =>
                {
                    await semaphore.WaitAsync(TimeSpan.FromMinutes(3));
                    try
                    {
                        if (!package.IsCoherencePackage && await IsAlreadyUploadedAsync(resource, package.Identity))
                        {
                            Log.WriteInformation($"Skipping {package.Identity} since it is already published.");
                            return;
                        }

                        var attempt = 0;
                        while (attempt < 10)
                        {
                            attempt++;
                            Log.WriteInformation($"Attempting to publish package {package.Identity} (Attempt: {attempt})");
                            try
                            {
                                await packageUpdateResource.Push(
                                    package.PackagePath,
                                    symbolsSource: null,
                                    timeoutInSecond: 30,
                                    disableBuffering: false,
                                    getApiKey: _ => apiKey,
                                    log: NullLogger.Instance);
                                Log.WriteInformation($"Done publishing package {package.Identity}");
                                return;
                            }
                            catch (Exception ex) when (attempt < 9)
                            {
                                Log.WriteInformation($"Attempt {(10 - attempt)} failed.{Environment.NewLine}{ex}{Environment.NewLine}Retrying...");
                            }
                        }
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                await Task.WhenAll(tasks);
            }
        }

        private static Task<MetadataResource> GetMetadataResourceAsync(string feed)
        {
            var settings = Settings.LoadDefaultSettings(
                Directory.GetCurrentDirectory(),
                configFileName: null,
                machineWideSettings: null);
            var sourceRepositoryProvider = new SourceRepositoryProvider(
                new PackageSourceProvider(settings),
                FactoryExtensionsV2.GetCoreV3(Repository.Provider));

            feed = feed.TrimEnd('/') + "/api/v3/index.json";
            var sourceRepository = sourceRepositoryProvider.CreateRepository(new PackageSource(feed));
            return sourceRepository.GetResourceAsync<MetadataResource>();
        }

        private static async Task<bool> IsAlreadyUploadedAsync(MetadataResource resource, PackageIdentity packageId)
        {
            if (resource == null)
            {
                // If we couldn't locate the v3 feed, republish the packages
                return false;
            }

            try
            {
                return await resource.Exists(packageId, NullLogger.Instance, default(CancellationToken));
            }
            catch (Exception ex)
            {
                // If we can't read feed info, republish the packages
                var exceptionMessage = (ex?.InnerException ?? ex.GetBaseException()).Message;
                Log.WriteInformation($"Failed to read package existence {Environment.NewLine}{exceptionMessage}.");
                return false;
            }
        }
    }
}
