using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
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

                Log.WriteInformation("Copied to {0}", packagePath);
            });
        }

        public static void PublishToFeed(ProcessResult processResult, string feed, string apiKey)
        {
            var server = new PackageServer(feed, "Custom DNX");
            var packagesToPushInOrder = Enumerable.Concat(
                 processResult.CoreCLRPackages.Values,
                 processResult.ProductPackages.OrderBy(p => p.Value.Degree).Select(p => p.Value));

            var httpClient = new System.Net.Http.HttpClient();
            var indexJson = JObject.Parse(httpClient.GetAsync(feed.TrimEnd('/') + "/api/v3/index.json").Result.Content.ReadAsStringAsync().Result);
            var v3Feed = indexJson
                .Property("resources")
                .Value.AsJEnumerable().Cast<JObject>()
                .First(item => item.Property("@type").Value.ToString() == "PackageBaseAddress/3.0.0")
                .Property("@id")
                .Value.ToString();

            Parallel.ForEach(
                packagesToPushInOrder,
                new ParallelOptions { MaxDegreeOfParallelism = 4 },
                package =>
            {
                int attempt = 0;
                Program.Retry(() =>
                {
                    if (package.IsCoreCLRPackage && IsAlreadyUploaded(v3Feed, httpClient, package.Package))
                    {
                        Log.WriteInformation($"Skipping {package.Package} since it is already published.");
                        return;
                    }

                    attempt++;
                    Log.WriteInformation($"Attempting to publish package {package.Package} (Attempt: {attempt})");
                    var length = new FileInfo(package.PackagePath).Length;
                    server.PushPackage(apiKey, new PushLocalPackage(package.PackagePath), length, (int)TimeSpan.FromMinutes(5).TotalMilliseconds, disableBuffering: false);
                    Log.WriteInformation($"Done publishing package {package.Package}");
                });
            });
        }

        private static bool IsAlreadyUploaded(string v3Feed, System.Net.Http.HttpClient client, IPackage package)
        {
            // https://myget-2e16.kxcdn.com/artifacts/aspnetvolatiledev/nuget/v3/flatcontainer/microsoft.aspnetcore.signalr.server/0.1.0-rc2-20311/microsoft.aspnetcore.signalr.server.0.1.0-rc2-20311.nupkg
            var id = package.Id.ToLowerInvariant();
            var version = package.Version.ToNormalizedString();
            var uri = $"{v3Feed.TrimEnd('/')}/{id}/{version}/{id}.{version}.nupkg";
            var message = new HttpRequestMessage(HttpMethod.Head, uri);
            var result = client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead).Result;
            return result.IsSuccessStatusCode;
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
