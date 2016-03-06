using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NuGet;

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

                Console.WriteLine("Copied to {0}", packagePath);
            });
        }

        public static void PublishToFeed(ProcessResult processResult, string feed, string apiKey)
        {
            var server = new PackageServer(feed, "Custom DNX");
            var packagesToPushInOrder = Enumerable.Concat(
                 processResult.CoreCLRPackages.Values,
                 processResult.ProductPackages.OrderBy(p => p.Value.Degree).Select(p => p.Value));

            Parallel.ForEach(
                packagesToPushInOrder,
                new ParallelOptions { MaxDegreeOfParallelism = 4 },
                package =>
            {
                int attempt = 0;
                Program.Retry(() =>
                {
                    attempt++;
                    Console.WriteLine($"Attempting to publishing package {package.Package} ({attempt})");
                    var length = new FileInfo(package.PackagePath).Length;
                    server.PushPackage(apiKey, new PushLocalPackage(package.PackagePath), length, (int)TimeSpan.FromMinutes(5).TotalMilliseconds, disableBuffering: false);
                });
                Console.WriteLine($"Done publishing package {package.Package}");
            });
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
