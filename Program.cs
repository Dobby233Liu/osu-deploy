// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Management.Automation;
using System.Net;
using System.Net.Http;
using System.Threading;
using Newtonsoft.Json;
using osu.Framework;
using osu.Framework.IO.Network;
using FileWebRequest = osu.Framework.IO.Network.FileWebRequest;
using WebRequest = osu.Framework.IO.Network.WebRequest;

namespace osu.Desktop.Deploy
{
    internal static class Program
    {
        private static string packages => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        private static string nugetPath => Path.Combine(packages, @"nuget.commandline\4.7.1\tools\NuGet.exe");

        private const string staging_folder = "staging";
        private const string releases_folder = "releases";

        /// <summary>
        /// How many previous build deltas we want to keep when publishing.
        /// </summary>
        private const int keep_delta_count = 4;

        public static string GitHubAccessToken = ConfigurationManager.AppSettings["GitHubAccessToken"];
        public static bool GitHubUpload = false;
        public static string GitHubUsername = ConfigurationManager.AppSettings["GitHubUsername"];
        public static string GitHubRepoName = ConfigurationManager.AppSettings["GitHubRepoName"];
        public static string SolutionName = ConfigurationManager.AppSettings["SolutionName"];
        public static string ProjectName = ConfigurationManager.AppSettings["ProjectName"];
        public static string NuSpecName = ConfigurationManager.AppSettings["NuSpecName"];
        public static bool IncrementVersion = bool.Parse(ConfigurationManager.AppSettings["IncrementVersion"] ?? "true");
        public static string PackageName = ConfigurationManager.AppSettings["PackageName"];
        public static string IconName = ConfigurationManager.AppSettings["IconName"];
        public static string CodeSigningCertificate = ConfigurationManager.AppSettings["CodeSigningCertificate"];

        public static string GitHubApiEndpoint => $"https://api.github.com/repos/{GitHubUsername}/{GitHubRepoName}/releases";

        private static string solutionPath;

        private static string stagingPath => Path.Combine(Environment.CurrentDirectory, staging_folder);
        private static string releasesPath => Path.Combine(Environment.CurrentDirectory, releases_folder);
        private static string iconPath => Path.Combine(solutionPath, ProjectName, IconName);

        private static readonly Stopwatch stopwatch = new Stopwatch();

        private static bool interactive;

        public static void Main(string[] args)
        {
            interactive = args.Length == 0;
            displayHeader();

            findSolutionPath();

            if (!Directory.Exists(releases_folder))
            {
                write("WARNING: No release directory found. Make sure you want this!", ConsoleColor.Yellow);
                Directory.CreateDirectory(releases_folder);
            }

            //increment build number until we have a unique one.
            string verBase = DateTime.Now.ToString("yyyy.Mdd.");
            int increment = 0;

            if (lastRelease?.TagName.StartsWith(verBase) ?? false)
                increment = int.Parse(lastRelease.TagName.Split('.')[2]) + (IncrementVersion ? 1 : 0);

            string version = $"{verBase}{increment}";

            if (args.Length > 1 && !string.IsNullOrEmpty(args[1]))
                version = args[1];

            Console.ResetColor();
            Console.WriteLine($"Increment Version:     {IncrementVersion}");
            Console.WriteLine($"Signing Certificate:   {CodeSigningCertificate}");
            Console.WriteLine();
            Console.Write($"Ready to deploy {version}!");

            pauseIfInteractive();

            stopwatch.Start();

            refreshDirectory(staging_folder);
            updateAppveyorVersion(version);

            write("Running build process...");

            switch (RuntimeInfo.OS)
            {
                case RuntimeInfo.Platform.Windows:
                    runCommand("dotnet", $"publish -f netcoreapp3.1 -r win-x64 {ProjectName} -o {stagingPath} --configuration Release /p:Version={version}");

                    // change subsystem of dotnet stub to WINDOWS (defaults to console; no way to change this yet https://github.com/dotnet/core-setup/issues/196)
                    runCommand("tools/editbin.exe", $"/SUBSYSTEM:WINDOWS {stagingPath}\\osu!.exe");

                    // add icon to dotnet stub
                    runCommand("tools/rcedit-x64.exe", $"\"{stagingPath}\\osu!.exe\" --set-icon \"{iconPath}\"");

                    write("Creating NuGet deployment package...");
                    runCommand(nugetPath, $"pack {NuSpecName} -Version {version} -Properties Configuration=Deploy -OutputDirectory {stagingPath} -BasePath {stagingPath}");

                    // prune once before checking for files so we can avoid erroring on files which aren't even needed for this build.
                    pruneReleases();

                    checkReleaseFiles();

                    // prune again to clean up before upload.
                    pruneReleases();

                    write("bins at releases_dir");
                    break;
                case RuntimeInfo.Platform.MacOsx:

                    // unzip the template app, with all structure existing except for dotnet published content.
                    runCommand("unzip", $"\"osu!.app-template.zip\" -d {stagingPath}", false);

                    runCommand("dotnet", $"publish -r osx-x64 {ProjectName} --configuration Release -o {stagingPath}/osu!.app/Contents/MacOS /p:Version={version}");

                    string stagingApp = $"{stagingPath}/osu!.app";
                    string zippedApp = $"{releasesPath}/osu!.app.zip";

                    // correct permissions post-build. dotnet outputs 644 by default; we want 755.
                    runCommand("chmod", $"-R 755 {stagingApp}");

                    // sign using apple codesign
                    runCommand("codesign", $"--deep --force --verify --entitlements {Path.Combine(Environment.CurrentDirectory, "osu.entitlements")} -o runtime --verbose --sign \"{CodeSigningCertificate}\" {stagingApp}");

                    // check codesign was successful
                    runCommand("spctl", $"--assess -vvvv {stagingApp}");

                    // package for distribution
                    runCommand("ditto", $"-ck --rsrc --keepParent --sequesterRsrc {stagingApp} {zippedApp}");

                    // upload for notarisation
                    runCommand("xcrun", $"altool --notarize-app --primary-bundle-id \"sh.ppy.osu.lazer\" --username \"{ConfigurationManager.AppSettings["AppleUsername"]}\" --password \"{ConfigurationManager.AppSettings["ApplePassword"]}\" --file {zippedApp}");

                    // TODO: make this actually wait properly
                    write("Waiting for notarisation to complete..");
                    Thread.Sleep(60000 * 5);

                    // staple notarisation result
                    runCommand("xcrun", $"stapler staple {stagingApp}");

                    File.Delete(zippedApp);

                    // repackage for distribution
                    runCommand("ditto", $"-ck --rsrc --keepParent --sequesterRsrc {stagingApp} {zippedApp}");

                    break;

                case RuntimeInfo.Platform.Linux:
                    // avoid use of unzip on Linux system, it is not preinstalled by default
                    ZipFile.ExtractToDirectory("osu!.AppDir-template.zip", $"{stagingPath}");

                    // mark AppRun as executable, as zip does not contains executable information
                    runCommand("chmod", $"+x {stagingPath}/osu!.AppDir/AppRun");

                    runCommand("dotnet", $"publish -f netcoreapp3.1 -r linux-x64 {ProjectName} -o {stagingPath}/osu!.AppDir/usr/bin/ --configuration Release /p:Version={version} --self-contained");

                    // mark output as executable
                    runCommand("chmod", $"+x {stagingPath}/osu!.AppDir/usr/bin/osu!");

                    // copy png icon (for desktop file)
                    File.Copy(Path.Combine(solutionPath, "assets/lazer.png"), $"{stagingPath}/osu!.AppDir/osu!.png");

                    // download appimagetool
                    using (var client = new WebClient())
                        client.DownloadFile("https://github.com/AppImage/AppImageKit/releases/download/continuous/appimagetool-x86_64.AppImage", $"{stagingPath}/appimagetool.AppImage");

                    // mark appimagetool as executable
                    runCommand("chmod", $"a+x {stagingPath}/appimagetool.AppImage");

                    // create AppImage itself
                    // gh-releases-zsync stands here for GitHub Releases ZSync, that is a way to check for updates
                    // ppy|osu|latest stands for https://github.com/ppy/osu and get the latest release
                    // osu.AppImage.zsync is AppImage update information file, that is generated by the tool
                    // more information there https://docs.appimage.org/packaging-guide/optional/updates.html?highlight=update#using-appimagetool
                    runCommand($"{stagingPath}/appimagetool.AppImage", $"\"{stagingPath}/osu!.AppDir\" -u \"gh-releases-zsync|ppy|osu|latest|osu.AppImage.zsync\" \"{Path.Combine(Environment.CurrentDirectory, "releases")}/osu.AppImage\" --sign", false);

                    // mark finally the osu! AppImage as executable -> Don't compress it.
                    runCommand("chmod", $"+x \"{Path.Combine(Environment.CurrentDirectory, "releases")}/osu.AppImage\"");

                    // copy update information
                    File.Move(Path.Combine(Environment.CurrentDirectory, "osu.AppImage.zsync"), $"{releases_folder}/osu.AppImage.zsync", true);

                    break;
            }

            write("Done!");
            pauseIfInteractive();
        }

        private static void displayHeader()
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine();
            Console.WriteLine("  Please note that osu! and ppy are registered trademarks and as such covered by trademark law.");
            Console.WriteLine("  Do not distribute builds of this project publicly that make use of these.");
            Console.ResetColor();
            Console.WriteLine();
        }

        /// <summary>
        /// Ensure we have all the files in the release directory which are expected to be there.
        /// This should have been accounted for in earlier steps, and just serves as a verification step.
        /// </summary>
        private static void checkReleaseFiles()
        {
            if (!canGitHub) return;

            var releaseLines = getReleaseLines();

            //ensure we have all files necessary
            foreach (var l in releaseLines)
                if (!File.Exists(Path.Combine(releases_folder, l.Filename)))
                    error($"Local file missing {l.Filename}");
        }

        private static IEnumerable<ReleaseLine> getReleaseLines() => File.ReadAllLines(Path.Combine(releases_folder, "RELEASES")).Select(l => new ReleaseLine(l));

        private static void pruneReleases()
        {
        }

        private static void uploadBuild(string version)
        {
        }

        private static void openGitHubReleasePage(){}

        private static bool canGitHub => false;

        /// <summary>
        /// Download assets from a previous release into the releases folder.
        /// </summary>
        /// <param name="release"></param>
        private static void getAssetsFromRelease(GitHubRelease release)
        {
        }

        private static void refreshDirectory(string directory)
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
            Directory.CreateDirectory(directory);
        }

        /// <summary>
        /// Find the base path of the active solution (git checkout location)
        /// </summary>
        private static void findSolutionPath()
        {
            string path = Path.GetDirectoryName(Environment.CommandLine.Replace("\"", "").Trim());

            if (string.IsNullOrEmpty(path))
                path = Environment.CurrentDirectory;

            while (true)
            {
                if (File.Exists(Path.Combine(path, $"{SolutionName}.sln")))
                    break;

                if (Directory.Exists(Path.Combine(path, "osu")) && File.Exists(Path.Combine(path, "osu", $"{SolutionName}.sln")))
                {
                    path = Path.Combine(path, "osu");
                    break;
                }

                path = path.Remove(path.LastIndexOf(Path.DirectorySeparatorChar));
            }

            path += Path.DirectorySeparatorChar;

            solutionPath = path;
        }

        private static bool runCommand(string command, string args, bool useSolutionPath = true)
        {
            write($"Running {command} {args}...");

            var psi = new ProcessStartInfo(command, args)
            {
                WorkingDirectory = useSolutionPath ? solutionPath : Environment.CurrentDirectory,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            Process p = Process.Start(psi);
            if (p == null) return false;

            string output = p.StandardOutput.ReadToEnd();
            output += p.StandardError.ReadToEnd();

            p.WaitForExit();

            if (p.ExitCode == 0) return true;

            write(output);
            error($"Command {command} {args} failed!");
            return false;
        }

        private static string readLineMasked()
        {
            var fg = Console.ForegroundColor;
            Console.ForegroundColor = Console.BackgroundColor;
            var ret = Console.ReadLine();
            Console.ForegroundColor = fg;

            return ret;
        }

        private static void error(string message)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"FATAL ERROR: {message}");

            pauseIfInteractive();
            Environment.Exit(-1);
        }

        private static void pauseIfInteractive()
        {
            if (interactive)
                Console.ReadLine();
            else
                Console.WriteLine();
        }

        private static bool updateAppveyorVersion(string version)
        {
            return false;
        }

        private static void write(string message, ConsoleColor col = ConsoleColor.Gray)
        {
            if (stopwatch.ElapsedMilliseconds > 0)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write(stopwatch.ElapsedMilliseconds.ToString().PadRight(8));
            }

            Console.ForegroundColor = col;
            Console.WriteLine(message);
        }

        public static void AuthenticatedBlockingPerform(this WebRequest r)
        {
            r.AddHeader("Authorization", $"token {GitHubAccessToken}");
            r.Perform();
        }
    }

    internal class RawFileWebRequest : WebRequest
    {
        public RawFileWebRequest(string url)
            : base(url)
        {
        }

        protected override string Accept => "application/octet-stream";
    }

    internal class ReleaseLine
    {
        public string Hash;
        public string Filename;
        public int Filesize;

        public ReleaseLine(string line)
        {
            var split = line.Split(' ');
            Hash = split[0];
            Filename = split[1];
            Filesize = int.Parse(split[2]);
        }

        public override string ToString() => $"{Hash} {Filename} {Filesize}";
    }
}
