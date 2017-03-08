using NuGet.Frameworks;
using NuGet.Packaging.Core;

namespace CoherenceBuild
{
    public class DependencyWithIssue
    {
        public PackageDependency Dependency { get; set; }
        public PackageInfo Info { get; set; }
        public NuGetFramework TargetFramework { get; set; }
    }
}
