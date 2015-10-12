using System;
using System.Collections.Generic;

namespace CoherenceBuild
{
    public class ProcessResult
    {
        public Dictionary<string, PackageInfo> ProductPackages { get; } =
            new Dictionary<string, PackageInfo>(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, List<PackageInfo>> CoreCLRPackages { get; } =
            new Dictionary<string, List<PackageInfo>>(StringComparer.OrdinalIgnoreCase);
    }
}
