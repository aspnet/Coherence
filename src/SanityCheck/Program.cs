using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Threading;
using NuGet;

namespace SanityCheck
{
    class Program
    {
        static int Main(string[] args)
        {
            if (args.Length < 6)
            {
                Console.WriteLine("Usage: SanityCheck [dropFolder] [buildBranch] [outputPath] [symbolsOutputPath] [symbolSourcePath] [nugetExePath]");
                return 1;
            }

            string dropFolder = args[0];
            string buildBranch = args[1];
            string outputPath = args[2];
            string symbolsOutputPath = args[3];
            string symbolSourcePath = args[4];
            string nugetExePath = args[5];

            var di = new DirectoryInfo(dropFolder);

            if (!di.Exists)
            {
                WriteError("Drop share {0} does not exist", di.FullName);
                return 1;
            }

            var productPackages = new Dictionary<string, PackageInfo>(StringComparer.OrdinalIgnoreCase);
            var coreclrPackages = new Dictionary<string, List<PackageInfo>>(StringComparer.OrdinalIgnoreCase);

            var projectsToSkip = new[]
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
                "docfx",
                "docfx-signed",
                "Entropy",
                "Glimpse",
                "IBC",
                "latest-dev",
                "latest-packages",
                "DataCommon.SQLite",
                "MusicStore",
                "NuGet.Packaging",
                "NuGet.Versioning",
                "Roslyn",
                "ServerTests",
                "Setup",
                "SqlClient",
                "System.Data.Common",
                "Templates",
                "WebSocketAbstractions",
            };

            foreach (var projectFolder in di.EnumerateDirectories())
            {
                if (projectsToSkip.Contains(projectFolder.Name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var latestPath = FindLatest(projectFolder, buildBranch);

                if (!Directory.Exists(latestPath))
                {
                    WriteError("Couldn't find latest for {0}", latestPath);
                    continue;
                }

                Console.WriteLine("Using {0}", latestPath);

                var build = new DirectoryInfo(Path.Combine(latestPath, "build"));

                if (!build.Exists)
                {
                    WriteError("Can't find build dir for {0}", projectFolder.Name);
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
                            if (!coreclrPackages.TryGetValue(zipPackage.Id, out clrPackages))
                            {
                                clrPackages = new List<PackageInfo>();
                                coreclrPackages[zipPackage.Id] = clrPackages;
                            }

                            clrPackages.Add(info);
                        }
                        else
                        {
                            productPackages[zipPackage.Id] = info;
                        }
                    });
                }
            }

            if (!VerifyAll(productPackages, coreclrPackages))
            {
                return 1;
            }

            Directory.CreateDirectory(outputPath);
            Directory.CreateDirectory(symbolsOutputPath);

            var pdbOutputPath = Path.Combine(symbolSourcePath, "pdbrepo");
            var sourceFilesPath = Path.Combine(symbolSourcePath, "sources");

            if (!Directory.Exists(pdbOutputPath))
            {
                Directory.CreateDirectory(pdbOutputPath);
            }

            if (!Directory.Exists(sourceFilesPath))
            {
                Directory.CreateDirectory(sourceFilesPath);
            }

            foreach (var packageInfo in productPackages.Values.Concat(coreclrPackages.SelectMany(pair => pair.Value)))
            {
                var packagePath = Path.Combine(outputPath, Path.GetFileName(packageInfo.PackagePath));

                Retry(() =>
                {
                    File.Copy(packageInfo.PackagePath, packagePath, overwrite: true);
                });

                Console.WriteLine("Copied to {0}", packagePath);

                if (File.Exists(packageInfo.SymbolsPath))
                {
                    var symbolsPath = Path.Combine(symbolsOutputPath, Path.GetFileName(packageInfo.SymbolsPath));

                    // REVIEW: Should we copy symbol packages elsewhere
                    Retry(() =>
                    {
                        File.Copy(packageInfo.SymbolsPath, symbolsPath, overwrite: true);
                        ExtractPdbsAndSourceFiles(packageInfo.SymbolsPath, sourceFilesPath, pdbOutputPath, nugetExePath);
                    });

                    Console.WriteLine("Copied to {0}", symbolsPath);
                }
            }

            return 0;
        }

        private static IDictionary<string, string> GetDictionaryField(string fieldName)
        {
            var dictionaryField = typeof(VersionUtility).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static);

            if (dictionaryField != null)
            {
                return dictionaryField.GetValue(obj: null) as IDictionary<string, string>;
            }

            return null;
        }

        private static string FindLatest(DirectoryInfo projectFolder, string buildBranch)
        {
            var latestPath = Path.Combine(projectFolder.FullName, buildBranch);

            return new DirectoryInfo(latestPath)
                              .EnumerateDirectories()
                              .Select(d =>
                              {
                                  int buildNumber;
                                  if (!Int32.TryParse(d.Name, out buildNumber))
                                  {
                                      buildNumber = Int32.MinValue;
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

        private static bool VerifyAll(Dictionary<string, PackageInfo> productPackages, Dictionary<string, List<PackageInfo>> coreclrPackages)
        {
            foreach (var productPackageInfo in productPackages.Values)
            {
                Visit(productPackageInfo, productPackages, coreclrPackages);
            }

            bool success = true;

            foreach (var packageInfo in productPackages.Values)
            {
                if (!packageInfo.Success)
                {
                    // Temporary workaround for FileSystemGlobbing used in Runtime.
                    if (packageInfo.Package.Id.Equals("Microsoft.Framework.Runtime", StringComparison.OrdinalIgnoreCase) &&
                        packageInfo.DependencyMismatches.All(d => d.Dependency.Id.Equals("Microsoft.Framework.FileSystemGlobbing", StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    // Temporary workaround for xunit.runner.aspnet used in Microsoft.AspNet.Testing. 
                    if (packageInfo.Package.Id.Equals("Microsoft.AspNet.Testing", StringComparison.OrdinalIgnoreCase) &&
                        packageInfo.DependencyMismatches.All(d => d.Dependency.Id.Equals("xunit.runner.aspnet", StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    if (packageInfo.InvalidCoreCLRPackageReferences.Count > 0)
                    {
                        WriteError("{0} has invalid package references:", packageInfo.Package.GetFullName());

                        foreach (var invalidReference in packageInfo.InvalidCoreCLRPackageReferences)
                        {
                            WriteError("Reference {0}({1}) must be changed to be a frameworkAssembly.",
                            invalidReference.Dependency,
                            invalidReference.TargetFramework);
                        }
                    }

                    if (packageInfo.DependencyMismatches.Count > 0)
                    {
                        WriteError("{0} has mismatched dependencies:", packageInfo.Package.GetFullName());

                        foreach (var mismatch in packageInfo.DependencyMismatches)
                        {
                            WriteError("    Expected {0}({1}) but got {2}",
                                mismatch.Dependency,
                                (mismatch.TargetFramework == VersionUtility.UnsupportedFrameworkName ?
                                "DNXCORE50" :
                                VersionUtility.GetShortFrameworkName(mismatch.TargetFramework)),
                                mismatch.Info.Package.Version);
                        }
                    }

                    success = false;
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
                value = value.Replace("|", "||")
                             .Replace("'", "|'")
                             .Replace("\r", "|r")
                             .Replace("\n", "|n")
                             .Replace("]", "|]");
                Console.Error.WriteLine("##teamcity[message text='" + value + "' status='ERROR']", args);
            }
            else
            {
                Console.Error.WriteLine(value, args);
            }
        }

        private static void Visit(PackageInfo productPackageInfo, Dictionary<string, PackageInfo> productPackages, Dictionary<string, List<PackageInfo>> coreclrPackages)
        {
            foreach (var dependencySet in productPackageInfo.Package.DependencySets)
            {
                // Skip PCL frameworks for verification
                if (IsPortableFramework(dependencySet.TargetFramework))
                {
                    continue;
                }

                foreach (var dependency in dependencySet.Dependencies)
                {
                    // For any dependency in the universe
                    PackageInfo dependencyPackageInfo;
                    if (productPackages.TryGetValue(dependency.Id, out dependencyPackageInfo))
                    {
                        if (dependencyPackageInfo.Package.Version !=
                                                         dependency.VersionSpec.MinVersion)
                        {
                            // Add a mismatch if the min version doesn't work out
                            // (we only really care about >= minVersion)
                            productPackageInfo.DependencyMismatches.Add(new DependencyWithIssue
                            {
                                Dependency = dependency,
                                TargetFramework = dependencySet.TargetFramework,
                                Info = dependencyPackageInfo
                            });
                        }
                    }
                    else if (coreclrPackages.Keys.Contains(dependency.Id))
                    {
                        var coreclrDependency = coreclrPackages[dependency.Id].Last();

                        if (string.Equals(dependency.Id, "System.Collections.Immutable", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(dependency.Id, "System.Reflection.Metadata", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (!string.Equals(dependencySet.TargetFramework.Identifier, "DNXCORE", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(dependencySet.TargetFramework.Identifier, ".NETPlatform", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(dependencySet.TargetFramework.Identifier, ".NETCore", StringComparison.OrdinalIgnoreCase))
                        {
                            productPackageInfo.InvalidCoreCLRPackageReferences.Add(new DependencyWithIssue
                            {
                                Dependency = dependency,
                                TargetFramework = dependencySet.TargetFramework,
                                Info = coreclrDependency
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

        private static void ExtractPdbsAndSourceFiles(string symbolsPath, string sourceFilesPath, string pdbPath, string nugetExePath)
        {
            nugetExePath = Path.Combine(nugetExePath, "nuget.exe");

            string processArgs = string.Format("pushsymbol \"{0}\" -symbolserver \"{1}\" -sourceserver \"{2}\"", symbolsPath, pdbPath, sourceFilesPath);
            var psi = new ProcessStartInfo(nugetExePath, processArgs)
            {
                CreateNoWindow = true,
            };
            Process.Start(psi).WaitForExit();
        }

        private static bool IsPortableFramework(FrameworkName framework)
        {
            // The profile part has been verified in the ParseFrameworkName() method. 
            // By the time it is called here, it's guaranteed to be valid.
            // Thus we can ignore the profile part here
            return framework != null && ".NETPortable".Equals(framework.Identifier, StringComparison.OrdinalIgnoreCase);
        }

        public class PackageInfo
        {
            public bool IsCoreCLRPackage { get; set; }

            // The actual package instance
            public IPackage Package { get; set; }

            // The path to this package
            public string PackagePath { get; set; }

            // The path to this package's symbol package
            public string SymbolsPath { get; set; }

            public bool Success
            {
                get { return DependencyMismatches.Count == 0 && InvalidCoreCLRPackageReferences.Count == 0; }
            }

            public IList<DependencyWithIssue> DependencyMismatches { get; private set; }

            public IList<DependencyWithIssue> InvalidCoreCLRPackageReferences { get; private set; }

            public PackageInfo()
            {
                DependencyMismatches = new List<DependencyWithIssue>();
                InvalidCoreCLRPackageReferences = new List<DependencyWithIssue>();
            }
        }

        public class DependencyWithIssue
        {
            public PackageDependency Dependency { get; set; }
            public PackageInfo Info { get; set; }
            public FrameworkName TargetFramework { get; set; }
        }
    }
}
