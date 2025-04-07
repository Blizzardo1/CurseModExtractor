using System.Diagnostics;
using System.IO.Compression;
using System.Text.Encodings.Web;
using System.Web;
using Newtonsoft.Json;
using NLog;
using NLog.Conditions;
using NLog.Targets;

using Flurl.Http;

namespace CurseModExtractor {
    public static class ModLoader {
        private static List<string>? _missingMods = [];

        private static readonly Logger? _log = LogManager.GetCurrentClassLogger();

        private static ColoredConsoleTarget SetupColoredConsole() {
            ColoredConsoleTarget cct = new();
            var infoHighlight = new ConsoleRowHighlightingRule(ConditionParser.ParseExpression("level == LogLevel.Info"),
                ConsoleOutputColor.Green, ConsoleOutputColor.NoChange);
            var warnHighlight = new ConsoleRowHighlightingRule(ConditionParser.ParseExpression("level == LogLevel.Warn"),
                ConsoleOutputColor.Yellow, ConsoleOutputColor.NoChange);
            var errorHighlight = new ConsoleRowHighlightingRule(ConditionParser.ParseExpression("level == LogLevel.Error"),
                ConsoleOutputColor.Red, ConsoleOutputColor.NoChange);
            var fatalHighlight = new ConsoleRowHighlightingRule(ConditionParser.ParseExpression("level == LogLevel.Fatal"),
                ConsoleOutputColor.Magenta, ConsoleOutputColor.NoChange);
            cct.RowHighlightingRules.Add(infoHighlight);
            cct.RowHighlightingRules.Add(warnHighlight);
            cct.RowHighlightingRules.Add(errorHighlight);
            cct.RowHighlightingRules.Add(fatalHighlight);
            cct.Layout = @"[${date:format=HH\:mm\:ss}] (${level:uppercase=true}) >> ${logger} -> ${message}";
            cct.UseDefaultRowHighlightingRules = false;
            cct.WordHighlightingRules.Add(new ConsoleWordHighlightingRule("TestGame", ConsoleOutputColor.Cyan,
                ConsoleOutputColor.NoChange));
            return cct;
        }

        static ModLoader() {
            LogManager.Setup().LoadConfiguration(builder => {
                builder.ForLogger().FilterMinLevel(LogLevel.Debug).WriteTo(SetupColoredConsole());
                // builder.ForLogger().FilterMinLevel(LogLevel.Info).WriteToFile(fileName: "output.log");
            });
        }

        public static async Task Extract(string filename, bool skipExtract = false) {
            _log?.Info($"Modpack filename is {filename}.");
            var zipFile = new FileInfo(filename);
            DirectoryInfo unzippedDir = ExtractModPackMetadata(zipFile, skipExtract);
            Manifest? manifest = await GetManifest(unzippedDir);
            DirectoryInfo outputDir = GetOutputDir(filename);

            DirectoryInfo minecraftDir = Directory.CreateDirectory(Path.Combine(outputDir.FullName, "minecraft"));

            if (manifest is null) {
                _log?.Fatal("No Manifest!");
                return;
            }
            
            if(!await DownloadModPackFromManifest(minecraftDir, manifest)) {
                _log?.Fatal("There was an error obtaining a file list from the manifest!");
                return;
            }
            
            CopyOverrides(manifest, unzippedDir, minecraftDir);
            SetupMultiMcInfo(manifest, outputDir);
            EndSetup(outputDir, manifest);
        }

        private static DirectoryInfo ExtractModPackMetadata(FileInfo zipFile, bool skipExtract) {
            string extractPath = zipFile.DirectoryName!;
            if (skipExtract) return new DirectoryInfo(extractPath);

            _log?.Info("Unzipping Modpack Download");
            ZipFile.ExtractToDirectory(zipFile.FullName, extractPath, true);
            _log?.Info("Done unzipping");

            return new DirectoryInfo(extractPath);
        }

        private static async Task<Manifest?> GetManifest(DirectoryInfo dir) {
            _log?.Info("Parsing Manifest");
            string manifestPath = Path.Combine(dir.FullName, "manifest.json");
            if (!File.Exists(manifestPath))
                throw new InvalidOperationException("Manifest not found");

            string json = await File.ReadAllTextAsync(manifestPath);
            var manifest = JsonConvert.DeserializeObject< Manifest >(json);
            
            _log?.Info($"Required Minecraft Version: {manifest?.Minecraft?.Version}");

            string[] forgeVer = manifest?.GetForgeVersion()!;

            bool allGood = forgeVer is not [] && forgeVer.First() != "N/A";
            
            if(allGood && forgeVer!.Length > 1) {
                _log?.Warn("Found multiple forge versions! Please ensure the version you wish to use is loaded into MultiMC.");
                _log?.Warn($"{string.Join(',', forgeVer)}");
            }
            _log?.Warn($"Do we have a Forge Version? {(allGood ? $"Yes, it's {forgeVer!.First()}" : "No")}");
            if (allGood) return manifest!;
            
            _log?.Fatal($"EY, NO FORGE VERSION!? MODLOADER IS RETURNING NULL!?");
            return null;
        }

        private static DirectoryInfo GetOutputDir(string filename) {
            string path = AppContext.BaseDirectory;
            string outName = Path.GetFileNameWithoutExtension(Uri.UnescapeDataString(filename));
            string fullPath = Path.Combine(path, outName);
            Directory.CreateDirectory(fullPath);
            _log?.Info("Output Dir is " + fullPath);
            return new DirectoryInfo(fullPath);
        }

        private static async Task<bool> DownloadModPackFromManifest(DirectoryInfo outputDir, Manifest manifest) {
            if (manifest.Files == null) {
                _log?.Fatal("Error, Files manifest is null!");
                return false;
            }
            int total = manifest.Files.Count;

            _log?.Info($"Downloading modpack from Manifest");
            _log?.Info($"Manifest contains {total} files to download\n");

            DirectoryInfo modsDir = new(outputDir.FullName + "/mods");
            if (!modsDir.Exists)
                modsDir.Create();

            int left = total;
            foreach (Manifest.FileData file in manifest.Files) {
                left--;
                await DownloadFile(file, modsDir, left, total);
            }

            _log?.Info("Mod downloads complete");
            return true;
        }

        private static void CopyOverrides(Manifest manifest, DirectoryInfo tempDir, DirectoryInfo outDir) {
            _log?.Info("Copying modpack overrides");

            if (manifest.Overrides != null) {
                DirectoryInfo overridesDir = new(Path.Combine(tempDir.FullName, manifest.Overrides));

                foreach (string path in Directory.EnumerateFiles(overridesDir.FullName, "*", SearchOption.AllDirectories)) {
                    string relative = Path.GetRelativePath(overridesDir.FullName, path);
                    string target = Path.Combine(outDir.FullName, relative);

                    _log?.Info($"Override: {Path.GetFileName(path)}");

                    try {
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Copy(path, target, overwrite: false);
                    } catch (IOException ex) when (ex is not FileNotFoundException) {
                        _log?.Error($"Error copying {Path.GetFileName(path)}: {ex.Message}, {ex.GetType()}");
                    }
                }
            }

            _log?.Info("Done copying overrides");
        }

        private static void SetupMultiMcInfo(Manifest manifest, DirectoryInfo outputDir) {
            _log?.Info("Setting up MultiMC info");

            FileInfo cfg = new(Path.Combine(outputDir.FullName, "instance.cfg"));

            using var writer = new StreamWriter(cfg.FullName, false);

            writer.WriteLine("InstanceType=OneSix");
            writer.WriteLine($"IntendedVersion={manifest.Minecraft?.Version}");
            writer.WriteLine("LogPrePostOutput=true");
            writer.WriteLine("OverrideCommands=false");
            writer.WriteLine("OverrideConsole=false");
            writer.WriteLine("OverrideJavaArgs=false");
            writer.WriteLine("OverrideJavaLocation=false");
            writer.WriteLine("OverrideMemory=false");
            writer.WriteLine("OverrideWindow=false");
            writer.WriteLine("iconKey=default");
            writer.WriteLine("lastLaunchTime=0");
            writer.WriteLine($"name={manifest.Name} {manifest.Version}");
            writer.WriteLine($"notes=Modpack by {manifest.Author}. Generated by Blizzardo1. Using Forge {manifest.GetForgeVersion()}.");
            writer.WriteLine("totalTimePlayed=0");
        }

        private static void EndSetup(DirectoryInfo outputDir, Manifest manifest) {
            _log?.Info("And we're done!");
            
            if(outputDir.Parent is null) {
                _log?.Fatal($"No Output Directory specified! No Parent directory for {outputDir.FullName}");
                return;
            }
            _log?.Info($"Output Path: {outputDir.FullName}");
            
            
            
            ZipFile.CreateFromDirectory(Path.Combine(outputDir.FullName, ".")!,
                Path.Combine(outputDir.Parent.FullName, $"{outputDir.Name} MultiMC.zip"),
                CompressionLevel.Optimal, 
                false);

            _log?.Info("################################################################################################");

            _log?.Warn("IMPORTANT NOTE: If you want to import this instance to MultiMC, you must install Forge manually");
            _log?.Warn($"The Forge version you need is {manifest.GetForgeVersion()!.First()}");
            _log?.Warn("A later version will probably also work just as fine, but this is the version shipped with the pack");
            _log?.Warn("This is also added to the instance notes");
            
            if (_missingMods is not null && _missingMods.Count > 0) {
                _log?.Warn("WARNING: Some mods could not be downloaded. Either the specific versions were taken down from CurseForge, or there were errors in the download.");
                _log?.Warn("The missing mods are the following:");
                foreach (string mod in _missingMods)
                    _log?.Info($" - {mod}");
                _log?.Warn("If these mods are crucial to the modpack functioning, try downloading the server version of the pack and pulling them from there.");
            }

            _missingMods = null;

            _log?.Info("################################################################################################");
            _log?.Info("Complete");

            Directory.Delete("overrides", true );
            File.Delete("modlist.html");
            File.Delete("manifest.json");

            // Open output directory in file explorer
            Process.Start(new ProcessStartInfo {
                FileName = outputDir.FullName,
                UseShellExecute = true
            });
        }

        private static async Task DownloadFile(Manifest.FileData fileData, DirectoryInfo targetDir, int left, int total) {
            // https://www.curseforge.com/api/v1/mods/855414/files/6371701/download
            string url = $"https://www.curseforge.com/api/v1/mods/{fileData.ProjectId}/files/{fileData.FileId}/download";
            string fileName = $"{fileData.ProjectId}-{fileData.FileId}.jar";

            try {
                IFlurlRequest? flurl = url.WithTimeout(3)
                    .WithHeader("User-Agent", "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Ubuntu Chromium/53.0.2785.143 Chrome/53.0.2785.143 Safari/537.36");

                IFlurlResponse? response = await flurl.GetAsync();

                LogLevel level = response.StatusCode switch {
                    404 => LogLevel.Error,
                    403 => LogLevel.Fatal,
                    401 => LogLevel.Error,
                    200 => LogLevel.Info,
                    _ => LogLevel.Info
                };

                if (response.ResponseMessage.RequestMessage?.RequestUri != null)
                    fileName = Path.GetFileName(HttpUtility.UrlDecode(response.ResponseMessage.RequestMessage.RequestUri.AbsolutePath));

                _log?.Log(level,
                    $"[Status {response.StatusCode}] -> {response.ResponseMessage.ReasonPhrase ?? "No reason"} -- [{total - left} - {total}] Downloading {fileName}");
                var ms = (MemoryStream) await response.GetStreamAsync();
                
                if(response.Headers.TryGetFirst("Content-Disposition", out string data)) {
                    _log?.Warn($"Content Disposition: {data}");
                }
                
                await using var fs = new FileStream(Path.Combine(targetDir.FullName, fileName), FileMode.Create, FileAccess.Write);
                await response.ResponseMessage.Content.CopyToAsync(fs);
            } catch (FlurlHttpException ex) {
                _missingMods ??= [];
                _missingMods.Add(fileName);
                _log?.Error($"Failed to download {fileName}: {ex.Message}");
            }
        }

    }
}
