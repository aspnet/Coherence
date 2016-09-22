using System.Collections.Generic;
using System.Linq;
using NuGet.Packaging;
using NuGet.Packaging.Core;

namespace CoherenceBuild
{
    public class PackageInfo
    {
        public bool IsPartnerPackage { get; set; }

        public PackageIdentity Identity { get; set; }

        public IEnumerable<PackageDependencyGroup> PackageDependencyGroups { get; set; }

        // The path to this package
        public string PackagePath { get; set; }

        // The path to this package's symbol package
        public string SymbolsPath { get; set; }

        public bool Success
        {
            get { return DependencyMismatches.Count == 0 && InvalidCoreCLRPackageReferences.Count == 0; }
        }

        public IList<PackageInfo> ProductDependencies { get; } = new List<PackageInfo>();

        public int Degree
        {
            get
            {
                if (IsPartnerPackage)
                {
                    return 1;
                }

                if (ProductDependencies.Count == 0)
                {
                    return 2;
                }

                return ProductDependencies.Max(d => 1 + d.Degree);
            }
        }

        public IList<DependencyWithIssue> DependencyMismatches { get; private set; }

        public IList<DependencyWithIssue> InvalidCoreCLRPackageReferences { get; private set; }

        public PackageInfo()
        {
            DependencyMismatches = new List<DependencyWithIssue>();
            InvalidCoreCLRPackageReferences = new List<DependencyWithIssue>();
        }

        public override string ToString()
        {
            return Identity.ToString();
        }
    }
}
