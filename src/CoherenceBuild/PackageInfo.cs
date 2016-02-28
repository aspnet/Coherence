using System.Collections.Generic;
using System.Linq;
using NuGet;

namespace CoherenceBuild
{
    public class PackageInfo
    {
        public bool IsCoreCLRPackage { get; set; }

        public bool IsDnxPackage { get; set; }

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

        public IList<PackageInfo> ProductDependencies { get; } = new List<PackageInfo>();

        public int Degree => ProductDependencies.Sum(d => 1 + d.Degree);

        public IList<DependencyWithIssue> DependencyMismatches { get; private set; }

        public IList<DependencyWithIssue> InvalidCoreCLRPackageReferences { get; private set; }

        public PackageInfo()
        {
            DependencyMismatches = new List<DependencyWithIssue>();
            InvalidCoreCLRPackageReferences = new List<DependencyWithIssue>();
        }
    }
}
