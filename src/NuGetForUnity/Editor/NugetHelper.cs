﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
using Debug = UnityEngine.Debug;

[assembly: InternalsVisibleTo("NuGetForUnity.Editor.Tests")]

namespace NugetForUnity
{
    /// <summary>
    ///     A set of helper methods that act as a wrapper around nuget.exe
    ///     TIP: It's incredibly useful to associate .nupkg files as compressed folder in Windows (View like .zip files).  To do this:
    ///     1) Open a command prompt as admin (Press Windows key. Type "cmd".  Right click on the icon and choose "Run as Administrator"
    ///     2) Enter this command: cmd /c assoc .nupkg=CompressedFolder
    /// </summary>
    [InitializeOnLoad]
    public static class NugetHelper
    {
        /// <summary>
        ///     The amount of time, in milliseconds, before the nuget.exe process times out and is killed.
        /// </summary>
        private const int TimeOut = 60000;

        /// <summary>
        ///     The path to the nuget.config file.
        /// </summary>
        public static readonly string NugetConfigFilePath = Path.Combine(Application.dataPath, NugetConfigFile.FileName);

        /// <summary>
        ///     The path to the packages.config file.
        /// </summary>
        private static readonly string PackagesConfigFilePath = Path.Combine(Application.dataPath, PackagesConfigFile.FileName);

        /// <summary>
        ///     The path where to put created (packed) and downloaded (not installed yet) .nupkg files.
        /// </summary>
        public static readonly string PackOutputDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.Combine("NuGet", "Cache"));

        /// <summary>
        ///     Backing field for the packages.config file.
        /// </summary>
        private static PackagesConfigFile packagesConfigFile;

        /// <summary>
        ///     The list of <see cref="NugetPackageSource" />s to use.
        /// </summary>
        private static readonly List<NugetPackageSource> packageSources = new List<NugetPackageSource>();

        /// <summary>
        ///     The dictionary of currently installed <see cref="NugetPackage" />s keyed off of their ID string.
        /// </summary>
        private static readonly Dictionary<string, NugetPackage> installedPackages = new Dictionary<string, NugetPackage>();

        /// <summary>
        ///     The dictionary of cached credentials retrieved by credential providers, keyed by feed URI.
        /// </summary>
        private static readonly Dictionary<Uri, CredentialProviderResponse?> cachedCredentialsByFeedUri =
            new Dictionary<Uri, CredentialProviderResponse?>();

        private static HashSet<string> alreadyImportedLibs;

        private static readonly string[] unityFrameworks = { "unity" };

        private static readonly string[] netStandardFrameworks =
        {
            "netstandard20",
            "netstandard16",
            "netstandard15",
            "netstandard14",
            "netstandard13",
            "netstandard12",
            "netstandard11",
            "netstandard10",
        };

        private static readonly string[] net4Unity2018Frameworks = { "net472", "net471", "net47" };

        private static readonly string[] net4Unity2017Frameworks =
        {
            "net462", "net461", "net46", "net452", "net451", "net45", "net403", "net40", "net4",
        };

        private static readonly string[] net3Frameworks = { "net35-unity full v3.5", "net35-unity subset v3.5", "net35", "net20", "net11" };

        private static readonly string[] net4Unity2021Frameworks = { "net48" };

        private static readonly string[] netStandardUnity2021Frameworks = { "netstandard21" };

        private static readonly string[] defaultFrameworks = { string.Empty };

        // TODO: Move to ScriptableObjet
        private static readonly List<AuthenticatedFeed> knownAuthenticatedFeeds = new List<AuthenticatedFeed>
        {
            new AuthenticatedFeed
            {
                AccountUrlPattern = @"^https:\/\/(?<account>[-a-zA-Z0-9]+)\.pkgs\.visualstudio\.com",
                ProviderUrlTemplate = "https://{account}.pkgs.visualstudio.com/_apis/public/nuget/client/CredentialProviderBundle.zip",
            },
            new AuthenticatedFeed
            {
                AccountUrlPattern = @"^https:\/\/pkgs\.dev\.azure\.com\/(?<account>[-a-zA-Z0-9]+)\/",
                ProviderUrlTemplate = "https://pkgs.dev.azure.com/{account}/_apis/public/nuget/client/CredentialProviderBundle.zip",
            },
        };

        /// <summary>
        ///     Static constructor used by Unity to initialize NuGet and restore packages defined in packages.config.
        /// </summary>
        static NugetHelper()
        {
            AbsoluteProjectPath = Path.GetFullPath(Path.GetDirectoryName(Application.dataPath));
            if (SessionState.GetBool("NugetForUnity.FirstProjectOpen", false))
            {
                return;
            }

            SessionState.SetBool("NugetForUnity.FirstProjectOpen", true);

            // if we are entering playmode, don't do anything
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return;
            }

            // Load the NuGet.config file
            LoadNugetConfigFile();

            // create the nupkgs directory, if it doesn't exist
            if (!Directory.Exists(PackOutputDirectory))
            {
                Directory.CreateDirectory(PackOutputDirectory);
            }

            // restore packages - this will be called EVERY time the project is loaded or a code-file changes
            Restore();
        }

        /// <summary>
        ///     The loaded NuGet.config file that holds the settings for NuGet.
        /// </summary>
        public static NugetConfigFile NugetConfigFile { get; private set; }

        /// <summary>
        ///     Gets the absolute path to the Unity-Project root directory.
        /// </summary>
        internal static string AbsoluteProjectPath { get; }

        /// <summary>
        ///     Gets the loaded packages.config file that hold the dependencies for the project.
        /// </summary>
        public static PackagesConfigFile PackagesConfigFile
        {
            get
            {
                if (packagesConfigFile == null)
                {
                    packagesConfigFile = PackagesConfigFile.Load(PackagesConfigFilePath);
                }

                return packagesConfigFile;
            }
        }

        /// <summary>
        ///     The current .NET version being used (2.0 [actually 3.5], 4.6, etc).
        /// </summary>
        private static ApiCompatibilityLevel DotNetVersion =>
            PlayerSettings.GetApiCompatibilityLevel(EditorUserBuildSettings.selectedBuildTargetGroup);

        /// <summary>
        ///     Gets the dictionary of packages that are actually installed in the project, keyed off of the ID.
        /// </summary>
        /// <returns>A dictionary of installed <see cref="NugetPackage" />s.</returns>
        public static IEnumerable<NugetPackage> InstalledPackages => installedPackages.Values;

        /// <summary>
        ///     Loads the NuGet.config file.
        /// </summary>
        public static void LoadNugetConfigFile()
        {
            if (File.Exists(NugetConfigFilePath))
            {
                NugetConfigFile = NugetConfigFile.Load(NugetConfigFilePath);
            }
            else
            {
                Debug.LogFormat("No NuGet.config file found. Creating default at {0}", NugetConfigFilePath);

                NugetConfigFile = NugetConfigFile.CreateDefaultFile(NugetConfigFilePath);
                AssetDatabase.Refresh();
            }

            // parse any command line arguments
            //LogVerbose("Command line: {0}", Environment.CommandLine);
            packageSources.Clear();
            var readingSources = false;
            var useCommandLineSources = false;
            foreach (var arg in Environment.GetCommandLineArgs())
            {
                if (readingSources)
                {
                    if (arg.StartsWith("-"))
                    {
                        readingSources = false;
                    }
                    else
                    {
                        var source = new NugetPackageSource("CMD_LINE_SRC_" + packageSources.Count, arg);
                        LogVerbose("Adding command line package source {0} at {1}", "CMD_LINE_SRC_" + packageSources.Count, arg);
                        packageSources.Add(source);
                    }
                }

                if (arg == "-Source")
                {
                    // if the source is being forced, don't install packages from the cache
                    NugetConfigFile.InstallFromCache = false;
                    readingSources = true;
                    useCommandLineSources = true;
                }
            }

            // if there are not command line overrides, use the NuGet.config package sources
            if (!useCommandLineSources)
            {
                if (NugetConfigFile.ActivePackageSource.ExpandedPath == "(Aggregate source)")
                {
                    packageSources.AddRange(NugetConfigFile.PackageSources);
                }
                else
                {
                    packageSources.Add(NugetConfigFile.ActivePackageSource);
                }
            }
        }

        /// <summary>
        ///     Runs nuget.exe using the given arguments.
        /// </summary>
        /// <param name="arguments">The arguments to run nuget.exe with.</param>
        /// <param name="logOuput">True to output debug information to the Unity console.  Defaults to true.</param>
        /// <returns>The string of text that was output from nuget.exe following its execution.</returns>
        private static void RunNugetProcess(string arguments, bool logOuput = true)
        {
            // Try to find any nuget.exe in the package tools installation location
            var toolsPackagesFolder = Path.Combine(Application.dataPath, "../Packages");

            // create the folder to prevent an exception when getting the files
            Directory.CreateDirectory(toolsPackagesFolder);

            var files = Directory.GetFiles(toolsPackagesFolder, "nuget.exe", SearchOption.AllDirectories);
            if (files.Length > 1)
            {
                Debug.LogWarningFormat("More than one nuget.exe found. Using first one.");
            }
            else if (files.Length < 1)
            {
                Debug.LogWarningFormat("No nuget.exe found! Attempting to install the NuGet.CommandLine package.");
                InstallIdentifier(new NugetPackageIdentifier("NuGet.CommandLine", "2.8.6"));
                files = Directory.GetFiles(toolsPackagesFolder, "nuget.exe", SearchOption.AllDirectories);
                if (files.Length < 1)
                {
                    Debug.LogErrorFormat("nuget.exe still not found. Quiting...");
                    return;
                }
            }

            LogVerbose("Running: {0} \nArgs: {1}", files[0], arguments);

            var fileName = string.Empty;
            var commandLine = string.Empty;

            if (Environment.OSVersion.Platform == PlatformID.MacOSX)
            {
                // ATTENTION: you must install mono running on your mac, we use this mono to run `nuget.exe`
                fileName = "/Library/Frameworks/Mono.framework/Versions/Current/Commands/mono";
                commandLine = " " + files[0] + " " + arguments;
                LogVerbose("command: " + commandLine);
            }
            else
            {
                fileName = files[0];
                commandLine = arguments;
            }

            var process = Process.Start(
                new ProcessStartInfo(fileName, commandLine)
                {
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,

                    // WorkingDirectory = Path.GettargetFramework(files[0]),

                    // http://stackoverflow.com/questions/16803748/how-to-decode-cmd-output-correctly
                    // Default = 65533, ASCII = ?, Unicode = nothing works at all, UTF-8 = 65533, UTF-7 = 242 = WORKS!, UTF-32 = nothing works at all
                    StandardOutputEncoding = Encoding.GetEncoding(850),
                });

            if (!process.WaitForExit(TimeOut))
            {
                Debug.LogWarning("NuGet took too long to finish.  Killing operation.");
                process.Kill();
            }

            var error = process.StandardError.ReadToEnd();
            if (!string.IsNullOrEmpty(error))
            {
                Debug.LogError(error);
            }

            var output = process.StandardOutput.ReadToEnd();
            if (logOuput && !string.IsNullOrEmpty(output))
            {
                Debug.Log(output);
            }
        }

        /// <summary>
        ///     Replace all %20 encodings with a normal space.
        /// </summary>
        /// <param name="directoryPath">The path to the directory.</param>
        private static void FixSpaces(string directoryPath)
        {
            if (directoryPath.Contains("%20"))
            {
                LogVerbose("Removing %20 from {0}", directoryPath);
                Directory.Move(directoryPath, directoryPath.Replace("%20", " "));
                directoryPath = directoryPath.Replace("%20", " ");
            }

            var subdirectories = Directory.GetDirectories(directoryPath);
            foreach (var subDir in subdirectories)
            {
                FixSpaces(subDir);
            }

            var files = Directory.GetFiles(directoryPath);
            foreach (var file in files)
            {
                if (file.Contains("%20"))
                {
                    LogVerbose("Removing %20 from {0}", file);
                    File.Move(file, file.Replace("%20", " "));
                }
            }
        }

        private static bool FrameworkNamesAreEqual(string tfm1, string tfm2)
        {
            return tfm1.Equals(tfm2, StringComparison.InvariantCultureIgnoreCase);
        }

        /// <summary>
        ///     Cleans up a package after it has been installed.
        ///     Since we are in Unity, we can make certain assumptions on which files will NOT be used, so we can delete them.
        /// </summary>
        /// <param name="package">The NugetPackage to clean.</param>
        private static void Clean(NugetPackageIdentifier package)
        {
            var packageInstallDirectory = Path.Combine(NugetConfigFile.RepositoryPath, string.Format("{0}.{1}", package.Id, package.Version));

            LogVerbose("Cleaning {0}", packageInstallDirectory);

            FixSpaces(packageInstallDirectory);

            // delete a remnant .meta file that may exist from packages created by Unity
            DeleteFile(packageInstallDirectory + "/" + package.Id + ".nuspec.meta");

            // delete directories & files that NuGet normally deletes, but since we are installing "manually" they exist
            DeleteDirectory(packageInstallDirectory + "/_rels");
            DeleteDirectory(packageInstallDirectory + "/package");
            DeleteFile(packageInstallDirectory + "/" + package.Id + ".nuspec");
            DeleteFile(packageInstallDirectory + "/[Content_Types].xml");

            // Unity has no use for the build directory
            DeleteDirectory(packageInstallDirectory + "/build");

            // For now, delete src.  We may use it later...
            DeleteDirectory(packageInstallDirectory + "/src");

            // Since we don't automatically fix up the runtime dll platforms, remove them until we improve support
            // for this newer feature of nuget packages.
            DeleteDirectory(Path.Combine(packageInstallDirectory, "runtimes"));

            // Delete documentation folders since they sometimes have HTML docs with JavaScript, which Unity tried to parse as "UnityScript"
            DeleteDirectory(packageInstallDirectory + "/docs");

            // Delete ref folder, as it is just used for compile-time reference and does not contain implementations.
            // Leaving it results in "assembly loading" and "multiple pre-compiled assemblies with same name" errors
            DeleteDirectory(packageInstallDirectory + "/ref");

            if (Directory.Exists(packageInstallDirectory + "/lib"))
            {
                var selectedDirectories = new List<string>();

                // go through the library folders in descending order (highest to lowest version)
                var libDirectories = Directory.GetDirectories(packageInstallDirectory + "/lib").Select(s => new DirectoryInfo(s));
                var targetFrameworks = libDirectories.Select(x => x.Name.ToLower());

                var isAlreadyImported = IsAlreadyImportedInEngine(package);
                var bestTargetFramework = TryGetBestTargetFrameworkForCurrentSettings(targetFrameworks);
                if (!isAlreadyImported && bestTargetFramework != null)
                {
                    var bestLibDirectory = libDirectories.First(x => FrameworkNamesAreEqual(x.Name, bestTargetFramework));

                    if (bestTargetFramework == "unity" ||
                        bestTargetFramework == "net35-unity full v3.5" ||
                        bestTargetFramework == "net35-unity subset v3.5")
                    {
                        selectedDirectories.Add(Path.Combine(bestLibDirectory.Parent.FullName, "unity"));
                        selectedDirectories.Add(Path.Combine(bestLibDirectory.Parent.FullName, "net35-unity full v3.5"));
                        selectedDirectories.Add(Path.Combine(bestLibDirectory.Parent.FullName, "net35-unity subset v3.5"));
                    }
                    else
                    {
                        selectedDirectories.Add(bestLibDirectory.FullName);
                    }
                }

                foreach (var directory in selectedDirectories)
                {
                    LogVerbose("Using {0}", directory);
                }

                // delete all of the libaries except for the selected one
                foreach (var directory in libDirectories)
                {
                    var validDirectory = selectedDirectories.Where(d => string.Compare(d, directory.FullName, true) == 0).Any();

                    if (!validDirectory)
                    {
                        DeleteDirectory(directory.FullName);
                    }
                }
            }

            if (Directory.Exists(packageInstallDirectory + "/tools"))
            {
                // Move the tools folder outside of the Unity Assets folder
                var toolsInstallDirectory = Path.Combine(
                    Application.dataPath,
                    string.Format("../Packages/{0}.{1}/tools", package.Id, package.Version));

                LogVerbose("Moving {0} to {1}", packageInstallDirectory + "/tools", toolsInstallDirectory);

                // create the directory to create any of the missing folders in the path
                Directory.CreateDirectory(toolsInstallDirectory);

                // delete the final directory to prevent the Move operation from throwing exceptions.
                DeleteDirectory(toolsInstallDirectory);

                Directory.Move(packageInstallDirectory + "/tools", toolsInstallDirectory);
            }

            // delete all PDB files since Unity uses Mono and requires MDB files, which causes it to output "missing MDB" errors
            DeleteAllFiles(packageInstallDirectory, "*.pdb");

            // if there are native DLLs, copy them to the Unity project root (1 up from Assets)
            if (Directory.Exists(packageInstallDirectory + "/output"))
            {
                var files = Directory.GetFiles(packageInstallDirectory + "/output");
                foreach (var file in files)
                {
                    var newFilePath = Directory.GetCurrentDirectory() + "/" + Path.GetFileName(file);
                    LogVerbose("Moving {0} to {1}", file, newFilePath);
                    DeleteFile(newFilePath);
                    File.Move(file, newFilePath);
                }

                LogVerbose("Deleting {0}", packageInstallDirectory + "/output");

                DeleteDirectory(packageInstallDirectory + "/output");
            }

            // if there are Unity plugin DLLs, copy them to the Unity Plugins folder (Assets/Plugins)
            if (Directory.Exists(packageInstallDirectory + "/unityplugin"))
            {
                var pluginsDirectory = Application.dataPath + "/Plugins/";

                DirectoryCopy(packageInstallDirectory + "/unityplugin", pluginsDirectory);

                LogVerbose("Deleting {0}", packageInstallDirectory + "/unityplugin");

                DeleteDirectory(packageInstallDirectory + "/unityplugin");
            }

            // if there are Unity StreamingAssets, copy them to the Unity StreamingAssets folder (Assets/StreamingAssets)
            if (Directory.Exists(packageInstallDirectory + "/StreamingAssets"))
            {
                var streamingAssetsDirectory = Application.dataPath + "/StreamingAssets/";

                if (!Directory.Exists(streamingAssetsDirectory))
                {
                    Directory.CreateDirectory(streamingAssetsDirectory);
                }

                // move the files
                var files = Directory.GetFiles(packageInstallDirectory + "/StreamingAssets");
                foreach (var file in files)
                {
                    var newFilePath = streamingAssetsDirectory + Path.GetFileName(file);

                    try
                    {
                        LogVerbose("Moving {0} to {1}", file, newFilePath);
                        DeleteFile(newFilePath);
                        File.Move(file, newFilePath);
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarningFormat("{0} couldn't be moved. \n{1}", newFilePath, e.ToString());
                    }
                }

                // move the directories
                var directories = Directory.GetDirectories(packageInstallDirectory + "/StreamingAssets");
                foreach (var directory in directories)
                {
                    var newDirectoryPath = streamingAssetsDirectory + new DirectoryInfo(directory).Name;

                    try
                    {
                        LogVerbose("Moving {0} to {1}", directory, newDirectoryPath);
                        if (Directory.Exists(newDirectoryPath))
                        {
                            DeleteDirectory(newDirectoryPath);
                        }

                        Directory.Move(directory, newDirectoryPath);
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarningFormat("{0} couldn't be moved. \n{1}", newDirectoryPath, e.ToString());
                    }
                }

                // delete the package's StreamingAssets folder and .meta file
                LogVerbose("Deleting {0}", packageInstallDirectory + "/StreamingAssets");
                DeleteDirectory(packageInstallDirectory + "/StreamingAssets");
                DeleteFile(packageInstallDirectory + "/StreamingAssets.meta");
            }
        }

        private static bool IsAlreadyImportedInEngine(NugetPackageIdentifier package)
        {
            var alreadyImportedLibs = GetAlreadyImportedLibs();
            var isAlreadyImported = alreadyImportedLibs.Contains(package.Id);
            LogVerbose("Is package '{0}' already imported? {1}", package.Id, isAlreadyImported);
            return isAlreadyImported;
        }

        private static HashSet<string> GetAlreadyImportedLibs()
        {
            if (alreadyImportedLibs == null)
            {
                // Find all the dll's already installed by NuGetForUnity
                var alreadyInstalledDllFileNames = new HashSet<string>();

                if (NugetConfigFile != null && Directory.Exists(NugetConfigFile.RepositoryPath))
                {
                    alreadyInstalledDllFileNames = new HashSet<string>(
                        Directory.EnumerateFiles(NugetConfigFile.RepositoryPath, "*.dll", SearchOption.AllDirectories)
                            .Select(Path.GetFileNameWithoutExtension));
                }

                // Get all assemblies loaded into Unity and filter out those installed by NuGetForUnity
                alreadyImportedLibs = new HashSet<string>(
                    AppDomain.CurrentDomain.GetAssemblies()
                        .Select(a => a.ManifestModule.Name)
                        .Select(p => Path.ChangeExtension(p, null))
                        .Where(p => !alreadyInstalledDllFileNames.Contains(p)));

                LogVerbose("Already imported libs: {0}", string.Join(", ", alreadyImportedLibs));
            }

            return alreadyImportedLibs;
        }

        public static NugetFrameworkGroup GetBestDependencyFrameworkGroupForCurrentSettings(NugetPackage package)
        {
            var targetFrameworks = package.Dependencies.Select(x => x.TargetFramework);

            var bestTargetFramework = TryGetBestTargetFrameworkForCurrentSettings(targetFrameworks);
            return package.Dependencies.FirstOrDefault(x => FrameworkNamesAreEqual(x.TargetFramework, bestTargetFramework)) ??
                   new NugetFrameworkGroup();
        }

        public static NugetFrameworkGroup GetBestDependencyFrameworkGroupForCurrentSettings(NuspecFile nuspec)
        {
            var targetFrameworks = nuspec.Dependencies.Select(x => x.TargetFramework);

            var bestTargetFramework = TryGetBestTargetFrameworkForCurrentSettings(targetFrameworks);
            return nuspec.Dependencies.FirstOrDefault(x => FrameworkNamesAreEqual(x.TargetFramework, bestTargetFramework)) ??
                   new NugetFrameworkGroup();
        }

        public static string TryGetBestTargetFrameworkForCurrentSettings(IEnumerable<string> targetFrameworks)
        {
            var intDotNetVersion = (int)DotNetVersion; // c

            // NET_4_6 option was added in Unity 5.6
            // NET_4_6 = 3 in Unity 5.6 and Unity 2017.1 - use the hard-coded int value to ensure it works in earlier versions of Unity
            // NET_4_8 in Unity 2021.2 is also 3
            // NET_Standard = 6 2.0 and 2.1 since Unity 2021.2 have the same value
            var using46 = intDotNetVersion == 3;
            var usingStandard = intDotNetVersion == 6; // using .net standard 2.0 or 2.1

            var frameworkGroups = new List<string[]> { unityFrameworks };

            if (usingStandard)
            {
                if (UnityVersion.Current >= new UnityVersion(2021, 2, 0, 'f', 0))
                {
                    frameworkGroups.Add(netStandardUnity2021Frameworks);
                }

                frameworkGroups.Add(netStandardFrameworks);
            }
            else if (using46)
            {
                if (UnityVersion.Current >= new UnityVersion(2021, 2, 0, 'f', 0))
                {
                    frameworkGroups.Add(net4Unity2021Frameworks);
                }

                if (UnityVersion.Current.Major >= 2018)
                {
                    frameworkGroups.Add(net4Unity2018Frameworks);
                }

                if (UnityVersion.Current.Major >= 2017)
                {
                    frameworkGroups.Add(net4Unity2017Frameworks);
                }

                frameworkGroups.Add(net3Frameworks);
                frameworkGroups.Add(netStandardFrameworks);

                if (UnityVersion.Current >= new UnityVersion(2021, 2, 0, 'f', 0))
                {
                    frameworkGroups.Add(netStandardUnity2021Frameworks);
                }
            }
            else
            {
                frameworkGroups.Add(net3Frameworks);
            }

            frameworkGroups.Add(defaultFrameworks);

            Func<string, int> getTfmPriority = tfm =>
            {
                for (var i = 0; i < frameworkGroups.Count; ++i)
                {
                    var index = Array.FindIndex(
                        frameworkGroups[i],
                        test =>
                        {
                            if (test.Equals(tfm, StringComparison.InvariantCultureIgnoreCase))
                            {
                                return true;
                            }

                            if (test.Equals(tfm.Replace(".", string.Empty), StringComparison.InvariantCultureIgnoreCase))
                            {
                                return true;
                            }

                            return false;
                        });

                    if (index >= 0)
                    {
                        return i * 1000 + index;
                    }
                }

                return int.MaxValue;
            };

            // Select the highest .NET library available that is supported
            // See here: https://docs.nuget.org/ndocs/schema/target-frameworks
            var result = targetFrameworks.Select(tfm => new PriorityFramework { Priority = getTfmPriority(tfm), Framework = tfm })
                .Where(pfm => pfm.Priority != int.MaxValue)
                .ToArray() // Ensure we don't search for priorities again when sorting
                .OrderBy(pfm => pfm.Priority)
                .Select(pfm => pfm.Framework)
                .FirstOrDefault();

            LogVerbose("Selecting {0} as the best target framework for current settings", result ?? "(null)");
            return result;
        }

        /// <summary>
        ///     Calls "nuget.exe pack" to create a .nupkg file based on the given .nuspec file.
        /// </summary>
        /// <param name="nuspecFilePath">The full filepath to the .nuspec file to use.</param>
        public static void Pack(string nuspecFilePath)
        {
            if (!Directory.Exists(PackOutputDirectory))
            {
                Directory.CreateDirectory(PackOutputDirectory);
            }

            // Use -NoDefaultExcludes to allow files and folders that start with a . to be packed into the package
            // This is done because if you want a file/folder in a Unity project, but you want Unity to ignore it, it must start with a .
            // This is especially useful for .cs and .js files that you don't want Unity to compile as game scripts
            var arguments = string.Format("pack \"{0}\" -OutputDirectory \"{1}\" -NoDefaultExcludes", nuspecFilePath, PackOutputDirectory);

            RunNugetProcess(arguments);
        }

        /// <summary>
        ///     Calls "nuget.exe push" to push a .nupkf file to the the server location defined in the NuGet.config file.
        ///     Note: This differs slightly from NuGet's Push command by automatically calling Pack if the .nupkg doesn't already exist.
        /// </summary>
        /// <param name="nuspec">The NuspecFile which defines the package to push.  Only the ID and Version are used.</param>
        /// <param name="nuspecFilePath">The full filepath to the .nuspec file to use.  This is required by NuGet's Push command.</param>
        /// ///
        /// <param name="apiKey">The API key to use when pushing a package to the server.  This is optional.</param>
        public static void Push(NuspecFile nuspec, string nuspecFilePath, string apiKey = "")
        {
            var packagePath = Path.Combine(PackOutputDirectory, string.Format("{0}.{1}.nupkg", nuspec.Id, nuspec.Version));
            if (!File.Exists(packagePath))
            {
                LogVerbose("Attempting to Pack.");
                Pack(nuspecFilePath);

                if (!File.Exists(packagePath))
                {
                    Debug.LogErrorFormat("NuGet package not found: {0}", packagePath);
                    return;
                }
            }

            var arguments = string.Format("push \"{0}\" {1} -configfile \"{2}\"", packagePath, apiKey, NugetConfigFilePath);

            RunNugetProcess(arguments);
        }

        /// <summary>
        ///     Recursively copies all files and sub-directories from one directory to another.
        /// </summary>
        /// <param name="sourceDirectoryPath">The filepath to the folder to copy from.</param>
        /// <param name="destDirectoryPath">The filepath to the folder to copy to.</param>
        private static void DirectoryCopy(string sourceDirectoryPath, string destDirectoryPath)
        {
            var dir = new DirectoryInfo(sourceDirectoryPath);
            if (!dir.Exists)
            {
                throw new DirectoryNotFoundException("Source directory does not exist or could not be found: " + sourceDirectoryPath);
            }

            // if the destination directory doesn't exist, create it
            if (!Directory.Exists(destDirectoryPath))
            {
                LogVerbose("Creating new directory: {0}", destDirectoryPath);
                Directory.CreateDirectory(destDirectoryPath);
            }

            // get the files in the directory and copy them to the new location
            var files = dir.GetFiles();
            foreach (var file in files)
            {
                var newFilePath = Path.Combine(destDirectoryPath, file.Name);

                try
                {
                    LogVerbose("Moving {0} to {1}", file.ToString(), newFilePath);
                    file.CopyTo(newFilePath, true);
                }
                catch (Exception e)
                {
                    Debug.LogWarningFormat(
                        "{0} couldn't be moved to {1}. It may be a native plugin already locked by Unity. Please trying closing Unity and manually moving it. \n{2}",
                        file.ToString(),
                        newFilePath,
                        e.ToString());
                }
            }

            // copy sub-directories and their contents to new location
            var dirs = dir.GetDirectories();
            foreach (var subdir in dirs)
            {
                var temppath = Path.Combine(destDirectoryPath, subdir.Name);
                DirectoryCopy(subdir.FullName, temppath);
            }
        }

        /// <summary>
        ///     Recursively deletes the folder at the given path.
        ///     NOTE: Directory.Delete() doesn't delete Read-Only files, whereas this does.
        /// </summary>
        /// <param name="directoryPath">The path of the folder to delete.</param>
        private static void DeleteDirectory(string directoryPath)
        {
            if (!Directory.Exists(directoryPath))
            {
                return;
            }

            var directoryInfo = new DirectoryInfo(directoryPath);

            // delete any sub-folders first
            foreach (var childInfo in directoryInfo.GetFileSystemInfos())
            {
                DeleteDirectory(childInfo.FullName);
            }

            // remove the read-only flag on all files
            var files = directoryInfo.GetFiles();
            foreach (var file in files)
            {
                file.Attributes = FileAttributes.Normal;
            }

            // remove the read-only flag on the directory
            directoryInfo.Attributes = FileAttributes.Normal;

            // recursively delete the directory
            directoryInfo.Delete(true);
        }

        /// <summary>
        ///     Deletes a file at the given filepath.
        /// </summary>
        /// <param name="filePath">The filepath to the file to delete.</param>
        private static void DeleteFile(string filePath)
        {
            if (File.Exists(filePath))
            {
                File.SetAttributes(filePath, FileAttributes.Normal);
                File.Delete(filePath);
            }
        }

        /// <summary>
        ///     Deletes all files in the given directory or in any sub-directory, with the given extension.
        /// </summary>
        /// <param name="directoryPath">The path to the directory to delete all files of the given extension from.</param>
        /// <param name="extension">The extension of the files to delete, in the form "*.ext"</param>
        private static void DeleteAllFiles(string directoryPath, string extension)
        {
            var files = Directory.GetFiles(directoryPath, extension, SearchOption.AllDirectories);
            foreach (var file in files)
            {
                DeleteFile(file);
            }
        }

        /// <summary>
        ///     Uninstalls all of the currently installed packages.
        /// </summary>
        internal static void UninstallAll()
        {
            foreach (var package in installedPackages.Values.ToList())
            {
                Uninstall(package);
            }
        }

        /// <summary>
        ///     "Uninstalls" the given package by simply deleting its folder.
        /// </summary>
        /// <param name="package">The NugetPackage to uninstall.</param>
        /// <param name="refreshAssets">True to force Unity to refesh its Assets folder.  False to temporarily ignore the change.  Defaults to true.</param>
        public static void Uninstall(NugetPackageIdentifier package, bool refreshAssets = true)
        {
            LogVerbose("Uninstalling: {0} {1}", package.Id, package.Version);

            // update the package.config file
            PackagesConfigFile.RemovePackage(package);
            PackagesConfigFile.Save(PackagesConfigFilePath);

            var packageInstallDirectory = Path.Combine(NugetConfigFile.RepositoryPath, string.Format("{0}.{1}", package.Id, package.Version));
            DeleteDirectory(packageInstallDirectory);

            var metaFile = Path.Combine(NugetConfigFile.RepositoryPath, string.Format("{0}.{1}.meta", package.Id, package.Version));
            DeleteFile(metaFile);

            var toolsInstallDirectory = Path.Combine(Application.dataPath, string.Format("../Packages/{0}.{1}", package.Id, package.Version));
            DeleteDirectory(toolsInstallDirectory);

            installedPackages.Remove(package.Id);

            if (refreshAssets)
            {
                AssetDatabase.Refresh();
            }
        }

        /// <summary>
        ///     Updates a package by uninstalling the currently installed version and installing the "new" version.
        /// </summary>
        /// <param name="currentVersion">The current package to uninstall.</param>
        /// <param name="newVersion">The package to install.</param>
        /// <param name="refreshAssets">True to refresh the assets inside Unity.  False to ignore them (for now).  Defaults to true.</param>
        public static bool Update(NugetPackageIdentifier currentVersion, NugetPackage newVersion, bool refreshAssets = true)
        {
            LogVerbose("Updating {0} {1} to {2}", currentVersion.Id, currentVersion.Version, newVersion.Version);
            Uninstall(currentVersion, false);
            return InstallIdentifier(newVersion, refreshAssets);
        }

        /// <summary>
        ///     Installs all of the given updates, and uninstalls the corresponding package that is already installed.
        /// </summary>
        /// <param name="updates">The list of all updates to install.</param>
        /// <param name="packagesToUpdate">The list of all packages currently installed.</param>
        public static void UpdateAll(IEnumerable<NugetPackage> updates, IEnumerable<NugetPackage> packagesToUpdate)
        {
            var progressStep = 1.0f / updates.Count();
            float currentProgress = 0;

            foreach (var update in updates)
            {
                EditorUtility.DisplayProgressBar(
                    string.Format("Updating to {0} {1}", update.Id, update.Version),
                    "Installing All Updates",
                    currentProgress);

                var installedPackage = packagesToUpdate.FirstOrDefault(p => p.Id == update.Id);
                if (installedPackage != null)
                {
                    Update(installedPackage, update, false);
                }
                else
                {
                    Debug.LogErrorFormat("Trying to update {0} to {1}, but no version is installed!", update.Id, update.Version);
                }

                currentProgress += progressStep;
            }

            AssetDatabase.Refresh();

            EditorUtility.ClearProgressBar();
        }

        /// <summary>
        ///     Updates the dictionary of packages that are actually installed in the project based on the files that are currently installed.
        /// </summary>
        public static void UpdateInstalledPackages()
        {
            LoadNugetConfigFile();

            var stopwatch = new Stopwatch();
            stopwatch.Start();

            installedPackages.Clear();

            // loops through the packages that are actually installed in the project
            if (Directory.Exists(NugetConfigFile.RepositoryPath))
            {
                // a package that was installed via NuGet will have the .nupkg it came from inside the folder
                var nupkgFiles = Directory.GetFiles(NugetConfigFile.RepositoryPath, "*.nupkg", SearchOption.AllDirectories);
                foreach (var nupkgFile in nupkgFiles)
                {
                    var package = NugetPackage.FromNupkgFile(nupkgFile);
                    if (!installedPackages.ContainsKey(package.Id))
                    {
                        installedPackages.Add(package.Id, package);
                    }
                    else
                    {
                        Debug.LogErrorFormat("Package is already in installed list: {0}", package.Id);
                    }
                }

                // if the source code & assets for a package are pulled directly into the project (ex: via a symlink/junction) it should have a .nuspec defining the package
                var nuspecFiles = Directory.GetFiles(NugetConfigFile.RepositoryPath, "*.nuspec", SearchOption.AllDirectories);
                foreach (var nuspecFile in nuspecFiles)
                {
                    var package = NugetPackage.FromNuspec(NuspecFile.Load(nuspecFile));
                    if (!installedPackages.ContainsKey(package.Id))
                    {
                        installedPackages.Add(package.Id, package);
                    }
                    else
                    {
                        Debug.LogErrorFormat("Package is already in installed list: {0}", package.Id);
                    }
                }
            }

            stopwatch.Stop();
            LogVerbose("Getting installed packages took {0} ms", stopwatch.ElapsedMilliseconds);
        }

        /// <summary>
        ///     Gets a list of NuGetPackages via the HTTP Search() function defined by NuGet.Server and NuGet Gallery.
        ///     This allows searching for partial IDs or even the empty string (the default) to list ALL packages.
        ///     NOTE: See the functions and parameters defined here: https://www.nuget.org/api/v2/$metadata
        /// </summary>
        /// <param name="searchTerm">The search term to use to filter packages. Defaults to the empty string.</param>
        /// <param name="includeAllVersions">True to include older versions that are not the latest version.</param>
        /// <param name="includePrerelease">True to include prerelease packages (alpha, beta, etc).</param>
        /// <param name="numberToGet">The number of packages to fetch.</param>
        /// <param name="numberToSkip">The number of packages to skip before fetching.</param>
        /// <returns>The list of available packages.</returns>
        public static List<NugetPackage> Search(string searchTerm = "",
            bool includeAllVersions = false,
            bool includePrerelease = false,
            int numberToGet = 15,
            int numberToSkip = 0)
        {
            var packages = new List<NugetPackage>();

            // Loop through all active sources and combine them into a single list
            foreach (var source in packageSources.Where(s => s.IsEnabled))
            {
                var newPackages = source.Search(searchTerm, includeAllVersions, includePrerelease, numberToGet, numberToSkip);
                packages.AddRange(newPackages);
                packages = packages.Distinct().ToList();
            }

            return packages;
        }

        /// <summary>
        ///     Queries the server with the given list of installed packages to get any updates that are available.
        /// </summary>
        /// <param name="packagesToUpdate">The list of currently installed packages.</param>
        /// <param name="includePrerelease">True to include prerelease packages (alpha, beta, etc).</param>
        /// <param name="includeAllVersions">True to include older versions that are not the latest version.</param>
        /// <param name="targetFrameworks">The specific frameworks to target?</param>
        /// <param name="versionContraints">The version constraints?</param>
        /// <returns>A list of all updates available.</returns>
        public static List<NugetPackage> GetUpdates(IEnumerable<NugetPackage> packagesToUpdate,
            bool includePrerelease = false,
            bool includeAllVersions = false,
            string targetFrameworks = "",
            string versionContraints = "")
        {
            var packages = new List<NugetPackage>();

            // Loop through all active sources and combine them into a single list
            foreach (var source in packageSources.Where(s => s.IsEnabled))
            {
                var newPackages = source.GetUpdates(packagesToUpdate, includePrerelease, includeAllVersions, targetFrameworks, versionContraints);
                packages.AddRange(newPackages);
                packages = packages.Distinct().ToList();
            }

            return packages;
        }

        /// <summary>
        ///     Gets a NugetPackage from the NuGet server with the exact ID and Version given.
        /// </summary>
        /// <param name="packageId">The <see cref="NugetPackageIdentifier" /> containing the ID and Version of the package to get.</param>
        /// <returns>The retrieved package, if there is one.  Null if no matching package was found.</returns>
        private static NugetPackage GetSpecificPackage(NugetPackageIdentifier packageId)
        {
            // First look to see if the package is already installed
            var package = GetInstalledPackage(packageId);

            if (package == null)
            {
                // That package isn't installed yet, so look in the cache next
                package = GetCachedPackage(packageId);
            }

            if (package == null)
            {
                // It's not in the cache, so we need to look in the active sources
                package = GetOnlinePackage(packageId);
            }

            return package;
        }

        /// <summary>
        ///     Tries to find an already installed package that matches (or is in the range of) the given package ID.
        /// </summary>
        /// <param name="packageId">The <see cref="NugetPackageIdentifier" /> of the <see cref="NugetPackage" /> to find.</param>
        /// <returns>The best <see cref="NugetPackage" /> match, if there is one, otherwise null.</returns>
        private static NugetPackage GetInstalledPackage(NugetPackageIdentifier packageId)
        {
            NugetPackage installedPackage = null;

            if (installedPackages.TryGetValue(packageId.Id, out installedPackage))
            {
                if (packageId.Version != installedPackage.Version)
                {
                    if (packageId.InRange(installedPackage))
                    {
                        LogVerbose(
                            "Requested {0} {1}, but {2} is already installed, so using that.",
                            packageId.Id,
                            packageId.Version,
                            installedPackage.Version);
                    }
                    else
                    {
                        LogVerbose(
                            "Requested {0} {1}. {2} is already installed, but it is out of range.",
                            packageId.Id,
                            packageId.Version,
                            installedPackage.Version);
                        installedPackage = null;
                    }
                }
                else
                {
                    LogVerbose("Found exact package already installed: {0} {1}", installedPackage.Id, installedPackage.Version);
                }
            }

            return installedPackage;
        }

        /// <summary>
        ///     Tries to find an already cached package that matches the given package ID.
        /// </summary>
        /// <param name="packageId">The <see cref="NugetPackageIdentifier" /> of the <see cref="NugetPackage" /> to find.</param>
        /// <returns>The best <see cref="NugetPackage" /> match, if there is one, otherwise null.</returns>
        private static NugetPackage GetCachedPackage(NugetPackageIdentifier packageId)
        {
            NugetPackage package = null;

            if (NugetConfigFile.InstallFromCache)
            {
                var cachedPackagePath = Path.Combine(PackOutputDirectory, string.Format("./{0}.{1}.nupkg", packageId.Id, packageId.Version));

                if (File.Exists(cachedPackagePath))
                {
                    LogVerbose("Found exact package in the cache: {0}", cachedPackagePath);
                    package = NugetPackage.FromNupkgFile(cachedPackagePath);
                }
            }

            return package;
        }

        /// <summary>
        ///     Tries to find an "online" (in the package sources - which could be local) package that matches (or is in the range of) the given package ID.
        /// </summary>
        /// <param name="packageId">The <see cref="NugetPackageIdentifier" /> of the <see cref="NugetPackage" /> to find.</param>
        /// <returns>The best <see cref="NugetPackage" /> match, if there is one, otherwise null.</returns>
        private static NugetPackage GetOnlinePackage(NugetPackageIdentifier packageId)
        {
            NugetPackage package = null;

            // Loop through all active sources and stop once the package is found
            foreach (var source in packageSources.Where(s => s.IsEnabled))
            {
                var foundPackage = source.GetSpecificPackage(packageId);
                if (foundPackage == null)
                {
                    continue;
                }

                if (foundPackage.Version == packageId.Version)
                {
                    LogVerbose("{0} {1} was found in {2}", foundPackage.Id, foundPackage.Version, source.Name);
                    return foundPackage;
                }

                LogVerbose("{0} {1} was found in {2}, but wanted {3}", foundPackage.Id, foundPackage.Version, source.Name, packageId.Version);
                if (package == null)
                {
                    // if another package hasn't been found yet, use the current found one
                    package = foundPackage;
                }

                // another package has been found previously, but neither match identically
                else if (foundPackage > package)
                {
                    // use the new package if it's closer to the desired version
                    package = foundPackage;
                }
            }

            if (package != null)
            {
                LogVerbose("{0} {1} not found, using {2}", packageId.Id, packageId.Version, package.Version);
            }
            else
            {
                LogVerbose("Failed to find {0} {1}", packageId.Id, packageId.Version);
            }

            return package;
        }

        /// <summary>
        ///     Copies the contents of input to output. Doesn't close either stream.
        /// </summary>
        private static void CopyStream(Stream input, Stream output)
        {
            var buffer = new byte[8 * 1024];
            int len;
            while ((len = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, len);
            }
        }

        /// <summary>
        ///     Installs the package given by the identifier.  It fetches the appropriate full package from the installed packages, package cache, or package
        ///     sources and installs it.
        /// </summary>
        /// <param name="package">The identifier of the package to install.</param>
        /// <param name="refreshAssets">True to refresh the Unity asset database.  False to ignore the changes (temporarily).</param>
        internal static bool InstallIdentifier(NugetPackageIdentifier package, bool refreshAssets = true)
        {
            if (IsAlreadyImportedInEngine(package))
            {
                LogVerbose("Package {0} is already imported in engine, skipping install.", package);
                return true;
            }

            var foundPackage = GetSpecificPackage(package);

            if (foundPackage != null)
            {
                return Install(foundPackage, refreshAssets);
            }

            Debug.LogErrorFormat("Could not find {0} {1} or greater.", package.Id, package.Version);
            return false;
        }

        /// <summary>
        ///     Outputs the given message to the log only if verbose mode is active.  Otherwise it does nothing.
        /// </summary>
        /// <param name="format">The formatted message string.</param>
        /// <param name="args">The arguments for the formatted message string.</param>
        public static void LogVerbose(string format, params object[] args)
        {
            if (NugetConfigFile == null || NugetConfigFile.Verbose)
            {
                var stackTraceLogType = Application.GetStackTraceLogType(LogType.Log);
                Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
                Debug.LogFormat(format, args);
                Application.SetStackTraceLogType(LogType.Log, stackTraceLogType);
            }
        }

        /// <summary>
        ///     Installs the given package.
        /// </summary>
        /// <param name="package">The package to install.</param>
        /// <param name="refreshAssets">True to refresh the Unity asset database.  False to ignore the changes (temporarily).</param>
        public static bool Install(NugetPackage package, bool refreshAssets = true)
        {
            if (IsAlreadyImportedInEngine(package))
            {
                LogVerbose("Package {0} is already imported in engine, skipping install.", package);
                return true;
            }

            if (installedPackages.TryGetValue(package.Id, out var installedPackage))
            {
                if (installedPackage < package)
                {
                    LogVerbose(
                        "{0} {1} is installed, but need {2} or greater. Updating to {3}",
                        installedPackage.Id,
                        installedPackage.Version,
                        package.Version,
                        package.Version);
                    return Update(installedPackage, package, false);
                }

                if (installedPackage > package)
                {
                    LogVerbose(
                        "{0} {1} is installed. {2} or greater is needed, so using installed version.",
                        installedPackage.Id,
                        installedPackage.Version,
                        package.Version);
                }
                else
                {
                    LogVerbose("Already installed: {0} {1}", package.Id, package.Version);
                }

                return true;
            }

            var installSuccess = false;
            try
            {
                LogVerbose("Installing: {0} {1}", package.Id, package.Version);

                // look to see if the package (any version) is already installed

                if (refreshAssets)
                {
                    EditorUtility.DisplayProgressBar(
                        string.Format("Installing {0} {1}", package.Id, package.Version),
                        "Installing Dependencies",
                        0.1f);
                }

                // install all dependencies for target framework
                var frameworkGroup = GetBestDependencyFrameworkGroupForCurrentSettings(package);

                LogVerbose("Installing dependencies for TargetFramework: {0}", frameworkGroup.TargetFramework);
                foreach (var dependency in frameworkGroup.Dependencies)
                {
                    LogVerbose("Installing Dependency: {0} {1}", dependency.Id, dependency.Version);
                    var installed = InstallIdentifier(dependency);
                    if (!installed)
                    {
                        throw new Exception(string.Format("Failed to install dependency: {0} {1}.", dependency.Id, dependency.Version));
                    }
                }

                // update packages.config
                PackagesConfigFile.AddPackage(package);
                PackagesConfigFile.Save(PackagesConfigFilePath);

                var cachedPackagePath = Path.Combine(PackOutputDirectory, string.Format("./{0}.{1}.nupkg", package.Id, package.Version));
                if (NugetConfigFile.InstallFromCache && File.Exists(cachedPackagePath))
                {
                    LogVerbose("Cached package found for {0} {1}", package.Id, package.Version);
                }
                else
                {
                    if (package.PackageSource.IsLocalPath)
                    {
                        LogVerbose("Caching local package {0} {1}", package.Id, package.Version);

                        // copy the .nupkg from the local path to the cache
                        File.Copy(
                            Path.Combine(package.PackageSource.ExpandedPath, string.Format("./{0}.{1}.nupkg", package.Id, package.Version)),
                            cachedPackagePath,
                            true);
                    }
                    else
                    {
                        LogVerbose("Downloading package {0} {1}", package.Id, package.Version);

                        if (refreshAssets)
                        {
                            EditorUtility.DisplayProgressBar(
                                string.Format("Installing {0} {1}", package.Id, package.Version),
                                "Downloading Package",
                                0.3f);
                        }

                        var objStream = RequestUrl(package.DownloadUrl, package.PackageSource.UserName, package.PackageSource.ExpandedPassword, null);
                        using (Stream file = File.Create(cachedPackagePath))
                        {
                            CopyStream(objStream, file);
                        }
                    }
                }

                if (refreshAssets)
                {
                    EditorUtility.DisplayProgressBar(string.Format("Installing {0} {1}", package.Id, package.Version), "Extracting Package", 0.6f);
                }

                if (File.Exists(cachedPackagePath))
                {
                    var baseDirectory = Path.Combine(NugetConfigFile.RepositoryPath, string.Format("{0}.{1}", package.Id, package.Version));

                    // unzip the package
                    using (var zip = ZipFile.OpenRead(cachedPackagePath))
                    {
                        foreach (var entry in zip.Entries)
                        {
                            var filePath = Path.Combine(baseDirectory, entry.FullName);
                            var directory = Path.GetDirectoryName(filePath);
                            Directory.CreateDirectory(directory);
                            if (Directory.Exists(filePath))
                            {
                                continue;
                            }

                            entry.ExtractToFile(filePath, true);

                            if (NugetConfigFile.ReadOnlyPackageFiles)
                            {
                                var extractedFile = new FileInfo(filePath);
                                extractedFile.Attributes |= FileAttributes.ReadOnly;
                            }
                        }
                    }

                    // copy the .nupkg inside the Unity project
                    File.Copy(cachedPackagePath, Path.Combine(baseDirectory, string.Format("{0}.{1}.nupkg", package.Id, package.Version)), true);
                }
                else
                {
                    Debug.LogErrorFormat("File not found: {0}", cachedPackagePath);
                }

                if (refreshAssets)
                {
                    EditorUtility.DisplayProgressBar(string.Format("Installing {0} {1}", package.Id, package.Version), "Cleaning Package", 0.9f);
                }

                // clean
                Clean(package);

                // update the installed packages list
                installedPackages.Add(package.Id, package);
                installSuccess = true;
            }
            catch (Exception e)
            {
                WarnIfDotNetAuthenticationIssue(e);
                Debug.LogErrorFormat("Unable to install package {0} {1}\n{2}", package.Id, package.Version, e.ToString());
                installSuccess = false;
            }
            finally
            {
                if (refreshAssets)
                {
                    EditorUtility.DisplayProgressBar(string.Format("Installing {0} {1}", package.Id, package.Version), "Importing Package", 0.95f);
                    AssetDatabase.Refresh();
                    EditorUtility.ClearProgressBar();
                }
            }

            return installSuccess;
        }

        /// <summary>
        ///     Makes a given path relative to the current Unity-Projects directory.
        /// </summary>
        /// <param name="path">The path to make relative.</param>
        /// <returns>The relative path.</returns>
        internal static string GetProjectRelativePath(string path)
        {
            return GetRelativePath(AbsoluteProjectPath, path);
        }

        private static string GetRelativePath(string relativeTo, string path)
        {
            // Path.GetRelativePath is only available in newer .net versions so we need to implement it our self
            if (path == null)
            {
                return null;
            }

            path = Path.GetFullPath(path);
            relativeTo = Path.GetFullPath(relativeTo).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                         Path.DirectorySeparatorChar;
            if (!path.StartsWith(relativeTo, StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }

            return path.Substring(relativeTo.Length);
        }

        private static void WarnIfDotNetAuthenticationIssue(Exception e)
        {
            var webException = e as WebException;
            var webResponse = webException != null ? webException.Response as HttpWebResponse : null;
            if (webResponse != null &&
                webResponse.StatusCode == HttpStatusCode.BadRequest &&
                webException.Message.Contains("Authentication information is not given in the correct format"))
            {
                // This error occurs when downloading a package with authentication using .NET 3.5, but seems to be fixed by the new .NET 4.6 runtime.
                // Inform users when this occurs.
                Debug.LogError(
                    "Authentication failed. This can occur due to a known issue in .NET 3.5. This can be fixed by changing Scripting Runtime to Experimental (.NET 4.6 Equivalent) in Player Settings.");
            }
        }

        /// <summary>
        ///     Get the specified URL from the web. Throws exceptions if the request fails.
        /// </summary>
        /// <param name="url">URL that will be loaded.</param>
        /// <param name="password">Password that will be passed in the Authorization header or the request. If null, authorization is omitted.</param>
        /// <param name="timeOut">Timeout in milliseconds or null to use the default timeout values of HttpWebRequest.</param>
        /// <returns>Stream containing the result.</returns>
        public static Stream RequestUrl(string url, string userName, string password, int? timeOut)
        {
            // Mono doesn't have a Certificate Authority, so we have to provide all validation manually. Currently just accept anything.
            // See here: http://stackoverflow.com/questions/4926676/mono-webrequest-fails-with-https
            ServicePointManager.ServerCertificateValidationCallback = (sender, certificate, chain, policyErrors) => true;

            var getRequest = (HttpWebRequest)WebRequest.Create(url);
            getRequest.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.None;
            if (timeOut.HasValue)
            {
                getRequest.Timeout = timeOut.Value;
                getRequest.ReadWriteTimeout = timeOut.Value;
            }

            if (string.IsNullOrEmpty(password))
            {
                var creds = GetCredentialFromProvider(GetTruncatedFeedUri(getRequest.RequestUri));
                if (creds.HasValue)
                {
                    userName = creds.Value.Username;
                    password = creds.Value.Password;
                }
            }

            if (password != null)
            {
                // Send password as described by https://docs.microsoft.com/en-us/vsts/integrate/get-started/rest/basics.
                // This works with Visual Studio Team Services, but hasn't been tested with other authentication schemes so there may be additional work needed if there
                // are different kinds of authentication.
                getRequest.Headers.Add(
                    "Authorization",
                    "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(string.Format("{0}:{1}", userName, password))));
            }

            LogVerbose("HTTP GET {0}", url);
            var objStream = getRequest.GetResponse().GetResponseStream();
            return objStream;
        }

        /// <summary>
        ///     Restores all packages defined in packages.config.
        /// </summary>
        public static void Restore()
        {
            UpdateInstalledPackages();

            var stopwatch = new Stopwatch();
            stopwatch.Start();

            try
            {
                var progressStep = 1.0f / PackagesConfigFile.Packages.Count;
                float currentProgress = 0;

                // copy the list since the InstallIdentifier operation below changes the actual installed packages list
                var packagesToInstall = new List<NugetPackageIdentifier>(PackagesConfigFile.Packages);

                LogVerbose("Restoring {0} packages.", packagesToInstall.Count);

                foreach (var package in packagesToInstall)
                {
                    if (package != null)
                    {
                        EditorUtility.DisplayProgressBar(
                            "Restoring NuGet Packages",
                            string.Format("Restoring {0} {1}", package.Id, package.Version),
                            currentProgress);

                        if (!IsInstalled(package))
                        {
                            LogVerbose("---Restoring {0} {1}", package.Id, package.Version);
                            InstallIdentifier(package);
                        }
                        else
                        {
                            LogVerbose("---Already installed: {0} {1}", package.Id, package.Version);
                        }
                    }

                    currentProgress += progressStep;
                }

                CheckForUnnecessaryPackages();
            }
            catch (Exception e)
            {
                Debug.LogErrorFormat("{0}", e.ToString());
            }
            finally
            {
                stopwatch.Stop();
                LogVerbose("Restoring packages took {0} ms", stopwatch.ElapsedMilliseconds);

                AssetDatabase.Refresh();
                EditorUtility.ClearProgressBar();
            }
        }

        internal static void CheckForUnnecessaryPackages()
        {
            if (!Directory.Exists(NugetConfigFile.RepositoryPath))
            {
                return;
            }

            var directories = Directory.GetDirectories(NugetConfigFile.RepositoryPath, "*", SearchOption.TopDirectoryOnly);
            foreach (var folder in directories)
            {
                var pkgPath = Path.Combine(folder, $"{Path.GetFileName(folder)}.nupkg");
                var package = NugetPackage.FromNupkgFile(pkgPath);

                var installed = false;
                foreach (var packageId in PackagesConfigFile.Packages)
                {
                    if (packageId.CompareTo(package) == 0)
                    {
                        installed = true;
                        break;
                    }
                }

                if (!installed)
                {
                    LogVerbose("---DELETE unnecessary package {0}", folder);

                    DeleteDirectory(folder);
                    DeleteFile(folder + ".meta");
                }
            }
        }

        /// <summary>
        ///     Checks if a given package is installed.
        /// </summary>
        /// <param name="package">The package to check if is installed.</param>
        /// <returns>True if the given package is installed.  False if it is not.</returns>
        internal static bool IsInstalled(NugetPackageIdentifier package)
        {
            if (IsAlreadyImportedInEngine(package))
            {
                return true;
            }

            var isInstalled = false;
            NugetPackage installedPackage = null;

            if (installedPackages.TryGetValue(package.Id, out installedPackage))
            {
                isInstalled = package.CompareVersion(installedPackage.Version) == 0;
            }

            return isInstalled;
        }

        /// <summary>
        ///     Downloads an image at the given URL and converts it to a Unity Texture2D.
        /// </summary>
        /// <param name="url">The URL of the image to download.</param>
        /// <returns>The image as a Unity Texture2D object.</returns>
        public static Task<Texture2D> DownloadImage(string url)
        {
            var fromCache = false;
            if (ExistsInDiskCache(url))
            {
                url = "file:///" + GetFilePath(url);
                fromCache = true;
            }

            var taskCompletionSource = new TaskCompletionSource<Texture2D>();
            var request = UnityWebRequest.Get(url);
            {
                var downloadHandler = new DownloadHandlerTexture(false);

                request.downloadHandler = downloadHandler;
                request.timeout = 1;
                var operation = request.SendWebRequest();
                operation.completed += asyncOperation =>
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(request.error))
                        {
                            LogVerbose("Downloading image {0} failed! Error: {1}.", url, request.error);
                            taskCompletionSource.TrySetResult(null);
                            return;
                        }

                        var result = downloadHandler.texture;

                        if (result != null && !fromCache)
                        {
                            CacheTextureOnDisk(url, downloadHandler.data);
                        }

                        taskCompletionSource.TrySetResult(result);
                    }
                    finally
                    {
                        request.Dispose();
                    }
                };

                return taskCompletionSource.Task;
            }
        }

        private static void CacheTextureOnDisk(string url, byte[] bytes)
        {
            var diskPath = GetFilePath(url);
            File.WriteAllBytes(diskPath, bytes);
        }

        private static bool ExistsInDiskCache(string url)
        {
            return File.Exists(GetFilePath(url));
        }

        private static string GetFilePath(string url)
        {
            return Path.Combine(Application.temporaryCachePath, GetHash(url));
        }

        private static string GetHash(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return null;
            }

            var md5 = new MD5CryptoServiceProvider();
            var data = md5.ComputeHash(Encoding.Default.GetBytes(s));
            var sBuilder = new StringBuilder();
            for (var i = 0; i < data.Length; i++)
            {
                sBuilder.Append(data[i].ToString("x2"));
            }

            return sBuilder.ToString();
        }

        private static void DownloadCredentialProviders(Uri feedUri)
        {
            foreach (var feed in knownAuthenticatedFeeds)
            {
                var account = feed.GetAccount(feedUri.ToString());
                if (string.IsNullOrEmpty(account))
                {
                    continue;
                }

                var providerUrl = feed.GetProviderUrl(account);

                var credentialProviderRequest = (HttpWebRequest)WebRequest.Create(providerUrl);

                try
                {
                    var credentialProviderDownloadStream = credentialProviderRequest.GetResponse().GetResponseStream();

                    var tempFileName = Path.GetTempFileName();
                    LogVerbose("Writing {0} to {1}", providerUrl, tempFileName);

                    using (var file = File.Create(tempFileName))
                    {
                        CopyStream(credentialProviderDownloadStream, file);
                    }

                    var providerDestination = Environment.GetEnvironmentVariable("NUGET_CREDENTIALPROVIDERS_PATH");
                    if (string.IsNullOrEmpty(providerDestination))
                    {
                        providerDestination = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "Nuget/CredentialProviders");
                    }

                    // Unzip the bundle and extract any credential provider exes
                    using (var zip = ZipFile.OpenRead(tempFileName))
                    {
                        foreach (var entry in zip.Entries)
                        {
                            if (Regex.IsMatch(entry.FullName, @"^credentialprovider.+\.exe$", RegexOptions.IgnoreCase))
                            {
                                LogVerbose("Extracting {0} to {1}", entry.FullName, providerDestination);
                                var filePath = Path.Combine(providerDestination, entry.FullName);
                                var directory = Path.GetDirectoryName(filePath);
                                Directory.CreateDirectory(directory);

                                entry.ExtractToFile(filePath, true);
                            }
                        }
                    }

                    // Delete the bundle
                    File.Delete(tempFileName);
                }
                catch (Exception e)
                {
                    Debug.LogErrorFormat("Failed to download credential provider from {0}: {1}", credentialProviderRequest.Address, e.Message);
                }
            }
        }

        /// <summary>
        ///     Helper function to aquire a token to access VSTS hosted nuget feeds by using the CredentialProvider.VSS.exe
        ///     tool. Downloading it from the VSTS instance if needed.
        ///     See here for more info on nuget Credential Providers:
        ///     https://docs.microsoft.com/en-us/nuget/reference/extensibility/nuget-exe-credential-providers
        /// </summary>
        /// <param name="feedUri">The hostname where the VSTS instance is hosted (such as microsoft.pkgs.visualsudio.com.</param>
        /// <returns>The password in the form of a token, or null if the password could not be aquired</returns>
        private static CredentialProviderResponse? GetCredentialFromProvider(Uri feedUri)
        {
            CredentialProviderResponse? response;
            if (!cachedCredentialsByFeedUri.TryGetValue(feedUri, out response))
            {
                response = GetCredentialFromProvider_Uncached(feedUri, true);
                cachedCredentialsByFeedUri[feedUri] = response;
            }

            return response;
        }

        /// <summary>
        ///     Given the URI of a nuget method, returns the URI of the feed itself without the method and query parameters.
        /// </summary>
        /// <param name="methodUri">URI of nuget method.</param>
        /// <returns>URI of the feed without the method and query parameters.</returns>
        private static Uri GetTruncatedFeedUri(Uri methodUri)
        {
            var truncatedUriString = methodUri.GetLeftPart(UriPartial.Path);

            // Pull off the function if there is one
            if (truncatedUriString.EndsWith(")"))
            {
                var lastSeparatorIndex = truncatedUriString.LastIndexOf('/');
                if (lastSeparatorIndex != -1)
                {
                    truncatedUriString = truncatedUriString.Substring(0, lastSeparatorIndex);
                }
            }

            var truncatedUri = new Uri(truncatedUriString);
            return truncatedUri;
        }

        /// <summary>
        ///     Clears static credentials previously cached by GetCredentialFromProvider.
        /// </summary>
        public static void ClearCachedCredentials()
        {
            cachedCredentialsByFeedUri.Clear();
        }

        /// <summary>
        ///     Internal function called by GetCredentialFromProvider to implement retrieving credentials. For performance reasons,
        ///     most functions should call GetCredentialFromProvider in order to take advantage of cached credentials.
        /// </summary>
        private static CredentialProviderResponse? GetCredentialFromProvider_Uncached(Uri feedUri, bool downloadIfMissing)
        {
            LogVerbose("Getting credential for {0}", feedUri);

            // Build the list of possible locations to find the credential provider. In order it should be local app data, paths set on the
            // environment varaible, and lastly look at the root of the pacakges save location.
            var possibleCredentialProviderPaths = new List<string>
            {
                Path.Combine(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nuget"),
                    "CredentialProviders"),
            };

            var environmentCredentialProviderPaths = Environment.GetEnvironmentVariable("NUGET_CREDENTIALPROVIDERS_PATH");
            if (!string.IsNullOrEmpty(environmentCredentialProviderPaths))
            {
                possibleCredentialProviderPaths.AddRange(
                    environmentCredentialProviderPaths.Split(new[] { ";" }, StringSplitOptions.RemoveEmptyEntries) ?? Enumerable.Empty<string>());
            }

            // Try to find any nuget.exe in the package tools installation location
            var toolsPackagesFolder = Path.Combine(Application.dataPath, "../Packages");
            possibleCredentialProviderPaths.Add(toolsPackagesFolder);

            // Search through all possible paths to find the credential provider.
            var providerPaths = new List<string>();
            foreach (var possiblePath in possibleCredentialProviderPaths)
            {
                if (Directory.Exists(possiblePath))
                {
                    providerPaths.AddRange(Directory.GetFiles(possiblePath, "credentialprovider*.exe", SearchOption.AllDirectories));
                }
            }

            foreach (var providerPath in providerPaths.Distinct())
            {
                // Launch the credential provider executable and get the json encoded response from the std output
                var process = new Process();
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.FileName = providerPath;
                process.StartInfo.Arguments = string.Format("-uri \"{0}\"", feedUri);

                // http://stackoverflow.com/questions/16803748/how-to-decode-cmd-output-correctly
                // Default = 65533, ASCII = ?, Unicode = nothing works at all, UTF-8 = 65533, UTF-7 = 242 = WORKS!, UTF-32 = nothing works at all
                process.StartInfo.StandardOutputEncoding = Encoding.GetEncoding(850);
                process.Start();
                process.WaitForExit();

                var output = process.StandardOutput.ReadToEnd();
                var errors = process.StandardError.ReadToEnd();

                switch ((CredentialProviderExitCode)process.ExitCode)
                {
                    case CredentialProviderExitCode.ProviderNotApplicable:
                        break; // Not the right provider
                    case CredentialProviderExitCode.Failure: // Right provider, failure to get creds
                        {
                            Debug.LogErrorFormat(
                                "Failed to get credentials from {0}!\n\tOutput\n\t{1}\n\tErrors\n\t{2}",
                                providerPath,
                                output,
                                errors);
                            return null;
                        }
                    case CredentialProviderExitCode.Success:
                        {
                            return JsonUtility.FromJson<CredentialProviderResponse>(output);
                        }
                    default:
                        {
                            Debug.LogWarningFormat(
                                "Unrecognized exit code {0} from {1} {2}",
                                process.ExitCode,
                                providerPath,
                                process.StartInfo.Arguments);
                            break;
                        }
                }
            }

            if (downloadIfMissing)
            {
                DownloadCredentialProviders(feedUri);
                return GetCredentialFromProvider_Uncached(feedUri, false);
            }

            return null;
        }

        private struct UnityVersion : IComparable<UnityVersion>
        {
            public readonly int Major;

            public readonly int Minor;

            public readonly int Revision;

            public readonly char Release;

            public readonly int Build;

            public static readonly UnityVersion Current = new UnityVersion(Application.unityVersion);

            public UnityVersion(string version)
            {
                var match = Regex.Match(version, @"(\d+)\.(\d+)\.(\d+)([fpba])(\d+)");
                if (!match.Success)
                {
                    throw new ArgumentException("Invalid unity version");
                }

                Major = int.Parse(match.Groups[1].Value);
                Minor = int.Parse(match.Groups[2].Value);
                Revision = int.Parse(match.Groups[3].Value);
                Release = match.Groups[4].Value[0];
                Build = int.Parse(match.Groups[5].Value);
            }

            public UnityVersion(int major, int minor, int revision, char release, int build)
            {
                Major = major;
                Minor = minor;
                Revision = revision;
                Release = release;
                Build = build;
            }

            public static int Compare(UnityVersion a, UnityVersion b)
            {
                if (a.Major < b.Major)
                {
                    return -1;
                }

                if (a.Major > b.Major)
                {
                    return 1;
                }

                if (a.Minor < b.Minor)
                {
                    return -1;
                }

                if (a.Minor > b.Minor)
                {
                    return 1;
                }

                if (a.Revision < b.Revision)
                {
                    return -1;
                }

                if (a.Revision > b.Revision)
                {
                    return 1;
                }

                if (a.Release < b.Release)
                {
                    return -1;
                }

                if (a.Release > b.Release)
                {
                    return 1;
                }

                if (a.Build < b.Build)
                {
                    return -1;
                }

                if (a.Build > b.Build)
                {
                    return 1;
                }

                return 0;
            }

            public int CompareTo(UnityVersion other)
            {
                return Compare(this, other);
            }

            public static bool operator <(UnityVersion left, UnityVersion right)
            {
                return left.CompareTo(right) < 0;
            }

            public static bool operator <=(UnityVersion left, UnityVersion right)
            {
                return left.CompareTo(right) <= 0;
            }

            public static bool operator >(UnityVersion left, UnityVersion right)
            {
                return left.CompareTo(right) > 0;
            }

            public static bool operator >=(UnityVersion left, UnityVersion right)
            {
                return left.CompareTo(right) >= 0;
            }
        }

        private struct PriorityFramework
        {
            public int Priority;

            public string Framework;
        }

        private struct AuthenticatedFeed
        {
            public string AccountUrlPattern;

            public string ProviderUrlTemplate;

            public string GetAccount(string url)
            {
                var match = Regex.Match(url, AccountUrlPattern, RegexOptions.IgnoreCase);
                if (!match.Success)
                {
                    return null;
                }

                return match.Groups["account"].Value;
            }

            public string GetProviderUrl(string account)
            {
                return ProviderUrlTemplate.Replace("{account}", account);
            }
        }

        /// <summary>
        ///     Data class returned from nuget credential providers in a JSON format. As described here:
        ///     https://docs.microsoft.com/en-us/nuget/reference/extensibility/nuget-exe-credential-providers#creating-a-nugetexe-credential-provider
        /// </summary>
        [Serializable]
        private struct CredentialProviderResponse
        {
            public string Username;

            public string Password;
        }

        /// <summary>
        ///     Possible response codes returned by a Nuget credential provider as described here:
        ///     https://docs.microsoft.com/en-us/nuget/reference/extensibility/nuget-exe-credential-providers#creating-a-nugetexe-credential-provider
        /// </summary>
        private enum CredentialProviderExitCode
        {
            Success = 0,

            ProviderNotApplicable = 1,

            Failure = 2,
        }
    }
}
