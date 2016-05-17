
using System;
using System.Collections.Generic;

namespace CoherenceBuild
{
    public class RepositoryInfo
    {
        public RepositoryInfo(string name, string buildNumber = null)
        {
            Name = name;
            BuildNumber = buildNumber;
        }

        public string Name { get; }

        public string BuildNumber { get; set; }

        public List<FileSystemDependency> FileSystemDependencies { get; } = new List<FileSystemDependency>();

        public string PackageSourceDir { get; set; } = "build";

        public string PackageDestinationDir { get; set; } = "build";

        public HashSet<string> PackagesToSkip { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }
}
