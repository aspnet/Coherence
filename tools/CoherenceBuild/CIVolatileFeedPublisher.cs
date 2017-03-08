using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CoherenceBuild
{
    public static class CIVolatileFeedPublisher
    {
        public static void CleanupVolatileFeed(
            string outputPackagesDir,
            string volatileShare)
        {
            var latestPackageFolders = Directory.GetDirectories(volatileShare);
            var coherentPackageNames = Directory.GetFiles(outputPackagesDir, "*.nupkg")
                .Select(p => Path.GetFileNameWithoutExtension(p));

            var delete = new List<string>();
            foreach (var latestPackageFolder in latestPackageFolders)
            {
                var projectName = Path.GetFileName(latestPackageFolder);

                var matchedVersion = new HashSet<string>(
                    coherentPackageNames
                        .Where(packageName => packageName.StartsWith(projectName, StringComparison.OrdinalIgnoreCase))
                        .Select(packageName => packageName.Substring(projectName.Length + 1)),
                    StringComparer.OrdinalIgnoreCase);

                if (matchedVersion.Any())
                {
                    // Retain all versions of packages published since the Coherent package was published.
                    var directoryInfo = new DirectoryInfo(latestPackageFolder);
                    var versionsToDelete = directoryInfo.EnumerateDirectories()
                        .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                        .TakeWhile(d => !matchedVersion.Contains(d.Name))
                        .Select(d => d.FullName);
                    delete.AddRange(versionsToDelete);
                }
                else
                {
                    delete.Add(latestPackageFolder);
                    Log.WriteInformation("Project {0} doesn't exist in this Coherence build hence it will be deleted from {1}",
                        projectName, outputPackagesDir);
                }
            }

            foreach (var d in delete)
            {
                Log.WriteInformation(string.Format("Delete folder {0}", d));

                try
                {
                    Directory.Delete(d, recursive: true);
                }
                catch
                {
                }
            }
        }
    }
}
