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
                    // Temporary workaround for FileSystemGlobbing used in Runtime.
                    if (packageInfo.Package.Id.Equals("Microsoft.Extensions.Runtime", StringComparison.OrdinalIgnoreCase) &&
                        packageInfo.DependencyMismatches.All(d => d.Dependency.Id.Equals("Microsoft.Extensions.FileSystemGlobbing", StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    // Temporary workaround for xunit.runner.aspnet used in Microsoft.AspNet.Testing.
                    if (packageInfo.Package.Id.Equals("Microsoft.AspNet.Testing", StringComparison.OrdinalIgnoreCase) &&
                        packageInfo.DependencyMismatches.All(d => d.Dependency.Id.Equals("xunit.runner.aspnet", StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    if (packageInfo.Package.Id.Equals("Microsoft.Extensions.PlatformAbstractions"))
                    {
                        // Temporarily skip PlatformAbstractions
                        continue;
                    }

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

        private static void Visit(PackageInfo productPackageInfo, ProcessResult result)
        {
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
                            if (result.CoreCLRPackages.ContainsKey(dependency.Id))
                            {
                                if (
                                    !string.Equals(dependencySet.TargetFramework.Identifier, "DNXCORE", StringComparison.OrdinalIgnoreCase) &&
                                    !string.Equals(dependencySet.TargetFramework.Identifier, ".NETPlatform", StringComparison.OrdinalIgnoreCase))
                                {
                                    // For CoreCLR packages, only verify if this is DNXCORE50
                                    continue;
                                }

                                if (dependency.Id == "System.Collections.Immutable")
                                {
                                    // EF depends on RTM build of System.Collections.Immutable.
                                    continue;
                                }
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

                        if (result.CoreCLRPackages.ContainsKey(dependency.Id))
                        {
                            var dependenciesToIgnore = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                            {
                                "System.Collections.Immutable",
                                "System.Diagnostics.DiagnosticSource",
                                "System.Numerics.Vectors",
                                "System.Reflection.Metadata",
                                "System.Text.Encodings.Web",
                                "System.Threading.Tasks.Extensions",
                                "System.Buffers",
                                "System.Runtime.InteropServices.RuntimeInformation",
                                "Microsoft.NETCore.Platforms",
                                "Microsoft.IdentityModel.Protocols.OpenIdConnect"
                            };

                            if (dependenciesToIgnore.Contains(dependency.Id))
                            {
                                continue;
                            }
                            if (!string.Equals(dependencySet.TargetFramework.Identifier, "DNXCORE", StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(dependencySet.TargetFramework.Identifier, ".NETPlatform", StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(dependencySet.TargetFramework.Identifier, ".NETCore", StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(dependencySet.TargetFramework.Identifier, "UAP10.0", StringComparison.OrdinalIgnoreCase))
                            {
                                productPackageInfo.InvalidCoreCLRPackageReferences.Add(new DependencyWithIssue
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
