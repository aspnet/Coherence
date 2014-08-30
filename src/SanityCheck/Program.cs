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
            AddNewFrameworksToNuGet();

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

            var packages = new Dictionary<string, PackageInfo>(StringComparer.OrdinalIgnoreCase);

            var projectsToSkip = new[] {
                "MusicStore",
                "Coherence",
                "Coherence-Signed",
                "latest-dev",
                "Entropy",
                "latest-packages",
                "Microsoft.Data.SQLite",
                "WebFx",
                "Data",
                "System.Data.Common",
                "DiagnosticsPages",
                "HttpClient",
                "kvm"
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
                        packages[zipPackage.Id] = new PackageInfo
                        {
                            Package = zipPackage,
                            PackagePath = packageInfo.FullName,
                            SymbolsPath = symbolsPath,
                            IsCoreCLRPackage = isCoreCLR
                        };
                    });
                }
            }

            if (!VerifyAll(packages))
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

            foreach (var packageInfo in packages.Values)
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

        private static void AddNewFrameworksToNuGet()
        {
            // Super hacky way to work around known nuget frameworks
            // Add ASP.NET and ASP.NET Core to the list of frameworks
            // so that parsing them won't both show up as unsupported
            const string AspNetFrameworkIdentifier = "Asp.Net";
            const string AspNetCoreFrameworkIdentifier = "Asp.NetCore";

            var knownIdentifiers = GetDictionaryField("_knownIdentifiers");
            var identifierToFrameworkFolder = GetDictionaryField("_identifierToFrameworkFolder");

            if (knownIdentifiers != null)
            {
                knownIdentifiers["aspnet"] = AspNetFrameworkIdentifier;
                knownIdentifiers["aspnetcore"] = AspNetCoreFrameworkIdentifier;
                knownIdentifiers["asp.net"] = AspNetFrameworkIdentifier;
                knownIdentifiers["asp.netcore"] = AspNetCoreFrameworkIdentifier;
            }

            if (identifierToFrameworkFolder != null)
            {
                identifierToFrameworkFolder[AspNetFrameworkIdentifier] = "aspnet";
                identifierToFrameworkFolder[AspNetCoreFrameworkIdentifier] = "aspnetcore";
            }
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

        private static bool VerifyAll(Dictionary<string, PackageInfo> universe)
        {
            foreach (var packageInfo in universe.Values)
            {
                if (packageInfo.IsCoreCLRPackage)
                {
                    continue;
                }

                Visit(packageInfo, universe);
            }

            bool success = true;

            foreach (var packageInfo in universe.Values)
            {
                if (packageInfo.DependencyMismatches.Any())
                {
                    WriteError("{0} has mismatched dependencies:", packageInfo.Package.GetFullName());

                    foreach (var mismatch in packageInfo.DependencyMismatches)
                    {
                        WriteError("    Expected {0}({1}) but got {2}",
                            mismatch.Dependency,
                            (mismatch.TargetFramework == VersionUtility.UnsupportedFrameworkName ?
                            "k10" :
                            VersionUtility.GetShortFrameworkName(mismatch.TargetFramework)),
                            mismatch.Info.Package.Version);
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

        private static void Visit(PackageInfo packageInfo, Dictionary<string, PackageInfo> universe)
        {
            foreach (var dependencySet in packageInfo.Package.DependencySets)
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
                get
                {
                    return DependencyMismatches.Count == 0;
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
