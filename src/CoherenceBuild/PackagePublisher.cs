using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NuGet;

namespace CoherenceBuild
{
    public static class PackagePublisher
    {
        public static void PublishSymbolsPackages(
            string outputPath,
            string symbolsOutputPath,
            string symbolSourcePath,
            string symbolsNuGetExe,
            ProcessResult processResult)
        {
            Directory.CreateDirectory(symbolsOutputPath);

            var pdbOutputPath = Path.Combine(symbolSourcePath, "pdbrepo");
            var sourceFilesPath = Path.Combine(symbolSourcePath, "sources");

            Directory.CreateDirectory(pdbOutputPath);
            Directory.CreateDirectory(sourceFilesPath);

            var packagesToCopy = Enumerable.Concat(
                processResult.CoreCLRPackages.SelectMany(p => p.Value),
                processResult.ProductPackages.Select(p => p.Value));

            Parallel.ForEach(packagesToCopy, new ParallelOptions { MaxDegreeOfParallelism = 4 }, packageInfo =>
            {
                var packagePath = Path.Combine(outputPath, Path.GetFileName(packageInfo.PackagePath));

                Program.Retry(() =>
                {
                    File.Copy(packageInfo.PackagePath, packagePath, overwrite: true);
                    // Update package path to point to local share.
                    packageInfo.PackagePath = packagePath;
                });

                Console.WriteLine("Copied to {0}", packagePath);

                if (File.Exists(packageInfo.SymbolsPath))
                {
                    var symbolsPath = Path.Combine(symbolsOutputPath, Path.GetFileName(packageInfo.SymbolsPath));

                    // REVIEW: Should we copy symbol packages elsewhere
                    Program.Retry(() =>
                    {
                        File.Copy(packageInfo.SymbolsPath, symbolsPath, overwrite: true);
                        ExtractPdbsAndSourceFiles(packageInfo.SymbolsPath, sourceFilesPath, pdbOutputPath, symbolsNuGetExe);
                    });

                    Console.WriteLine("Copied to {0}", symbolsPath);
                }
            });
        }

        private static void ExtractPdbsAndSourceFiles(string symbolsPath, string sourceFilesPath, string pdbPath, string nugetExePath)
        {
            nugetExePath = Path.Combine(nugetExePath, "nuget.exe");

            string processArgs = string.Format("pushsymbol \"{0}\" -symbolserver \"{1}\" -sourceserver \"{2}\"", symbolsPath, pdbPath, sourceFilesPath);
            var psi = new ProcessStartInfo(nugetExePath, processArgs)
            {
                UseShellExecute = false,
            };
            using (var process = Process.Start(psi))
            {
                process.WaitForExit();
            }
        }

        public static void PublishNuGetPackages(ProcessResult processResult, string feed, string apiKey)
        {
            var server = new PackageServer(feed, "Custom DNX");
            var packagesToPushInOrder = Enumerable.Concat(
                 processResult.CoreCLRPackages.SelectMany(p => p.Value),
                 processResult.ProductPackages.OrderBy(p => p.Value.Degree).Select(p => p.Value));
            
            foreach (var package in packagesToPushInOrder)
            {
                Console.WriteLine($"Publishing package {package.Package}");
                Program.Retry(() =>
                {
                    var length = new FileInfo(package.PackagePath).Length;
                    server.PushPackage(apiKey, new PushLocalPackage(package.PackagePath), length, (int)TimeSpan.FromMinutes(5).TotalMilliseconds, disableBuffering: false);
                });
                Console.WriteLine($"Done publishing package {package.Package}");
            }
        }

        private class PushLocalPackage : LocalPackage
        {
            private readonly string _filePath;

            public PushLocalPackage(string filePath)
            {
                _filePath = filePath;
            }

            public override void ExtractContents(IFileSystem fileSystem, string extractPath)
            {
                throw new NotSupportedException();
            }

            public override Stream GetStream()
            {
                return File.OpenRead(_filePath);
            }

            protected override IEnumerable<IPackageAssemblyReference> GetAssemblyReferencesCore()
            {
                throw new NotSupportedException();
            }

            protected override IEnumerable<IPackageFile> GetFilesBase()
            {
                throw new NotSupportedException();
            }
        }
    }
}
