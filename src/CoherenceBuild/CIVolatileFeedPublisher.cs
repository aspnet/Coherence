using System;
using System.Collections.Generic;
using System.Linq;
using NuGet;

namespace CoherenceBuild
{
    public static class CIVolatileFeedPublisher
    {
        public static void CleanupVolatileFeed(
            ProcessResult processResult,
            string volatileShare)
        {
            var expandedPackageRepository = new ExpandedPackageRepository(new PhysicalFileSystem(volatileShare));
            var packagesToDelete = new List<IPackage>();
            foreach (var package in expandedPackageRepository.GetPackages())
            {
                List<PackageInfo> coherentPackageInfos;
                PackageInfo coherentPackageInfo;
                if (processResult.CoreCLRPackages.TryGetValue(package.Id, out coherentPackageInfos))
                {
                    if (coherentPackageInfos.Any(c => c.Package.Version <= package.Version))
                    {
                        // Allow packages in the volatile share to be higer than the version that is coherent.
                        // When we have multiple CoreCLR versions, only delete it if it is lower than all packages
                        // for that id.
                        continue;
                    }
                }
                else if (processResult.ProductPackages.TryGetValue(package.Id, out coherentPackageInfo))
                {
                    if (coherentPackageInfo.Package.Version <= package.Version)
                    {
                        continue;
                    }
                }

                // The package in the volatile share is older than the coherent package or is no longer being published.
                packagesToDelete.Add(package);
            }


            for (var i = 0; i < packagesToDelete.Count; i++)
            {
                try
                {
                    Console.WriteLine("Deleting package " + packagesToDelete[i]);
                    expandedPackageRepository.RemovePackage(packagesToDelete[i]);
                }
                catch
                {
                    // No-op
                }
            }
        }
    }
}
