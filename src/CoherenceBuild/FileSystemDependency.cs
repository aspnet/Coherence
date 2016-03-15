using System.Collections.Generic;
using System.IO;

namespace CoherenceBuild
{
    public abstract class FileSystemDependency
    {
        public abstract void Copy(string source, string destination);

        public string Destination { get; set; } = string.Empty;

        public bool Optional { get; set; }

        protected void CopyFile(string source, string destination)
        {
            Log.WriteInformation($"Copying {source} to {destination}");
            File.Copy(source, destination, overwrite: true);
        }
    }
}
