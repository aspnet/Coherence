using System.Runtime.Versioning;
using NuGet;

namespace CoherenceBuild
{
    public class DependencyWithIssue
    {
        public PackageDependency Dependency { get; set; }
        public PackageInfo Info { get; set; }
        public FrameworkName TargetFramework { get; set; }
    }
}
