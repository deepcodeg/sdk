﻿// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using NuGet.Configuration;
using NuGet.ProjectModel;
using NuGet.Versioning;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Microsoft.NET.Build.Tasks
{
    /// <summary>
    /// Raises Nuget LockFile representation to MSBuild items and resolves
    /// assets specified in the lock file.
    /// </summary>
    public sealed class ResolvePackageDependencies : TaskBase
    {
        private const string ProjectTypeKey = "project";
        private const string PackageTypeKey = "package";
        private readonly Dictionary<string, string> _fileTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _projectFileDependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private IPackageResolver _packageResolver;
        private LockFile _lockFile;

        #region Output Items

        private readonly List<ITaskItem> _targetDefinitions = new List<ITaskItem>();
        private readonly List<ITaskItem> _packageDefinitions = new List<ITaskItem>();
        private readonly List<ITaskItem> _fileDefinitions = new List<ITaskItem>();
        private readonly List<ITaskItem> _packageDependencies = new List<ITaskItem>();
        private readonly List<ITaskItem> _fileDependencies = new List<ITaskItem>();

        /// <summary>
        /// All the targets in the lock file.
        /// </summary>
        [Output]
        public ITaskItem[] TargetDefinitions
        {
            get { return _targetDefinitions.ToArray(); }
        }

        /// <summary>
        /// All the libraries/packages in the lock file.
        /// </summary>
        [Output]
        public ITaskItem[] PackageDefinitions
        {
            get { return _packageDefinitions.ToArray(); }
        }

        /// <summary>
        /// All the files in the lock file
        /// </summary>
        [Output]
        public ITaskItem[] FileDefinitions
        {
            get { return _fileDefinitions.ToArray(); }
        }

        /// <summary>
        /// All the dependencies between packages. Each package has metadata 'ParentPackage' 
        /// to refer to the package that depends on it. For top level packages this value is blank.
        /// </summary>
        [Output]
        public ITaskItem[] PackageDependencies
        {
            get { return _packageDependencies.ToArray(); }
        }

        /// <summary>
        /// All the dependencies between files and packages, labeled by the group containing
        /// the file (e.g. CompileTimeAssembly, RuntimeAssembly, etc.)
        /// </summary>
        [Output]
        public ITaskItem[] FileDependencies
        {
            get { return _fileDependencies.ToArray(); }
        }

        #endregion

        #region Inputs

        /// <summary>
        /// The path to the current project.
        /// </summary>
        [Required]
        public string ProjectPath
        {
            get; set;
        }

        /// <summary>
        /// The assets file to process
        /// </summary>
        [Required]
        public string ProjectAssetsFile
        {
            get; set;
        }

        /// <summary>
        /// Optional the Project Language (E.g. C#, VB)
        /// </summary>
        public string ProjectLanguage
        {
            get; set;
        }

        #endregion

        public ResolvePackageDependencies()
        {
        }

        #region Test Support

        internal ResolvePackageDependencies(LockFile lockFile, IPackageResolver packageResolver)
            : this()
        {
            _lockFile = lockFile;
            _packageResolver = packageResolver;
        }

        #endregion

        private IPackageResolver PackageResolver
        {
            get
            {
                if (_packageResolver == null)
                {
                    _packageResolver = NuGetPackageResolver.CreateResolver(LockFile, ProjectPath);
                }

                return _packageResolver;
            }
        }

        private LockFile LockFile
        {
            get
            {
                if (_lockFile == null)
                {
                    _lockFile = new LockFileCache(BuildEngine4).GetLockFile(ProjectAssetsFile);
                }

                return _lockFile;
            }
        }

        /// <summary>
        /// Raise Nuget LockFile representation to MSBuild items
        /// </summary>
        protected override void ExecuteCore()
        {
            ReadProjectFileDependencies();
            RaiseLockFileTargets();
            GetPackageAndFileDefinitions();
            GetDependencyDiagnostics();
        }

        private void ReadProjectFileDependencies()
        {
            foreach (var group in LockFile.ProjectFileDependencyGroups)
            {
                foreach (var dep in group.Dependencies)
                {
                    // Get package name from e.g. Microsoft.VSSDK.BuildTools >= 15.0.25604-Preview4
                    _projectFileDependencies.Add(dep.Split()[0].Trim());
                }
            }
        }

        // get library and file definitions
        private void GetPackageAndFileDefinitions()
        {
            TaskItem item;
            foreach (var package in LockFile.Libraries)
            {
                var packageName = package.Name;
                var packageVersion = package.Version.ToNormalizedString();
                string packageId = $"{packageName}/{packageVersion}";
                item = new TaskItem(packageId);
                item.SetMetadata(MetadataKeys.Name, packageName);
                item.SetMetadata(MetadataKeys.Type, package.Type);
                item.SetMetadata(MetadataKeys.Version, packageVersion);

                item.SetMetadata(MetadataKeys.Path, package.Path ?? string.Empty);

                string resolvedPackagePath = ResolvePackagePath(package);
                item.SetMetadata(MetadataKeys.ResolvedPath, resolvedPackagePath ?? string.Empty);

                _packageDefinitions.Add(item);

                foreach (var file in package.Files)
                {
                    if (NuGetUtils.IsPlaceholderFile(file))
                    {
                        continue;
                    }

                    var fileKey = $"{packageId}/{file}";
                    var fileItem = new TaskItem(fileKey);
                    fileItem.SetMetadata(MetadataKeys.Path, file);
                    fileItem.SetMetadata(MetadataKeys.PackageName, packageName);
                    fileItem.SetMetadata(MetadataKeys.PackageVersion, packageVersion);

                    string resolvedPath = ResolveFilePath(file, resolvedPackagePath);
                    fileItem.SetMetadata(MetadataKeys.ResolvedPath, resolvedPath ?? string.Empty);

                    if (IsAnalyzer(file))
                    {
                        fileItem.SetMetadata(MetadataKeys.Analyzer, "true");
                        fileItem.SetMetadata(MetadataKeys.Type, "AnalyzerAssembly");

                        // get targets that contain this package
                        var parentTargets = LockFile.Targets
                            .Where(t => t.Libraries.Any(lib => lib.Name == package.Name));

                        foreach (var target in parentTargets)
                        {
                            var fileDepsItem = new TaskItem(fileKey);
                            fileDepsItem.SetMetadata(MetadataKeys.ParentTarget, target.Name); // Foreign Key
                            fileDepsItem.SetMetadata(MetadataKeys.ParentPackage, packageId); // Foreign Key

                            _fileDependencies.Add(fileDepsItem);
                        }
                    }
                    else
                    {
                        // get a type for the file if one is available
                        string fileType;
                        if (!_fileTypes.TryGetValue(fileKey, out fileType))
                        {
                            fileType = "unknown";
                        }
                        fileItem.SetMetadata(MetadataKeys.Type, fileType);
                    }

                    _fileDefinitions.Add(fileItem);
                }
            }
        }

        private bool IsAnalyzer(string file)
        {
            bool isAnalyzer = false;

            if (file.StartsWith("analyzers", StringComparison.Ordinal)
                && Path.GetExtension(file).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            {
                var projectLanguage = NuGetUtils.GetLockFileLanguageName(ProjectLanguage);

                if (projectLanguage == "cs" || projectLanguage == "vb")
                {
                    string excludeLanguage = projectLanguage == "vb" ? "cs" : "vb";
                    var fileParts = file.Split('/');

                    isAnalyzer =
                        fileParts.Any(x => x.Equals(projectLanguage, StringComparison.OrdinalIgnoreCase)) ||
                        !fileParts.Any(x => x.Equals(excludeLanguage, StringComparison.OrdinalIgnoreCase));
                }
            }

            return isAnalyzer;
        }

        // get target definitions and package and file dependencies
        private void RaiseLockFileTargets()
        {
            TaskItem item;
            foreach (var target in LockFile.Targets)
            {
                item = new TaskItem(target.Name);
                item.SetMetadata(MetadataKeys.RuntimeIdentifier, target.RuntimeIdentifier ?? string.Empty);
                item.SetMetadata(MetadataKeys.TargetFrameworkMoniker, target.TargetFramework.DotNetFrameworkName);
                item.SetMetadata(MetadataKeys.FrameworkName, target.TargetFramework.Framework);
                item.SetMetadata(MetadataKeys.FrameworkVersion, target.TargetFramework.Version.ToString());
                item.SetMetadata(MetadataKeys.Type, "target");

                _targetDefinitions.Add(item);

                // raise each library in the target
                GetPackageAndFileDependencies(target);
            }
        }

        private void GetPackageAndFileDependencies(LockFileTarget target)
        {
            var resolvedPackageVersions = target.Libraries
                .ToDictionary(pkg => pkg.Name, pkg => pkg.Version.ToNormalizedString(), StringComparer.OrdinalIgnoreCase);

            var transitiveProjectRefs = new HashSet<string>(
                target.Libraries
                    .Where(lib => IsTransitiveProjectReference(lib))
                    .Select(pkg => pkg.Name), 
                StringComparer.OrdinalIgnoreCase);
            
            TaskItem item;
            foreach (var package in target.Libraries)
            {
                string packageId = $"{package.Name}/{package.Version.ToNormalizedString()}";

                if (_projectFileDependencies.Contains(package.Name))
                {
                    item = new TaskItem(packageId);
                    item.SetMetadata(MetadataKeys.ParentTarget, target.Name); // Foreign Key
                    item.SetMetadata(MetadataKeys.ParentPackage, string.Empty); // Foreign Key

                    _packageDependencies.Add(item);
                }

                // get sub package dependencies
                GetPackageDependencies(package, target.Name, resolvedPackageVersions, transitiveProjectRefs);

                // get file dependencies on this package
                GetFileDependencies(package, target.Name);
            }
        }

        // A package is a TransitiveProjectReference if it is a project, is not directly referenced,
        // and does not contain a placeholder compile time assembly
        private bool IsTransitiveProjectReference(LockFileTargetLibrary package) =>
            string.Equals(package.Type, ProjectTypeKey, StringComparison.OrdinalIgnoreCase) &&
            !_projectFileDependencies.Contains(package.Name) &&
            package.CompileTimeAssemblies.FirstOrDefault(f => NuGetUtils.IsPlaceholderFile(f.Path)) == null;

        private void GetPackageDependencies(
            LockFileTargetLibrary package, 
            string targetName, 
            Dictionary<string, string> resolvedPackageVersions,
            HashSet<string> transitiveProjectRefs)
        {
            string packageId = $"{package.Name}/{package.Version.ToNormalizedString()}";
            TaskItem item;
            foreach (var deps in package.Dependencies)
            {
                string version;
                if (!resolvedPackageVersions.TryGetValue(deps.Id, out version))
                {
                    Log.LogError(Strings.UnexpectedDependencyWithNoVersionNumber, deps.Id);
                    continue;
                }

                string depsName = $"{deps.Id}/{version}";

                item = new TaskItem(depsName);
                item.SetMetadata(MetadataKeys.ParentTarget, targetName); // Foreign Key
                item.SetMetadata(MetadataKeys.ParentPackage, packageId); // Foreign Key

                if (transitiveProjectRefs.Contains(deps.Id))
                {
                    item.SetMetadata(MetadataKeys.TransitiveProjectReference, "true");
                }

                _packageDependencies.Add(item);
            }
        }

        private void GetFileDependencies(LockFileTargetLibrary package, string targetName)
        {
            string packageId = $"{package.Name}/{package.Version.ToNormalizedString()}";

            // for each type of file group
            foreach (var fileGroup in (FileGroup[])Enum.GetValues(typeof(FileGroup)))
            {
                var filePathList = fileGroup.GetFilePathAndProperties(package);
                foreach (var entry in filePathList)
                {
                    string filePath = entry.Item1;
                    IDictionary<string, string> properties = entry.Item2;

                    if (NuGetUtils.IsPlaceholderFile(filePath))
                    {
                        continue;
                    }

                    var fileKey = $"{packageId}/{filePath}";
                    var item = new TaskItem(fileKey);
                    item.SetMetadata(MetadataKeys.FileGroup, fileGroup.ToString());
                    item.SetMetadata(MetadataKeys.ParentTarget, targetName); // Foreign Key
                    item.SetMetadata(MetadataKeys.ParentPackage, packageId); // Foreign Key

                    if (fileGroup == FileGroup.FrameworkAssembly)
                    {
                        // NOTE: the path provided for framework assemblies is the name of the framework reference
                        item.SetMetadata("FrameworkAssembly", filePath);
                        item.SetMetadata(MetadataKeys.PackageName, package.Name);
                        item.SetMetadata(MetadataKeys.PackageVersion, package.Version.ToNormalizedString());
                    }

                    foreach (var property in properties)
                    {
                        item.SetMetadata(property.Key, property.Value);
                    }

                    _fileDependencies.Add(item);

                    // map each file key to a Type metadata value
                    SaveFileKeyType(fileKey, fileGroup);
                }
            }
        }

        private void GetDependencyDiagnostics()
        {
            Dictionary<string, string> projectDeps = LockFile.GetProjectFileDependencies();

            // Temporarily suppress MSBuild logging because these diagnostics are also 
            // reported by NuGet. This will no longer be necessary when NuGet moves 
            // these diagnostics into the lockfile: 
            // - https://github.com/NuGet/Home/issues/1599
            // - https://github.com/dotnet/sdk/issues/585
            bool logToMsBuild = false;

            // if a project dependency is not in the list of libs, then it is an unresolved reference
            var unresolvedDeps = projectDeps.Where(dep =>
                null == LockFile.Libraries.FirstOrDefault(lib =>
                    string.Equals(lib.Name, dep.Key, StringComparison.OrdinalIgnoreCase)));

            foreach (var target in LockFile.Targets)
            {
                foreach (var dep in unresolvedDeps)
                {
                    string packageId = dep.Key;
                    packageId += dep.Value == null ? string.Empty : $"/{dep.Value}";

                    Diagnostics.Add(nameof(Strings.NU1001),
                        string.Format(CultureInfo.CurrentCulture, Strings.NU1001, packageId),
                        ProjectPath,
                        DiagnosticMessageSeverity.Warning,
                        1, 0,
                        target.Name,
                        packageId,
                        logToMSBuild: logToMsBuild
                        );
                }

                // report diagnostic if project dependency version does not match library version
                foreach (var dep in projectDeps)
                {
                    var library = target.Libraries.FirstOrDefault(lib =>
                        string.Equals(lib.Name, dep.Key, StringComparison.OrdinalIgnoreCase));
                    var libraryVersion = library?.Version?.ToNormalizedString();

                    if (libraryVersion != null && dep.Value != null &&
                        !string.Equals(libraryVersion, dep.Value, StringComparison.OrdinalIgnoreCase))
                    {
                        Diagnostics.Add(nameof(Strings.NU1012),
                            string.Format(CultureInfo.CurrentCulture, Strings.NU1012, library.Name, dep.Value, libraryVersion),
                            ProjectPath,
                            DiagnosticMessageSeverity.Warning,
                            1, 0,
                            target.Name,
                            $"{library.Name}/{libraryVersion}", 
                            logToMSBuild: logToMsBuild
                            );
                    }
                }
            }
        }

        // save file type metadata based on the group the file appears in
        private void SaveFileKeyType(string fileKey, FileGroup fileGroup)
        {
            string fileType = fileGroup.GetTypeMetadata();
            if (fileType != null)
            {
                string currentFileType;
                if (!_fileTypes.TryGetValue(fileKey, out currentFileType))
                {
                    _fileTypes.Add(fileKey, fileType);
                }
                else if (currentFileType != fileType)
                {
                    throw new BuildErrorException(Strings.UnexpectedFileType, fileKey, fileType, currentFileType);
                }
            }
        }

        private string ResolvePackagePath(LockFileLibrary package)
        {
            if (string.Equals(package.Type, ProjectTypeKey, StringComparison.OrdinalIgnoreCase))
            {
                var relativeMSBuildProjectPath = package.MSBuildProject;

                if (string.IsNullOrEmpty(relativeMSBuildProjectPath))
                {
                    throw new BuildErrorException(Strings.ProjectAssetsConsumedWithoutMSBuildProjectPath, package.Name, ProjectAssetsFile); 
                }

                return GetAbsolutePathFromProjectRelativePath(relativeMSBuildProjectPath);
            }
            else
            {
                return PackageResolver.GetPackageDirectory(package.Name, package.Version);
            }
        }

        private string ResolveFilePath(string relativePath, string resolvedPackagePath)
        {
            if (NuGetUtils.IsPlaceholderFile(relativePath))
            {
                return null;
            }
            
            relativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
            relativePath = relativePath.Replace('\\', Path.DirectorySeparatorChar);
            return resolvedPackagePath != null
                ? Path.Combine(resolvedPackagePath, relativePath)
                : string.Empty;
        }

        private string GetAbsolutePathFromProjectRelativePath(string path)
        {
            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(ProjectAssetsFile)), path));
        }
    }
}
