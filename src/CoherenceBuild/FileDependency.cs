using System.IO;

namespace CoherenceBuild
{
    public class FileDependency : FileSystemDependency
    {
        private readonly string _fileName;

        public FileDependency(string fileName)
        {
            _fileName = fileName;
        }

        public override void Copy(string source, string destination)
        {
            var sourceFilePath = Path.Combine(source, _fileName);

            if (!File.Exists(sourceFilePath))
            {
                var errorMessage = $"Could not copy dependency file {sourceFilePath} because it doesn't exist";

                if (!Optional)
                {
                    throw new FileNotFoundException(errorMessage);
                }

                Log.WriteWarning(errorMessage);
                return;
            }

            var destinationFilePath = Path.Combine(destination, Destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFilePath));

            CopyFile(sourceFilePath, destinationFilePath);
        }
    }
}
