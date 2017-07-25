// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO;
using System.Text;
using Microsoft.Build.Framework;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using RepoTasks.Utilities;

namespace RepoTasks
{
    public class CopyPackagesToSplitFolders : Microsoft.Build.Utilities.Task
    {
        /// <summary>
        /// The item group containing the nuget packages to split in different folders.
        /// </summary>
        [Required]
        public ITaskItem[] Packages { get; set; }

        /// <summary>
        /// Path to the CSV file containing the names and subfolders where each package must go.
        /// </summary>
        [Required]
        public string CsvFile { get; set; }

        /// <summary>
        /// The folder where packages should be copied. Subfolders will be created based on package category.
        /// </summary>
        [Required]
        public string DestinationFolder { get; set; }

        public override bool Execute()
        {
            if (Packages?.Length == 0)
            {
                Log.LogError("No packages were found.");
                return false;
            }

            if (string.IsNullOrEmpty(CsvFile) || !File.Exists(CsvFile))
            {
                Log.LogError($"Package manifest (csv file) could not be loaded from '{CsvFile}'");
                return false;
            }

            var expectedPackages = PackageCollection.DeserializeFromCsv(CsvFile);

            Directory.CreateDirectory(DestinationFolder);

            foreach (var package in Packages)
            {
                PackageIdentity identity;
                using (var reader = new PackageArchiveReader(package.ItemSpec))
                {
                    identity = reader.GetIdentity();
                }

                if (!expectedPackages.TryGetCategory(identity.Id, out var category))
                {
                    Log.LogError($"{CsvFile} does not contain an entry for a package with id: {identity.Id}");
                    return false;
                }

                string destDir;
                switch (category)
                {
                    case PackageCategory.Unknown:
                        throw new InvalidOperationException($"Package {identity} does not have a recognized package category.");
                    case PackageCategory.Shipping:
                        destDir = Path.Combine(DestinationFolder, "ship");
                        break;
                    case PackageCategory.NoShip:
                        destDir = Path.Combine(DestinationFolder, "noship");
                        break;
                    case PackageCategory.ShipOob:
                        destDir = Path.Combine(DestinationFolder, "shipoob");
                        break;
                    default:
                        throw new NotImplementedException();
                }

                Directory.CreateDirectory(destDir);

                var destFile = Path.Combine(destDir, Path.GetFileName(package.ItemSpec));

                Log.LogMessage($"Copying {package.ItemSpec} to {destFile}");

                File.Copy(package.ItemSpec, destFile);
                expectedPackages.Remove(identity.Id);
            }

            if (expectedPackages.Count != 0)
            {
                var error = new StringBuilder();
                foreach (var key in expectedPackages.Keys)
                {
                    error.Append(" - ").AppendLine(key);
                }

                Log.LogError($"Expected the following packages based on the contents of {CsvFile}, but they were not found:" + error.ToString());
                return false;
            }

            return true;
        }
    }
}
