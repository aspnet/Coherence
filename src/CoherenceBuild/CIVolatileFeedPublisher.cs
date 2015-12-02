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
                        .Where(packageName => packageName.IndexOf(projectName) == 0)
                        .Select(packageName => packageName.Substring(projectName.Length + 1)));

                if (matchedVersion.Any())
                {
                    delete.AddRange(Directory.GetDirectories(latestPackageFolder)
                                            .Where(p => !matchedVersion.Contains(Path.GetFileName(p))));
                }
                else
                {
                    delete.Add(latestPackageFolder);
                    Console.WriteLine("Project {0} doesn't exist in this Coherence build hence it will be deleted from {1}",
                        projectName, outputPackagesDir);
                }
            }

            foreach (var d in delete)
            {
                Console.WriteLine(string.Format("Delete folder {0}", d));

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
