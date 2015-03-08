﻿// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Reflection;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Xml.Linq;
using NuGet;

namespace Microsoft.Framework.Runtime
{
    public class FrameworkReferenceResolver : IFrameworkReferenceResolver
    {
        private readonly IDictionary<FrameworkName, FrameworkInformation> _cache = new Dictionary<FrameworkName, FrameworkInformation>();

        private static readonly IDictionary<FrameworkName, List<FrameworkName>> _aliases = new Dictionary<FrameworkName, List<FrameworkName>>
        {
            { new FrameworkName(VersionUtility.AspNetFrameworkIdentifier, new Version(5, 0)), new List<FrameworkName> {
                    new FrameworkName(VersionUtility.NetFrameworkIdentifier, new Version(4, 5, 1))
                }
            },
            { new FrameworkName(VersionUtility.DnxFrameworkIdentifier, new Version(4, 5, 1)), new List<FrameworkName> {
                    new FrameworkName(VersionUtility.NetFrameworkIdentifier, new Version(4, 5, 1))
                } 
            }
        };

        private static readonly IDictionary<FrameworkName, List<FrameworkName>> _monoAliases = new Dictionary<FrameworkName, List<FrameworkName>>
        {
            { new FrameworkName(VersionUtility.NetFrameworkIdentifier, new Version(4, 5, 1)), new List<FrameworkName> {
                    new FrameworkName(VersionUtility.DnxFrameworkIdentifier, new Version(4, 5, 1)),
                    new FrameworkName(VersionUtility.AspNetFrameworkIdentifier, new Version(5, 0))
                }
            }
        };

        public FrameworkReferenceResolver()
        {
            PopulateCache();
        }

        public bool TryGetAssembly(string name, FrameworkName targetFramework, out string path, out Version version)
        {
            path = null;
            version = null;

            var information = _cache.GetOrAdd(targetFramework, GetFrameworkInformation);

            if (information == null)
            {
                return false;
            }

            lock (information.Assemblies)
            {
                AssemblyEntry entry;
                if (information.Assemblies.TryGetValue(name, out entry))
                {
                    if (string.IsNullOrEmpty(entry.Path))
                    {
                        entry.Path = GetAssemblyPath(information.Path, name);
                    }

                    if (!string.IsNullOrEmpty(entry.Path) && entry.Version == null)
                    {
                        // This code path should only run on mono
                        entry.Version = VersionUtility.GetAssemblyVersion(entry.Path).Version;
                    }

                    path = entry.Path;
                    version = entry.Version;
                }
            }

            return !string.IsNullOrEmpty(path);
        }

        public string GetFriendlyFrameworkName(FrameworkName targetFramework)
        {
            // We don't have a friendly name for this anywhere on the machine so hard code it
            if (string.Equals(targetFramework.Identifier, "K", StringComparison.OrdinalIgnoreCase))
            {
                return ".NET Core Framework 4.5";
            }
            else if (string.Equals(targetFramework.Identifier, VersionUtility.AspNetCoreFrameworkIdentifier, StringComparison.OrdinalIgnoreCase))
            {
                return "ASP.NET Core 5.0";
            }
            else if (string.Equals(targetFramework.Identifier, VersionUtility.AspNetFrameworkIdentifier, StringComparison.OrdinalIgnoreCase))
            {
                return "ASP.NET 5.0";
            }
            else if (string.Equals(targetFramework.Identifier, VersionUtility.DnxCoreFrameworkIdentifier, StringComparison.OrdinalIgnoreCase))
            {
                return "DNX Core 5.0";
            }
            else if (string.Equals(targetFramework.Identifier, VersionUtility.DnxFrameworkIdentifier, StringComparison.OrdinalIgnoreCase))
            {
                return "DNX " + targetFramework.Version.ToString();
            }

            var information = _cache.GetOrAdd(targetFramework, GetFrameworkInformation);

            if (information == null)
            {
                return targetFramework.ToString();
            }

            return information.Name;
        }

        public string GetFrameworkPath(FrameworkName targetFramework)
        {
            var information = _cache.GetOrAdd(targetFramework, GetFrameworkInformation);

            if (information == null)
            {
                return null;
            }

            return information.Path;
        }

        public string GetFrameworkRedistListPath(FrameworkName targetFramework)
        {
            var information = _cache.GetOrAdd(targetFramework, GetFrameworkInformation);

            if (information == null)
            {
                return null;
            }

            return information.RedistListPath;
        }

        public static string GetReferenceAssembliesPath()
        {
            // References assemblies are in %ProgramFiles(x86)% on
            // 64 bit machines
            var programFiles = Environment.GetEnvironmentVariable("ProgramFiles(x86)");

            if (string.IsNullOrEmpty(programFiles))
            {
                // On 32 bit machines they are in %ProgramFiles%
                programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
            }

            if (string.IsNullOrEmpty(programFiles))
            {
                // Reference assemblies aren't installed
                return null;
            }

            return Path.Combine(
                    programFiles,
                    "Reference Assemblies", "Microsoft", "Framework");
        }

        private void PopulateCache()
        {
#if DNX451
            if (PlatformHelper.IsMono)
            {
                var mscorlibLocationOnThisRunningMonoInstance = typeof(object).GetTypeInfo().Assembly.Location;

                var libPath = Path.GetDirectoryName(Path.GetDirectoryName(mscorlibLocationOnThisRunningMonoInstance));

                // Mono is a bit inconsistent as .NET 4.5 and .NET 4.5.1 are the
                // same folder
                var supportedVersions = new Dictionary<string, string> {
                    { "4.6", "4.5" },
                    { "4.5.3", "4.5" },
                    { "4.5.1", "4.5" },
                    { "4.5", "4.5" },
                    { "4.0", "4.0" }
                };

                // Temporary cache while enumerating assemblies in directories
                var pathCache = new Dictionary<string, FrameworkInformation>();

                foreach (var versionFolderPair in supportedVersions)
                {
                    var targetFrameworkPath = Path.Combine(libPath, versionFolderPair.Value);

                    if (!Directory.Exists(targetFrameworkPath))
                    {
                        continue;
                    }

                    FrameworkInformation frameworkInfo;
                    if (!pathCache.TryGetValue(targetFrameworkPath, out frameworkInfo))
                    {
                        frameworkInfo = new FrameworkInformation();
                        frameworkInfo.Path = targetFrameworkPath;

                        var assemblies = new List<Tuple<string, string>>();

                        PopulateAssemblies(assemblies, targetFrameworkPath);
                        PopulateAssemblies(assemblies, Path.Combine(targetFrameworkPath, "Facades"));

                        foreach (var pair in assemblies)
                        {
                            var entry = new AssemblyEntry();
                            entry.Path = pair.Item2;
                            frameworkInfo.Assemblies[pair.Item1] = entry;
                        }

                        pathCache[targetFrameworkPath] = frameworkInfo;
                    }

                    var frameworkName = new FrameworkName(VersionUtility.NetFrameworkIdentifier, new Version(versionFolderPair.Key));
                    _cache[frameworkName] = frameworkInfo;

                    List<FrameworkName> aliases;
                    if (_monoAliases.TryGetValue(frameworkName, out aliases))
                    {
                        foreach (var aliasFrameworkName in aliases)
                        {
                            _cache[aliasFrameworkName] = frameworkInfo;
                        }
                    }
                }

                // Not needed anymore
                pathCache.Clear();
            }
#endif
        }

        private static FrameworkInformation GetFrameworkInformation(FrameworkName targetFramework)
        {
            string referenceAssembliesPath = GetReferenceAssembliesPath();

            if (string.IsNullOrEmpty(referenceAssembliesPath))
            {
                return null;
            }

            List<FrameworkName> candidates;
            if (_aliases.TryGetValue(targetFramework, out candidates))
            {
                foreach (var framework in candidates)
                {
                    var information = GetFrameworkInformation(framework, referenceAssembliesPath);

                    if (information != null)
                    {
                        return information;
                    }
                }

                return null;
            }
            else
            {
                return GetFrameworkInformation(targetFramework, referenceAssembliesPath);
            }
        }

        private static FrameworkInformation GetFrameworkInformation(FrameworkName targetFramework, string referenceAssembliesPath)
        {
            var basePath = Path.Combine(referenceAssembliesPath,
                                        targetFramework.Identifier,
                                        "v" + targetFramework.Version);

            if (!string.IsNullOrEmpty(targetFramework.Profile))
            {
                basePath = Path.Combine(basePath, "Profile", targetFramework.Profile);
            }

            var version = new DirectoryInfo(basePath);
            if (!version.Exists)
            {
                return null;
            }

            return GetFrameworkInformation(version, targetFramework);
        }

        private static FrameworkInformation GetFrameworkInformation(DirectoryInfo directory, FrameworkName targetFramework)
        {
            var frameworkInfo = new FrameworkInformation();
            frameworkInfo.Path = directory.FullName;

            // The redist list contains the list of assemblies for this target framework
            string redistList = Path.Combine(directory.FullName, "RedistList", "FrameworkList.xml");

            if (File.Exists(redistList))
            {
                frameworkInfo.RedistListPath = redistList;

                using (var stream = File.OpenRead(redistList))
                {
                    var frameworkList = XDocument.Load(stream);

                    foreach (var e in frameworkList.Root.Elements())
                    {
                        var assemblyName = e.Attribute("AssemblyName").Value;
                        var version = e.Attribute("Version")?.Value;

                        var entry = new AssemblyEntry();
                        entry.Version = version != null ? Version.Parse(version) : null;
                        frameworkInfo.Assemblies.Add(assemblyName, entry);
                    }

                    var nameAttribute = frameworkList.Root.Attribute("Name");

                    frameworkInfo.Name = nameAttribute == null ? null : nameAttribute.Value;
                }
            }

            return frameworkInfo;
        }

        private static void PopulateAssemblies(List<Tuple<string, string>> assemblies, string path)
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            foreach (var assemblyPath in Directory.GetFiles(path, "*.dll"))
            {
                assemblies.Add(Tuple.Create(Path.GetFileNameWithoutExtension(assemblyPath), assemblyPath));
            }
        }

        private static string GetAssemblyPath(string basePath, string assemblyName)
        {
            var assemblyPath = Path.Combine(basePath, assemblyName + ".dll");

            if (File.Exists(assemblyPath))
            {
                return assemblyPath;
            }

            var facadePath = Path.Combine(basePath, "Facades", assemblyName + ".dll");

            if (File.Exists(facadePath))
            {
                return facadePath;
            }

            return null;
        }

        private class FrameworkInformation
        {
            public FrameworkInformation()
            {
                Assemblies = new Dictionary<string, AssemblyEntry>();
            }

            public string Path { get; set; }

            public string RedistListPath { get; set; }

            public IDictionary<string, AssemblyEntry> Assemblies { get; private set; }

            public string Name { get; set; }
        }

        private class AssemblyEntry
        {
            public string Path { get; set; }
            public Version Version { get; set; }
        }
    }
}
