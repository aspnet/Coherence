using System.IO;
using System.Threading.Tasks;

namespace CoherenceBuild
{
    public class FolderDependecy : FileSystemDependency
    {
        private readonly string _folder;

        public FolderDependecy(string folder)
        {
            _folder = folder;
        }

        public override void Copy(string source, string destination)
        {
            var sourceDirectoryPath = Path.Combine(source, _folder);

            if (!Directory.Exists(sourceDirectoryPath))
            {
                var errorMessage = $"Could not copy dependency folder {sourceDirectoryPath} because it doesn't exist";

                if (!Optional)
                {
                    throw new DirectoryNotFoundException(errorMessage);
                }

                Log.WriteWarning(errorMessage);
                return;
            }

            var destinationDirectoryPath = Path.Combine(destination, string.IsNullOrEmpty(Destination) ? _folder : Destination);
            CopyDirectory(sourceDirectoryPath, destinationDirectoryPath);
        }

        private void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);

            var sourceInfo = new DirectoryInfo(source);

            Parallel.ForEach(sourceInfo.GetFiles(), sourceFile =>
            {
                var sourceFilePath = sourceFile.FullName;
                var destinationFilePath = Path.Combine(destination, Path.GetFileName(sourceFilePath));
                CopyFile(sourceFilePath, destinationFilePath);
            });

            foreach (var sourceSubDir in sourceInfo.GetDirectories())
            {
                var sourceSubDirPath = sourceSubDir.FullName;
                var destinationSubDirPath = Path.Combine(destination, Path.GetFileName(sourceSubDirPath));
                CopyDirectory(sourceSubDirPath, destinationSubDirPath);
            }
        }
    }
}
