// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RepoTasks.Utilities
{
    public class PackageCollection
    {
        private readonly IDictionary<string, PackageCategory> _packages = new Dictionary<string, PackageCategory>(StringComparer.OrdinalIgnoreCase);

        private PackageCollection()
        {
        }

        public bool TryGetCategory(string packageId, out PackageCategory category) => _packages.TryGetValue(packageId, out category);

        public void Remove(string packageId) => _packages.Remove(packageId);

        public int Count => _packages.Count;

        public IEnumerable<string> Keys => _packages.Keys;

        public static PackageCollection DeserializeFromCsv(string filepath)
        {
            using (var stream = File.OpenRead(filepath))
            using (var reader = new StreamReader(stream))
            {
                return DeserializeFromCsv(reader);
            }
        }

        public static PackageCollection DeserializeFromCsv(TextReader reader)
        {
            var lineNo = 0;
            var list = new PackageCollection();

            string[] Split(string l) => l.Split(',').Select(s => s.Trim()).ToArray();

            // Package,Category,...
            // Microsoft.AspNetCore.Razor.Tools,ship,...
            // Microsoft.VisualStudio.Web.CodeGeneration.Tools,ship,...
            // Microsoft.Extensions.SecretManager.Tools,ship,...

            var line = reader.ReadLine();
            lineNo++;
            var columns = Split(line);

            EnsureHeaderColumns(columns);

            while ((line = reader.ReadLine()) != null)
            {
                lineNo++;

                var values = Split(line);
                if (values.Length < 2)
                {
                    throw new FormatException($"Error on line {lineNo}. Too few columns. Expected at least two.");
                }

                PackageCategory category;
                switch (values[1].ToLowerInvariant())
                {
                    case "ship":
                        category = PackageCategory.Shipping;
                        break;
                    case "noship":
                        category = PackageCategory.NoShip;
                        break;
                    case "shipoob":
                        category = PackageCategory.ShipOob;
                        break;
                    default:
                        category = PackageCategory.Unknown;
                        break;
                }

                if (list._packages.ContainsKey(values[0]))
                {
                    throw new InvalidDataException($"Duplicate package id detected: {values[0]} on line {lineNo}");
                }

                list._packages.Add(values[0], category);
            }

            return list;
        }

        private static void EnsureHeaderColumns(string[] columns)
        {
            if (columns.Length < 2)
            {
                throw new FormatException("Unrecognized number of columns in csv file");
            }

            if (!columns[0].Equals("Package", StringComparison.OrdinalIgnoreCase))
            {
                throw new FormatException("Unrecognized column: " + columns[0]);
            }

            if (!columns[1].Equals("Category", StringComparison.OrdinalIgnoreCase))
            {
                throw new FormatException("Unrecognized column: " + columns[0]);
            }
        }
    }
}
