using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using NuGet;

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
                        Log.WriteError("{0} has invalid package references:", packageInfo.Package.GetFullName());

                        foreach (var invalidReference in packageInfo.InvalidCoreCLRPackageReferences)
                        {
                            Log.WriteError("Reference {0}({1}) must be changed to be a frameworkAssembly.",
                                invalidReference.Dependency,
                                invalidReference.TargetFramework);
                        }
                    }

                    if (packageInfo.DependencyMismatches.Count > 0)
                    {
                        Log.WriteError("{0} has mismatched dependencies:", packageInfo.Package.GetFullName());

                        foreach (var mismatch in packageInfo.DependencyMismatches)
                        {
                            Log.WriteError("    Expected {0}({1}) but got {2}",
                                mismatch.Dependency,
                                (mismatch.TargetFramework == VersionUtility.UnsupportedFrameworkName ?
                                "NETSTANDARDAPP1_5" :
                                VersionUtility.GetShortFrameworkName(mismatch.TargetFramework)),
                                mismatch.Info.Package.Version);
                        }
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

            if (productPackageInfo.Package.Id.Contains(".VSRC1"))
            {
                // Ignore .VSRC1 for SanityCheck
                return;
            }

            try
            {
                foreach (var dependencySet in productPackageInfo.Package.DependencySets)
                {
                    // If the package doens't target any frameworks, just accept it
                    if (dependencySet.TargetFramework == null)
                    {
                        continue;
                    }

                    // Skip PCL frameworks for verification
                    if (IsPortableFramework(dependencySet.TargetFramework))
                    {
                        continue;
                    }

                    foreach (var dependency in dependencySet.Dependencies)
                    {
                        PackageInfo dependencyPackageInfo;
                        if (!result.AllPackages.TryGetValue(dependency.Id, out dependencyPackageInfo))
                        {
                            // External dependency
                            continue;
                        }

                        if (dependencyPackageInfo.Package.Version != dependency.VersionSpec.MinVersion)
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
                Log.WriteError($"Unable to verify package {productPackageInfo.Package.GetFullName()}");
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
