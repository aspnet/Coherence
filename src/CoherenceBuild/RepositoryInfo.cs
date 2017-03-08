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

        public string BuildDirectory { get; set; } = "build";

        public string PackagesDestinationDirectory { get; set; }
    }
}
