using System;
using System.Runtime.Versioning;

namespace CoherenceBuild
{
    public class CoherenceVerifier
    {
        public static bool VerifyAll(ProcessResult result)
        {
            foreach (var productPackageInfo in result.ProductPackages.Values)
            {
                Visit(productPackageInfo, result);
            }

            var success = true;
            foreach (var packageInfo in result.ProductPackages.Values)
            {
                if (!packageInfo.Success)
                {
                    if (packageInfo.InvalidCoreCLRPackageReferences.Count > 0)
                    {
                        Log.WriteError("{0} has invalid package references:", packageInfo.Identity);

                        foreach (var invalidReference in packageInfo.InvalidCoreCLRPackageReferences)
                        {
                            Log.WriteError("Reference {0}({1}) must be changed to be a frameworkAssembly.",
                                invalidReference.Dependency,
                                invalidReference.TargetFramework);
                        }
                    }

                    foreach (var mismatch in packageInfo.DependencyMismatches)
                    {
                        Log.WriteError($"{packageInfo.Identity} depends on {mismatch.Dependency.Id} " +
                            $"v{mismatch.Dependency.VersionRange} ({mismatch.TargetFramework}) when the latest build is v{mismatch.Info.Identity.Version}.");
                    }

                    success = false;
                }
            }

            return success;
        }

        private static void Visit(PackageInfo productPackageInfo, ProcessResult result)
        {
            if (!productPackageInfo.IsCoherencePackage)
            {
                // Only verify packages from UniverseCoherence.
                return;
            }

            if (productPackageInfo.Identity.Id.Contains(".VSRC1"))
            {
                // Ignore .VSRC1 for SanityCheck
                return;
            }

            try
            {
                foreach (var dependencySet in productPackageInfo.PackageDependencyGroups)
                {
                    // If the package doens't target any frameworks, just accept it
                    if (dependencySet.TargetFramework == null)
                    {
                        continue;
                    }

                    // Skip PCL frameworks for verification
                    if (dependencySet.TargetFramework.IsPCL)
                    {
                        continue;
                    }

                    foreach (var dependency in dependencySet.Packages)
                    {
                        PackageInfo dependencyPackageInfo;
                        if (!result.AllPackages.TryGetValue(dependency.Id, out dependencyPackageInfo))
                        {
                            // External dependency
                            continue;
                        }

                        if (string.Equals(dependencySet.TargetFramework.Framework, ".NETCore", StringComparison.OrdinalIgnoreCase))
                        {
                            // Skip netcore50 since it references RTM package versions.
                            continue;
                        }

                        if (dependencyPackageInfo.Identity.Version != dependency.VersionRange.MinVersion)
                        {
                            PackageInfo dependencyInfo;
                            if (result.AllPackages.TryGetValue(dependency.Id, out dependencyInfo) && dependencyInfo.IsDnxPackage)
                            {
                                // Ignore Dnx dependencies
                                continue;
                            }

                            // For any dependency in the universe
                            // Add a mismatch if the min version doesn't work out
                            // (we only really care about >= minVersion)
                            productPackageInfo.DependencyMismatches.Add(new DependencyWithIssue
                            {
                                Dependency = dependency,
                                TargetFramework = dependencySet.TargetFramework,
                                Info = dependencyPackageInfo
                            });
                        }

                        if (result.ProductPackages.ContainsKey(dependency.Id))
                        {
                            productPackageInfo.ProductDependencies.Add(dependencyPackageInfo);
                        }
                    }
                }
            }
            catch
            {
                Log.WriteError($"Unable to verify package {productPackageInfo.Identity}");
                throw;
            }
        }

        private static bool IsPortableFramework(FrameworkName framework)
        {
            // The profile part has been verified in the ParseFrameworkName() method.
            // By the time it is called here, it's guaranteed to be valid.
            // Thus we can ignore the profile part here
            return framework != null && ".NETPortable".Equals(framework.Identifier, StringComparison.OrdinalIgnoreCase);
        }
    }
}
