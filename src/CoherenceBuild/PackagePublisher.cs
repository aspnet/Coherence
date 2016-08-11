using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;

namespace CoherenceBuild
{
    public static class PackagePublisher
    {
        public static void PublishToFeed(IEnumerable<PackageInfo> processedPackages, string feed, string apiKey)
        {
            PublishToFeedAsync(processedPackages, feed, apiKey).Wait();
        }

        public static void ExpandPackageFiles(IEnumerable<PackageInfo> processedPackages, string expandDirectory)
        {
            Parallel.ForEach (processedPackages, package =>
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

        private static async Task PublishToFeedAsync(IEnumerable<PackageInfo> processedPackages, string feed, string apiKey)
        {
            var packagesToPush = processedPackages.OrderBy(a => a.Degree);
            using (var semaphore = new SemaphoreSlim(4))
            {
                var sourceRepository = CreateSourceRepository(feed);

                var metadataResource = await sourceRepository.GetResourceAsync<MetadataResource>();
                var packageUpdateResource = await sourceRepository.GetResourceAsync<PackageUpdateResource>();
                var tasks = processedPackages.Select(async package =>
                {
                    await semaphore.WaitAsync(TimeSpan.FromMinutes(3));
                    try
                    {
                        if (package.IsPartnerPackage && await IsAlreadyUploadedAsync(metadataResource, package.Identity))
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
                                    symbolSource: null,
                                    timeoutInSecond: 30,
                                    disableBuffering: false,
                                    getApiKey: _ => apiKey,
                                    getSymbolApiKey: _ => null,
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

        private static SourceRepository CreateSourceRepository(string feed)
        {
            return Repository.CreateSource(Repository.Provider.GetCoreV3(), feed);
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
