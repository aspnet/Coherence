using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NuGet;

namespace SanityCheck
{
    class Program
    {
        static int Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: SanityCheck [dropFolder] [outputPath]");
                return 1;
            }

            string dropFolder = args[0];
            string outputPath = args[1];

            var di = new DirectoryInfo(dropFolder);

            var packages = new Dictionary<string, PackageInfo>(StringComparer.OrdinalIgnoreCase);

            var projectsToAllowLooseDependencies = new[]{
                "CoreCLR"
            };

            var projectsToSkip = new[] {
                "KRuntime",
                "Coherence"
            };

            foreach (var projectFolder in di.EnumerateDirectories())
            {
                if (projectsToSkip.Contains(projectFolder.Name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var latestPath = Path.Combine(projectFolder.FullName, "dev", "Latest");

                if (!Directory.Exists(latestPath))
                {
                    WriteError("Couldn't find latest for {0}", latestPath);
                    continue;
                }

                var build = new DirectoryInfo(Path.Combine(latestPath, "build"));

                if (!build.Exists)
                {
                    WriteError("Can't find build dir for {0}", projectFolder.Name);
                    continue;
                }

                bool allowLooseDependency = projectsToAllowLooseDependencies.Contains(projectFolder.Name,
                                                                                      StringComparer.OrdinalIgnoreCase);

                foreach (var packageInfo in build.EnumerateFiles("*.nupkg"))
                {
                    Console.WriteLine("Processing " + packageInfo + "...");

                    Retry(() =>
                    {
                        var zipPackage = new ZipPackage(packageInfo.FullName);
                        packages[zipPackage.Id] = new PackageInfo
                        {
                            Package = zipPackage,
                            PackagePath = packageInfo.FullName,
                            AllowMismatchedDependency = allowLooseDependency
                        };
                    });
                }
            }

            if (!VerifyAll(packages))
            {
                return 1;
            }

            Directory.CreateDirectory(outputPath);

            foreach (var packageInfo in packages.Values.Where(pi => pi.Success))
            {
                var path = Path.Combine(outputPath, Path.GetFileName(packageInfo.PackagePath));

                Retry(() =>
                {
                    File.Copy(packageInfo.PackagePath, outputPath, overwrite: true);
                });

                Console.WriteLine("Copied to {0}", outputPath);
            }

            return 0;
        }

        private static bool VerifyAll(Dictionary<string, PackageInfo> universe)
        {
            foreach (var packageInfo in universe.Values)
            {
                Visit(packageInfo, universe);
            }

            bool success = true;

            foreach (var packageInfo in universe.Values)
            {
                var mismatches = packageInfo.DependencyMismatches.Where(d => !d.Info.AllowMismatchedDependency)
                                                                 .ToList();
                if (mismatches.Any())
                {
                    WriteError("{0} has mismatched dependencies:", packageInfo.Package.GetFullName());

                    foreach (var mismatch in mismatches)
                    {
                        WriteError("    Expected {0}({1}) but got {2}",
                            mismatch.Dependency,
                            (mismatch.TargetFramework == VersionUtility.UnsupportedFrameworkName ?
                            "k10" :
                            VersionUtility.GetShortFrameworkName(mismatch.TargetFramework)),
                            mismatch.Info.Package.Version);

                        success = false;
                    }
                }
            }

            return success;
        }

        private static void WriteWarning(string value, params object[] args)
        {
            Console.WriteLine(value, args);
        }

        private static void WriteError(string value, params object[] args)
        {
            if (Environment.GetEnvironmentVariable("TEAMCITY_VERSION") != null)
            {
                Console.Error.WriteLine("##teamcity[message text='" + value + "' status='ERROR']", args);
            }
            else
            {
                Console.Error.WriteLine(value, args);
            }
        }

        private static void Visit(PackageInfo packageInfo, Dictionary<string, PackageInfo> universe)
        {
            foreach (var dependencySet in packageInfo.Package.DependencySets)
            {
                foreach (var dependency in dependencySet.Dependencies)
                {
                    // For any dependency in the universe
                    PackageInfo dependencyPackageInfo;
                    if (universe.TryGetValue(dependency.Id, out dependencyPackageInfo))
                    {
                        if (dependencyPackageInfo.Package.Version !=
                            dependency.VersionSpec.MinVersion)
                        {
                            // Add a mismatch if the min version doesn't work out
                            // (we only really care about >= minVersion)
                            packageInfo.DependencyMismatches.Add(new DependencyMismatch
                            {
                                Dependency = dependency,
                                TargetFramework = dependencySet.TargetFramework,
                                Info = dependencyPackageInfo
                            });
                        }
                    }
                }
            }
        }

        private static void Retry(Action action)
        {
            int attempts = 3;
            while (true)
            {
                try
                {
                    action();
                    break;
                }
                catch (FileNotFoundException ex)
                {
                    attempts--;

                    if (attempts == 0)
                    {
                        throw;
                    }

                    Console.WriteLine(ex);
                    Console.WriteLine("Retrying...");
                    Thread.Sleep(3000);
                }
            }
        }

        public class PackageInfo
        {
            // Some packages we're not as strict about
            public bool AllowMismatchedDependency { get; set; }

            // The actual package instance
            public IPackage Package { get; set; }

            // The path to this package
            public string PackagePath { get; set; }

            public bool Success
            {
                get
                {
                    return DependencyMismatches.Count == 0 ||
                           DependencyMismatches.All(d => d.Info.AllowMismatchedDependency);
                }
            }

            public IList<DependencyMismatch> DependencyMismatches { get; set; }

            public PackageInfo()
            {
                DependencyMismatches = new List<DependencyMismatch>();
            }
        }

        public class DependencyMismatch
        {
            public PackageDependency Dependency { get; set; }
            public PackageInfo Info { get; set; }
            public FrameworkName TargetFramework { get; set; }
        }
    }
}
