// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using Microsoft.Framework.Runtime;

namespace Microsoft.Framework.PackageManager.Packing
{
    public class PackProject
    {
        private readonly ProjectReferenceDependencyProvider _projectReferenceDependencyProvider;
        private readonly IProjectResolver _projectResolver;
        private readonly LibraryDescription _libraryDescription;

        public PackProject(
            ProjectReferenceDependencyProvider projectReferenceDependencyProvider,
            IProjectResolver projectResolver,
            LibraryDescription libraryDescription)
        {
            _projectReferenceDependencyProvider = projectReferenceDependencyProvider;
            _projectResolver = projectResolver;
            _libraryDescription = libraryDescription;
        }

        public string Name { get { return _libraryDescription.Identity.Name; } }
        public string TargetPath { get; private set; }
        public string AppFolder { get; set; }

        public void EmitSource(PackRoot root)
        {
            Console.WriteLine("Packing project dependency {0}", _libraryDescription.Identity.Name);

            Runtime.Project project;
            if (!_projectResolver.TryResolveProject(_libraryDescription.Identity.Name, out project))
            {
                throw new Exception("TODO: unable to resolve project named " + _libraryDescription.Identity.Name);
            }

            var targetName = project.Name;
            TargetPath = Path.Combine(root.OutputPath, PackRoot.AppRootName, "src", targetName);

            Console.WriteLine("  Source {0}", project.ProjectDirectory);
            Console.WriteLine("  Target {0}", TargetPath);

            root.Operations.Delete(TargetPath);

            // A set of excluded files used as a filter when doing copy
            var excludeFiles = new HashSet<string>(project.ExcludeFiles, StringComparer.OrdinalIgnoreCase);

            root.Operations.Copy(project.ProjectDirectory, TargetPath, (isRoot, filePath) =>
            {
                if (excludeFiles.Contains(filePath))
                {
                    return false;
                }

                return true;
            });
        }

        public void EmitNupkg(PackRoot root)
        {
            Console.WriteLine("Packing nupkg from project dependency {0}", _libraryDescription.Identity.Name);

            Runtime.Project project;
            if (!_projectResolver.TryResolveProject(_libraryDescription.Identity.Name, out project))
            {
                throw new Exception("TODO: unable to resolve project named " + _libraryDescription.Identity.Name);
            }

            var targetName = project.Name;
            var targetNupkgName = string.Format("{0}.{1}", targetName, project.Version);
            TargetPath = Path.Combine(root.OutputPath, PackRoot.AppRootName, "packages", targetNupkgName);

            Console.WriteLine("  Source {0}", project.ProjectDirectory);
            Console.WriteLine("  Target {0}", TargetPath);

            if (Directory.Exists(TargetPath))
            {
                if (root.Overwrite)
                {
                    root.Operations.Delete(TargetPath);
                }
                else
                {
                    Console.WriteLine("  {0} already exists.", TargetPath);
                    return;
                }
            }

            // Generate nupkg from this project dependency
            var buildOptions = new BuildOptions();
            buildOptions.ProjectDir = project.ProjectDirectory;
            buildOptions.OutputDir = Path.Combine(project.ProjectDirectory, "bin");
            buildOptions.Configurations.Add(root.Configuration);
            var buildManager = new BuildManager(buildOptions);
            if (!buildManager.Build())
            {
                return;
            }

            // Extract the generated nupkg to target path
            var srcNupkgPath = Path.Combine(buildOptions.OutputDir, root.Configuration, targetNupkgName + ".nupkg");
            var targetNupkgPath = Path.Combine(TargetPath, targetNupkgName + ".nupkg");
            using (var sourceStream = new FileStream(srcNupkgPath, FileMode.Open, FileAccess.Read))
            {
                using (var archive = new ZipArchive(sourceStream, ZipArchiveMode.Read))
                {
                    root.Operations.ExtractNupkg(archive, TargetPath);
                }
            }
            using (var sourceStream = new FileStream(srcNupkgPath, FileMode.Open, FileAccess.Read))
            {
                using (var targetStream = new FileStream(targetNupkgPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    sourceStream.CopyTo(targetStream);
                }

                sourceStream.Seek(0, SeekOrigin.Begin);
                var sha512Bytes = SHA512.Create().ComputeHash(sourceStream);
                File.WriteAllText(targetNupkgPath + ".sha512", Convert.ToBase64String(sha512Bytes));
            }
        }

        public void PostProcess(PackRoot root)
        {
            Project project;
            if (!_projectResolver.TryResolveProject(_libraryDescription.Identity.Name, out project))
            {
                throw new Exception("TODO: unable to resolve project named " + _libraryDescription.Identity.Name);
            }

            // Construct path to public app folder, which contains content files and tool dlls
            // The name of public app folder is specified with "--appfolder" option
            // Default name of public app folder is the same as main project
            var appFolderPath = Path.Combine(root.OutputPath, AppFolder ?? project.Name);

            // Delete old public app folder because we don't want leftovers from previous operations
            root.Operations.Delete(appFolderPath);
            Directory.CreateDirectory(appFolderPath);

            // Copy content files (e.g. html, js and images) of main project into public app folder
            CopyContentFiles(root, project);

            // Tool dlls including AspNet.Loader.dll go to bin folder under public app folder
            var appFolderBinPath = Path.Combine(appFolderPath, "bin");

            var defaultRuntime = root.Runtimes.FirstOrDefault();
            var iniFilePath = Path.Combine(TargetPath, "k.ini");
            if (defaultRuntime != null && !File.Exists(iniFilePath))
            {
                var parts = defaultRuntime.Name.Split(new []{'.'}, 2);
                if (parts.Length == 2)
                {
                    var versionNumber = parts[1];
                    parts = parts[0].Split(new []{'-'}, 3);
                    if (parts.Length == 3)
                    {
                        var flavor = parts[1];
                        File.WriteAllText(iniFilePath, string.Format(@"[Runtime]
KRE_VERSION={0}
KRE_FLAVOR={1}
CONFIGURATION={2}
", 
versionNumber, 
flavor == "svrc50" ? "CoreCLR" : "DesktopCLR",
root.Configuration));
                    }
                }
            }

            // Generate k.ini for public app folder
            var appFolderIniFilePath = Path.Combine(appFolderPath, "k.ini");
            var appBaseLine = string.Format("APP_BASE={0}",
                Path.Combine("..", PackRoot.AppRootName, "src", project.Name));
            var iniContents = string.Empty;
            if (File.Exists(iniFilePath))
            {
                iniContents = File.ReadAllText(iniFilePath);
            }
            File.WriteAllText(appFolderIniFilePath,
                string.Format("{0}{1}", iniContents, appBaseLine));

            // Copy tools/*.dll into bin to support AspNet.Loader.dll
            foreach (var package in root.Packages)
            {
                var packageToolsPath = Path.Combine(package.TargetPath, "tools");
                if (Directory.Exists(packageToolsPath))
                {
                    foreach (var packageToolFile in Directory.EnumerateFiles(packageToolsPath, "*.dll").Select(Path.GetFileName))
                    {
                        if (!Directory.Exists(appFolderBinPath))
                        {
                            Directory.CreateDirectory(appFolderBinPath);
                        }

                        // Copy to bin folder under public app folder
                        File.Copy(
                            Path.Combine(packageToolsPath, packageToolFile),
                            Path.Combine(appFolderBinPath, packageToolFile),
                            true);
                    }
                }
            }
        }

        private void CopyContentFiles(PackRoot root, Project project)
        {
            var targetName = AppFolder ?? project.Name;
            Console.WriteLine("Copying contents of project dependency {0} to {1}",
                _libraryDescription.Identity.Name, targetName);

            var appFolderPath = Path.Combine(root.OutputPath, targetName);

            Console.WriteLine("  Source {0}", project.ProjectDirectory);
            Console.WriteLine("  Target {0}", appFolderPath);

            // A set of content files that should be copied
            var contentFiles = new HashSet<string>(project.ContentFiles, StringComparer.OrdinalIgnoreCase);

            root.Operations.Copy(project.ProjectDirectory, appFolderPath, (isRoot, filePath) =>
            {
                // We always explore a directory
                if (Directory.Exists(filePath))
                {
                    return true;
                }

                var fileName = Path.GetFileName(filePath);
                // Public app folder doesn't need project.json
                if (string.Equals(fileName, "project.json", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                return contentFiles.Contains(filePath);
            });
        }

        private bool IncludeRuntimeFileInBundle(string relativePath, string fileName)
        {
            return true;
        }

        private string BasePath(string relativePath)
        {
            var index1 = (relativePath + Path.DirectorySeparatorChar).IndexOf(Path.DirectorySeparatorChar);
            var index2 = (relativePath + Path.AltDirectorySeparatorChar).IndexOf(Path.AltDirectorySeparatorChar);
            return relativePath.Substring(0, Math.Min(index1, index2));
        }

    }
}
