using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using DiscUtils.Iso9660;

enum BootImageType
{
    Unknown = 0,
    BiosBootSector512 = 1,
    EfiImage = 2
}

enum PluginTrustMode
{
    Disabled = 0,
    ConfirmEach = 1,
    AllowListed = 2
}

enum BootPriorityMode
{
    Auto = 0,
    PreferUefi = 1,
    PreferBios = 2
}

interface IBuildPlugin
{
    string Name { get; }
    void OnBeforeBuild(BuildContext ctx);
    void OnAfterBuild(BuildContext ctx);
    void OnStepExecuting(BuildContext ctx, IBuildStep step);
    void OnStepExecuted(BuildContext ctx, IBuildStep step);
}

interface IBuildStep
{
    string Name { get; }
    void Execute(BuildContext ctx);
}

sealed class IsoBuilderOptions
{
    public string ProjectDir { get; init; } = string.Empty;
    public string BootDir { get; init; } = string.Empty;
    public string IsoDir { get; init; } = string.Empty;
    public string Output { get; init; } = string.Empty;
    public string VolumeId { get; init; } = string.Empty;
    public bool Uefi { get; init; }
    public bool SecureBoot { get; init; }
    public bool Silent { get; init; }
    public bool NoPrompt { get; init; }
    public bool NoSpinner { get; init; }
    public bool NoProgress { get; init; }
    public bool BiosOnly { get; init; }
    public bool UefiOnly { get; init; }
    public bool Init { get; init; }
    public bool Version { get; init; }
    public bool Help { get; init; }
    public bool Verbose { get; init; }
    public bool PluginsOff { get; init; }
    public bool DryRun { get; init; }
    public bool Manifest { get; init; }
    public string BootPriority { get; init; } = "auto";
    public long MaxSizeMb { get; init; }
    public int MaxFiles { get; init; }
    public int TimeoutSeconds { get; init; }
    public string JsonReportPath { get; init; } = string.Empty;
    public string ConfigPath { get; init; } = string.Empty;
    public IReadOnlyList<string> PluginAllowList { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ExcludePatterns { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> PluginHashPins { get; init; } = new Dictionary<string, string>();

    public static IsoBuilderOptions Parse(string[] args)
    {
        string project = string.Empty, output = string.Empty, bootDir = string.Empty,
               isoDir = string.Empty, volumeId = string.Empty, bootPriority = "auto",
               jsonReportPath = string.Empty, configPath = string.Empty;
        bool uefi = false, secureBoot = false, silent = false, noPrompt = false,
             noSpinner = false, noProgress = false, biosOnly = false, uefiOnly = false,
             init = false, version = false, help = false, verbose = false, pluginsOff = false,
             dryRun = false, manifest = false;
        long maxSizeMb = 0;
        int maxFiles = 0, timeoutSeconds = 0;
        var allowList = new List<string>();
        var excludeList = new List<string>();
        var pluginHashPins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string raw = args[i]?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(raw)) continue;
            int eq = raw.IndexOf('=');
            string key = eq > 0 ? raw[..eq] : raw;
            string emb = eq > 0 ? raw[(eq + 1)..].Trim('"') : string.Empty;

            string Next()
            {
                if (!string.IsNullOrEmpty(emb)) return emb;
                if (i + 1 < args.Length) { i++; return (args[i] ?? string.Empty).Trim().Trim('"'); }
                errors.Add($"Missing value for argument: {key}");
                return string.Empty;
            }

            switch (key)
            {
                case "init": init = true; break;
                case "version": case "--version": version = true; break;
                case "help": case "-h": case "--help": help = true; break;
                case "--uefi": uefi = true; break;
                case "--secureboot": secureBoot = true; break;
                case "--silent": silent = true; break;
                case "--no-prompt": noPrompt = true; break;
                case "--no-spinner": noSpinner = true; break;
                case "--no-progress": noProgress = true; break;
                case "--bios-only": biosOnly = true; break;
                case "--uefi-only": uefiOnly = true; uefi = true; break;
                case "--verbose": verbose = true; break;
                case "--plugins-off": pluginsOff = true; break;
                case "--dry-run": dryRun = true; break;
                case "--manifest": manifest = true; break;
                case "-Pu":
                case "--plugin":
                    string pn = Next();
                    if (!string.IsNullOrWhiteSpace(pn)) allowList.Add(pn.Trim());
                    break;
                case "--plugin-hash":
                    string phRaw = Next();
                    int sep = phRaw.IndexOf('=');
                    if (sep > 0 && sep < phRaw.Length - 1)
                        pluginHashPins[phRaw[..sep].Trim()] = phRaw[(sep + 1)..].Trim().ToLowerInvariant();
                    else if (!string.IsNullOrEmpty(phRaw))
                        errors.Add($"Invalid value for --plugin-hash, expected Name=sha256: {phRaw}");
                    break;
                case "--exclude":
                    string exGlob = Next();
                    if (!string.IsNullOrWhiteSpace(exGlob)) excludeList.Add(exGlob.Trim());
                    break;
                case "--max-size":
                    string msRaw = Next();
                    if (long.TryParse(msRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long msVal) && msVal > 0) maxSizeMb = msVal;
                    else if (!string.IsNullOrEmpty(msRaw)) errors.Add($"Invalid value for --max-size: {msRaw}");
                    break;
                case "--max-files":
                    string mfRaw = Next();
                    if (int.TryParse(mfRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mfVal) && mfVal > 0) maxFiles = mfVal;
                    else if (!string.IsNullOrEmpty(mfRaw)) errors.Add($"Invalid value for --max-files: {mfRaw}");
                    break;
                case "--timeout":
                    string toRaw = Next();
                    if (int.TryParse(toRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int toVal) && toVal > 0) timeoutSeconds = toVal;
                    else if (!string.IsNullOrEmpty(toRaw)) errors.Add($"Invalid value for --timeout: {toRaw}");
                    break;
                case "--boot-priority":
                    string bpRaw = Next().Trim().ToLowerInvariant();
                    if (bpRaw is "uefi" or "bios" or "auto") bootPriority = bpRaw;
                    else if (!string.IsNullOrEmpty(bpRaw)) errors.Add($"Invalid value for --boot-priority: {bpRaw}");
                    break;
                case "--json-report": jsonReportPath = Next(); break;
                case "--config": configPath = Next(); break;
                case "-p": case "--project": project = Next(); break;
                case "-o": case "--output": output = Next(); break;
                case "-b": case "--boot": bootDir = Next(); break;
                case "-i": case "--iso": isoDir = Next(); break;
                case "-V": case "--volume-id": volumeId = Next(); break;
                default:
                    errors.Add($"Unknown argument: {key}");
                    break;
            }
        }

        if (errors.Count > 0 && !silent)
            foreach (string err in errors)
                Console.WriteLine($"[WARN] {err}");

        return new IsoBuilderOptions
        {
            ProjectDir = project,
            Output = output,
            BootDir = bootDir,
            IsoDir = isoDir,
            VolumeId = volumeId,
            Uefi = uefi,
            SecureBoot = secureBoot,
            Silent = silent,
            NoPrompt = noPrompt,
            NoSpinner = noSpinner,
            NoProgress = noProgress,
            BiosOnly = biosOnly,
            UefiOnly = uefiOnly,
            Init = init,
            Version = version,
            Help = help,
            Verbose = verbose,
            PluginsOff = pluginsOff,
            DryRun = dryRun,
            Manifest = manifest,
            BootPriority = bootPriority,
            MaxSizeMb = maxSizeMb,
            MaxFiles = maxFiles,
            TimeoutSeconds = timeoutSeconds,
            JsonReportPath = jsonReportPath,
            ConfigPath = configPath,
            PluginAllowList = allowList.AsReadOnly(),
            ExcludePatterns = excludeList.AsReadOnly(),
            PluginHashPins = pluginHashPins
        };
    }
}

sealed class FileConfigModel
{
    public string Project { get; set; } = string.Empty;
    public string Output { get; set; } = string.Empty;
    public string BootDir { get; set; } = string.Empty;
    public string IsoDir { get; set; } = string.Empty;
    public string VolumeId { get; set; } = string.Empty;
    public bool? Uefi { get; set; }
    public bool? SecureBoot { get; set; }
    public bool? Silent { get; set; }
    public bool? NoPrompt { get; set; }
    public bool? NoSpinner { get; set; }
    public bool? NoProgress { get; set; }
    public bool? BiosOnly { get; set; }
    public bool? UefiOnly { get; set; }
    public bool? Verbose { get; set; }
    public bool? PluginsOff { get; set; }
    public bool? DryRun { get; set; }
    public bool? Manifest { get; set; }
    public string BootPriority { get; set; } = string.Empty;
    public long? MaxSizeMb { get; set; }
    public int? MaxFiles { get; set; }
    public int? TimeoutSeconds { get; set; }
    public string JsonReportPath { get; set; } = string.Empty;
    public List<string> PluginAllowList { get; set; } = new();
    public List<string> Exclude { get; set; } = new();
    public Dictionary<string, string> PluginHashPins { get; set; } = new();
}

static class ConfigFileLoader
{
    public static IsoBuilderOptions Merge(IsoBuilderOptions cli)
    {
        if (string.IsNullOrWhiteSpace(cli.ConfigPath)) return cli;

        if (!File.Exists(cli.ConfigPath))
        {
            try { Console.Error.WriteLine($"Warning: config file not found: {cli.ConfigPath}"); } catch { }
            return cli;
        }

        FileConfigModel? fc;
        try
        {
            string json = File.ReadAllText(cli.ConfigPath);
            fc = JsonSerializer.Deserialize<FileConfigModel>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            try { Console.Error.WriteLine($"Warning: failed to parse config file {cli.ConfigPath}: {ex.Message}"); } catch { }
            return cli;
        }

        if (fc == null) return cli;

        var mergedPlugins = new List<string>(cli.PluginAllowList);
        foreach (string p in fc.PluginAllowList)
            if (!mergedPlugins.Contains(p, StringComparer.OrdinalIgnoreCase)) mergedPlugins.Add(p);

        var mergedExcludes = new List<string>(cli.ExcludePatterns);
        foreach (string p in fc.Exclude)
            if (!mergedExcludes.Contains(p, StringComparer.Ordinal)) mergedExcludes.Add(p);

        var mergedPins = new Dictionary<string, string>(cli.PluginHashPins, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in fc.PluginHashPins)
            if (!mergedPins.ContainsKey(kv.Key)) mergedPins[kv.Key] = kv.Value.ToLowerInvariant();

        return new IsoBuilderOptions
        {
            ProjectDir = string.IsNullOrEmpty(cli.ProjectDir) ? fc.Project : cli.ProjectDir,
            Output = string.IsNullOrEmpty(cli.Output) ? fc.Output : cli.Output,
            BootDir = string.IsNullOrEmpty(cli.BootDir) ? fc.BootDir : cli.BootDir,
            IsoDir = string.IsNullOrEmpty(cli.IsoDir) ? fc.IsoDir : cli.IsoDir,
            VolumeId = string.IsNullOrEmpty(cli.VolumeId) ? fc.VolumeId : cli.VolumeId,
            Uefi = cli.Uefi || (fc.Uefi ?? false),
            SecureBoot = cli.SecureBoot || (fc.SecureBoot ?? false),
            Silent = cli.Silent || (fc.Silent ?? false),
            NoPrompt = cli.NoPrompt || (fc.NoPrompt ?? false),
            NoSpinner = cli.NoSpinner || (fc.NoSpinner ?? false),
            NoProgress = cli.NoProgress || (fc.NoProgress ?? false),
            BiosOnly = cli.BiosOnly || (fc.BiosOnly ?? false),
            UefiOnly = cli.UefiOnly || (fc.UefiOnly ?? false),
            Init = cli.Init,
            Version = cli.Version,
            Help = cli.Help,
            Verbose = cli.Verbose || (fc.Verbose ?? false),
            PluginsOff = cli.PluginsOff || (fc.PluginsOff ?? false),
            DryRun = cli.DryRun || (fc.DryRun ?? false),
            Manifest = cli.Manifest || (fc.Manifest ?? false),
            BootPriority = cli.BootPriority != "auto" ? cli.BootPriority : (string.IsNullOrEmpty(fc.BootPriority) ? cli.BootPriority : fc.BootPriority),
            MaxSizeMb = cli.MaxSizeMb > 0 ? cli.MaxSizeMb : (fc.MaxSizeMb ?? 0),
            MaxFiles = cli.MaxFiles > 0 ? cli.MaxFiles : (fc.MaxFiles ?? 0),
            TimeoutSeconds = cli.TimeoutSeconds > 0 ? cli.TimeoutSeconds : (fc.TimeoutSeconds ?? 0),
            JsonReportPath = string.IsNullOrEmpty(cli.JsonReportPath) ? fc.JsonReportPath : cli.JsonReportPath,
            ConfigPath = cli.ConfigPath,
            PluginAllowList = mergedPlugins.AsReadOnly(),
            ExcludePatterns = mergedExcludes.AsReadOnly(),
            PluginHashPins = mergedPins
        };
    }
}

static class GlobMatcher
{
    public static bool IsMatch(string input, string pattern)
    {
        if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(input)) return false;
        string regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(input, regexPattern, RegexOptions.IgnoreCase);
    }

    public static bool AnyMatch(string fileName, string relativePath, IReadOnlyList<string> patterns)
    {
        if (patterns.Count == 0) return false;
        foreach (string p in patterns)
            if (IsMatch(fileName, p) || IsMatch(relativePath, p)) return true;
        return false;
    }
}

static class FileHasher
{
    public const long MaxHashableBytes = 512 * 1024 * 1024;
    const int MaxAttempts = 3;

    public static string ComputeSha256(string path)
    {
        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Length > MaxHashableBytes) return "file_too_large";

                using var fs = File.OpenRead(path);
                byte[] hash = SHA256.HashData(fs);
                return Convert.ToHexString(hash).ToLowerInvariant();
            }
            catch (IOException) when (attempt < MaxAttempts - 1)
            {
                Thread.Sleep(100 * (attempt + 1));
            }
            catch (Exception ex)
            {
                return $"hash_error_{ex.GetType().Name}";
            }
        }

        return "hash_failed";
    }

    public static string Truncate(string hash, int length = 16)
    {
        if (string.IsNullOrEmpty(hash)) return hash;
        return hash.Length <= length ? hash : hash[..length];
    }
}

sealed class PluginManager
{
    readonly List<IBuildPlugin> _plugins = new();
    readonly HashSet<string> _allowedNames = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, string> _hashPins = new(StringComparer.OrdinalIgnoreCase);
    readonly bool _noPrompt;
    readonly bool _silent;

    public IReadOnlyList<IBuildPlugin> Plugins => _plugins;
    public PluginTrustMode TrustMode { get; private set; } = PluginTrustMode.Disabled;

    public PluginManager(bool noPrompt, bool silent)
    {
        _noPrompt = noPrompt;
        _silent = silent;
    }

    public void Configure(IsoBuilderOptions opts, BuildLogger logger)
    {
        _hashPins = new Dictionary<string, string>(opts.PluginHashPins, StringComparer.OrdinalIgnoreCase);

        if (opts.PluginsOff)
        {
            TrustMode = PluginTrustMode.Disabled;
            if (opts.PluginAllowList.Count > 0)
                logger.Warn("Plugins disabled via --plugins-off; ignoring --plugin allow-list entries.");
            return;
        }

        if (opts.PluginAllowList.Count > 0)
        {
            TrustMode = PluginTrustMode.AllowListed;
            foreach (string n in opts.PluginAllowList) _allowedNames.Add(n);
        }
        else
        {
            TrustMode = PluginTrustMode.ConfirmEach;
        }
    }

    public void LoadFromDirectory(string dir, BuildLogger logger, ConsoleUI ui)
    {
        if (TrustMode == PluginTrustMode.Disabled) return;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

        string[] dlls;
        try { dlls = Directory.GetFiles(dir, "*.dll"); }
        catch (Exception ex) { logger.Warn($"Failed to enumerate plugins in {dir}: {ex.Message}"); return; }

        foreach (string dll in dlls)
        {
            try
            {
                string hash = FileHasher.ComputeSha256(dll);
                var ctx2 = new AssemblyLoadContext($"plugin_{Path.GetFileNameWithoutExtension(dll)}", isCollectible: true);
                var asm = ctx2.LoadFromAssemblyPath(dll);

                var types = asm.GetTypes()
                    .Where(t => typeof(IBuildPlugin).IsAssignableFrom(t)
                                && !t.IsAbstract
                                && t.GetConstructor(Type.EmptyTypes) != null);

                foreach (var t in types)
                {
                    string pluginName = t.Name;
                    if (!IsPluginAllowed(pluginName, dll, hash, logger, ui))
                    {
                        logger.Warn($"Plugin blocked: {pluginName} ({dll})");
                        continue;
                    }

                    try
                    {
                        if (Activator.CreateInstance(t) is IBuildPlugin plugin)
                        {
                            string logLine = $"Loaded plugin: {plugin.Name} from {Path.GetFileName(dll)} [SHA256: {FileHasher.Truncate(hash)}...]";
                            lock (_plugins) _plugins.Add(plugin);
                            logger.Log(logLine);
                        }
                    }
                    catch (Exception ex) { logger.Error($"Failed to instantiate plugin {t.FullName}: {ex}"); }
                }
            }
            catch (Exception ex) { logger.Warn($"Failed to load plugin assembly {dll}: {ex.Message}"); }
        }
    }

    bool IsPluginAllowed(string name, string dllPath, string hash, BuildLogger logger, ConsoleUI ui)
    {
        if (_hashPins.TryGetValue(name, out string? pinned) && !string.IsNullOrEmpty(pinned))
        {
            if (!string.Equals(pinned, hash, StringComparison.OrdinalIgnoreCase))
            {
                logger.Error($"Plugin hash mismatch for pinned plugin '{name}': expected {pinned}, got {hash}. Refusing to load.");
                return false;
            }
        }

        if (TrustMode == PluginTrustMode.Disabled) return false;
        if (TrustMode == PluginTrustMode.AllowListed) return _allowedNames.Contains(name);
        if (_noPrompt || _silent) return false;

        ui.WriteLine(string.Empty);
        ui.WriteLine("Plugin detected:");
        ui.WriteLine($" Name : {name}");
        ui.WriteLine($" File : {dllPath}");
        ui.WriteLine($" SHA256: {hash}");
        ui.Write("Allow execution of this plugin? (y/N): ");
        string answer = Console.ReadLine() ?? string.Empty;
        bool allowed = PromptHelper.ParseYesNo(answer);
        if (!allowed) logger.Warn($"User denied plugin: {name} ({dllPath})");
        return allowed;
    }

    void Invoke(BuildLogger logger, string methodName, Action<IBuildPlugin> call)
    {
        List<IBuildPlugin> snapshot;
        lock (_plugins) snapshot = new List<IBuildPlugin>(_plugins);
        foreach (var p in snapshot)
        {
            try { call(p); }
            catch (Exception ex) { logger.Warn($"Plugin {p.Name} {methodName} failed: {ex.Message}"); }
        }
    }

    public void OnBeforeBuild(BuildContext ctx) => Invoke(ctx.Logger, nameof(OnBeforeBuild), p => p.OnBeforeBuild(ctx));
    public void OnAfterBuild(BuildContext ctx) => Invoke(ctx.Logger, nameof(OnAfterBuild), p => p.OnAfterBuild(ctx));
    public void OnStepExecuting(BuildContext ctx, IBuildStep step) => Invoke(ctx.Logger, nameof(OnStepExecuting), p => p.OnStepExecuting(ctx, step));
    public void OnStepExecuted(BuildContext ctx, IBuildStep step) => Invoke(ctx.Logger, nameof(OnStepExecuted), p => p.OnStepExecuted(ctx, step));
}

sealed class BuildPaths
{
    public const string AppVersion = "2027.1";
    public const string BootBinFilename = "boot.bin";
    public const string EfiImgFilename = "efiboot.img";
    public const string PluginsDirName = "plugins";
}

sealed class BuildConfig
{
    public string ProjectDir { get; init; } = string.Empty;
    public string BootDir { get; init; } = string.Empty;
    public string IsoDir { get; init; } = string.Empty;
    public string BootBinPath { get; init; } = string.Empty;
    public string EfiImgPath { get; init; } = string.Empty;
    public string IsoOutputPath { get; init; } = string.Empty;
    public string VolumeId { get; init; } = string.Empty;
    public bool IncludeUefi { get; init; }
    public bool BiosEnabled { get; init; }
    public bool SecureBoot { get; init; }
    public BootPriorityMode BootPriority { get; init; } = BootPriorityMode.Auto;
    public IReadOnlyList<string> ExcludePatterns { get; init; } = Array.Empty<string>();
    public long MaxSizeMb { get; init; }
    public int MaxFiles { get; init; }
    public bool ManifestEnabled { get; init; }
}

sealed class BuildState
{
    public bool Failed { get; set; }
    public string FailureMessage { get; set; } = string.Empty;
    public BootImageType BootType { get; set; } = BootImageType.Unknown;
    public long PredictedSizeMb { get; set; }
    public long ActualSizeBytes { get; set; }
    public double BuildSeconds { get; set; }
    public double BuildSpeedMbPerSec { get; set; }
    public int TotalFiles { get; set; }
    public int ProcessedFiles { get; set; }
    public string IsoSha256 { get; set; } = string.Empty;
    public List<(string IsoPath, string Sha256, long Size)> ManifestEntries { get; } = new();

    public void Fail(string message)
    {
        Failed = true;
        FailureMessage = message;
    }
}

sealed class BuildContext
{
    public BuildConfig Config { get; }
    public BuildState State { get; } = new();
    public IsoBuilderOptions Opts { get; }
    public BuildLogger Logger { get; }
    public ConsoleUI Ui { get; }
    public PluginManager Plugins { get; }
    public CancellationToken CancellationToken { get; }

    public BuildContext(BuildConfig config, IsoBuilderOptions opts, ConsoleUI ui, BuildLogger logger, PluginManager plugins, CancellationToken ct = default)
    {
        Config = config;
        Opts = opts;
        Ui = ui;
        Logger = logger;
        Plugins = plugins;
        CancellationToken = ct;
    }
}

sealed class BuildPipeline
{
    public List<IBuildStep> Steps { get; } = new();

    public static BuildPipeline CreateDefault() => new()
    {
        Steps =
        {
            new StepScanFiles(),
            new StepValidateBoot(),
            new StepPredictSize(),
            new StepCheckDiskSpace(),
            new StepBuildIso(),
            new StepVerifyIso(),
            new StepWriteManifest(),
            new StepWriteReadme(),
            new StepFinalize()
        }
    };
}

sealed class StepScanFiles : IBuildStep
{
    public string Name => "ScanFiles";
    public void Execute(BuildContext ctx)
    {
        int count = FileTreeScanner.CountFiles(ctx.Config.IsoDir, ctx.Logger, ctx.Config.ExcludePatterns);
        ctx.State.TotalFiles = count;
        ctx.State.ProcessedFiles = 0;
        if (!ctx.Opts.Silent) ctx.Ui.WriteLine($"Files to add: {count}");

        if (ctx.Config.MaxFiles > 0 && count > ctx.Config.MaxFiles)
            ctx.State.Fail($"File count guard triggered: {count} files exceed the configured limit of {ctx.Config.MaxFiles} (--max-files).");
    }
}

sealed class StepValidateBoot : IBuildStep
{
    public string Name => "ValidateBoot";
    public void Execute(BuildContext ctx)
    {
        if (ctx.Config.BiosEnabled)
        {
            if (!BootValidator.Validate(ctx.Config.BootBinPath, out BootImageType type, ctx.Logger))
            {
                ctx.State.Fail("Invalid or unrecognized boot.bin.");
                return;
            }
            ctx.State.BootType = type;
        }

        if (ctx.Config.IncludeUefi)
        {
            string efiBoot = Path.Combine(ctx.Config.IsoDir, "EFI", "BOOT", "BOOTX64.EFI");
            if (!File.Exists(efiBoot))
            {
                string warn = "Warning: EFI/BOOT/BOOTX64.EFI missing in ISO root.";
                if (!ctx.Opts.Silent) ctx.Ui.WriteLine(warn);
                ctx.Logger.Warn(warn);
            }
        }

        if (ctx.Config.SecureBoot)
        {
            string note = "Note: Secure Boot flag enabled. Ensure efiboot.img is properly signed.";
            if (!ctx.Opts.Silent) ctx.Ui.WriteLine(note);
            ctx.Logger.Log(note);
        }

        if (ctx.Config.BiosEnabled && ctx.Config.IncludeUefi)
        {
            string prioNote = $"Both BIOS and UEFI boot images configured. Boot priority: {ctx.Config.BootPriority}.";
            ctx.Logger.Log(prioNote);
        }
    }
}

sealed class StepPredictSize : IBuildStep
{
    public string Name => "PredictSize";
    public void Execute(BuildContext ctx)
    {
        long mb = SizePredictor.PredictIsoSizeMb(ctx.Config.IsoDir, ctx.Config.BootBinPath, ctx.Config.EfiImgPath, ctx.Logger);
        ctx.State.PredictedSizeMb = mb;
        if (!ctx.Opts.Silent) ctx.Ui.WriteLine($"Predicted ISO size: {mb} MB");
        ctx.Logger.Log($"Predicted ISO size: {mb} MB");

        if (ctx.Config.MaxSizeMb > 0 && mb > ctx.Config.MaxSizeMb)
            ctx.State.Fail($"Size guard triggered: predicted {mb} MB exceeds the configured limit of {ctx.Config.MaxSizeMb} MB (--max-size).");
    }
}

sealed class StepCheckDiskSpace : IBuildStep
{
    public string Name => "CheckDiskSpace";
    public void Execute(BuildContext ctx)
    {
        if (!DiskSpaceChecker.Check(ctx.Config.IsoOutputPath, ctx.State.PredictedSizeMb, ctx.Logger, ctx.Ui, ctx.Opts.NoPrompt, ctx.Opts.Silent))
            ctx.State.Fail("Build cancelled: insufficient disk space or disk check failed.");
    }
}

sealed class StepBuildIso : IBuildStep
{
    public string Name => "BuildIso";
    public void Execute(BuildContext ctx)
    {
        if (ctx.CancellationToken.IsCancellationRequested) { ctx.State.Fail("Build cancelled."); return; }

        if (ctx.Opts.DryRun)
        {
            var swDry = Stopwatch.StartNew();
            try
            {
                var probeBuilder = new CDBuilder { UseJoliet = true, VolumeIdentifier = ctx.Config.VolumeId };
                int processed = 0;
                FileTreeScanner.AddToBuilder(probeBuilder, ctx.Config.IsoDir, ctx.Logger, ref processed, ctx.Config.ExcludePatterns, false, ctx.State.ManifestEntries);
                ctx.State.ProcessedFiles = processed;
                swDry.Stop();
                ctx.State.BuildSeconds = swDry.Elapsed.TotalSeconds;
                if (!ctx.Opts.Silent) ctx.Ui.WriteLine("Dry run: file scan and sanitization validated, no ISO written.");
                ctx.Logger.Log("Dry run: build step simulated without writing output.");
            }
            catch (Exception ex)
            {
                ctx.State.Fail($"Dry run validation failed: {ex.Message}");
                ctx.Logger.Error($"Dry run exception: {ex}");
            }
            return;
        }

        var sw = Stopwatch.StartNew();
        using var spinner = new SpinnerScope(ctx.Ui, ctx.Opts);

        try
        {
            IsoBuilderService.BuildIso(ctx);
            sw.Stop();

            if (!ctx.State.Failed)
            {
                ctx.State.BuildSeconds = sw.Elapsed.TotalSeconds;
                double sizeMb = ctx.State.ActualSizeBytes / (1024.0 * 1024.0);
                ctx.State.BuildSpeedMbPerSec = ctx.State.BuildSeconds > 0 ? sizeMb / ctx.State.BuildSeconds : 0;
                spinner.Succeed();
            }
            else
            {
                spinner.Fail();
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            spinner.Fail();
            ctx.State.Fail($"Build step failed: {ex.Message}");
            ctx.Logger.Error($"Build step exception: {ex}");
        }
    }
}

sealed class StepVerifyIso : IBuildStep
{
    public string Name => "VerifyIso";
    public void Execute(BuildContext ctx)
    {
        if (!File.Exists(ctx.Config.IsoOutputPath))
        {
            ctx.State.Fail("ISO output file not found after build.");
            return;
        }

        FileInfo finfo;
        try { finfo = new FileInfo(ctx.Config.IsoOutputPath); }
        catch (Exception ex) { ctx.State.Fail($"Cannot verify ISO: {ex.Message}"); return; }

        long actual = finfo.Length;
        if (actual < 2048)
        {
            ctx.State.Fail($"ISO output file is suspiciously small: {actual} bytes.");
            return;
        }

        long predictedBytes = ctx.State.PredictedSizeMb * 1024L * 1024L;
        double ratio = predictedBytes > 0 ? (double)actual / predictedBytes : 1.0;
        if (ratio < 0.05 || ratio > 20.0)
            ctx.Logger.Warn($"ISO size ({actual} bytes) differs significantly from prediction ({predictedBytes} bytes).");

        const long VolumeDescriptorOffset = 32768;
        const int SignatureProbeLength = 6;

        if (actual >= VolumeDescriptorOffset + SignatureProbeLength)
        {
            byte[] probe = new byte[SignatureProbeLength];
            try
            {
                using (var fs = File.OpenRead(ctx.Config.IsoOutputPath))
                {
                    fs.Seek(VolumeDescriptorOffset, SeekOrigin.Begin);
                    int total = 0;
                    while (total < SignatureProbeLength)
                    {
                        int n = fs.Read(probe, total, SignatureProbeLength - total);
                        if (n <= 0) break;
                        total += n;
                    }

                    if (total < SignatureProbeLength)
                    {
                        ctx.Logger.Warn("ISO file too small to read full volume descriptor header.");
                    }
                    else if (!(probe[1] == (byte)'C' && probe[2] == (byte)'D' && probe[3] == (byte)'0' && probe[4] == (byte)'0' && probe[5] == (byte)'1'))
                    {
                        ctx.Logger.Warn("ISO 9660 signature not found at expected position (sector 16).");
                    }
                }
            }
            catch (Exception ex) { ctx.Logger.Warn($"Failed to validate ISO header: {ex.Message}"); }
        }
        else
        {
            ctx.Logger.Warn("ISO file too small to contain a volume descriptor at sector 16.");
        }

        string hash = FileHasher.ComputeSha256(ctx.Config.IsoOutputPath);
        ctx.State.IsoSha256 = hash;
        ctx.Logger.Log($"ISO SHA256: {hash}");
        if (!ctx.Opts.Silent) ctx.Ui.WriteLine($"ISO SHA256: {hash}");
    }
}

sealed class StepWriteManifest : IBuildStep
{
    public string Name => "WriteManifest";
    public void Execute(BuildContext ctx)
    {
        if (!ctx.Config.ManifestEnabled) return;

        string outputDir = Path.GetDirectoryName(ctx.Config.IsoOutputPath) ?? ctx.Config.ProjectDir;
        string manifestPath = Path.Combine(outputDir, "MANIFEST.SHA256SUMS");

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# BuildIso {BuildPaths.AppVersion} manifest");
            sb.AppendLine($"# Generated: {DateTime.UtcNow:O}");
            sb.AppendLine($"# ISO: {Path.GetFileName(ctx.Config.IsoOutputPath)}");
            sb.AppendLine($"# ISO SHA256: {ctx.State.IsoSha256}");
            sb.AppendLine();

            foreach (var entry in ctx.State.ManifestEntries.OrderBy(e => e.IsoPath, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine($"{entry.Sha256}  {entry.Size,12}  {entry.IsoPath}");

            File.WriteAllText(manifestPath, sb.ToString(), BuildLogger.Utf8NoBom);
            ctx.Logger.Log($"Manifest written: {manifestPath} ({ctx.State.ManifestEntries.Count} entries)");
            if (!ctx.Opts.Silent) ctx.Ui.WriteLine($"Manifest: {manifestPath}");
        }
        catch (Exception ex) { ctx.Logger.Warn($"Failed to write manifest: {ex.Message}"); }
    }
}

sealed class StepWriteReadme : IBuildStep
{
    public string Name => "WriteReadme";
    public void Execute(BuildContext ctx)
    {
        string outputDir = Path.GetDirectoryName(ctx.Config.IsoOutputPath) ?? ctx.Config.ProjectDir;
        string projectName = Path.GetFileName(ctx.Config.ProjectDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(projectName)) projectName = "BuildIso";
        ReadmeGenerator.Write(outputDir, projectName, BuildPaths.AppVersion, ctx.Config.BiosEnabled, ctx.Config.IncludeUefi, ctx.Config.SecureBoot, ctx.Config.BootPriority, ctx.Config.ManifestEnabled, ctx.Logger);
    }
}

sealed class StepFinalize : IBuildStep
{
    public string Name => "Finalize";
    public void Execute(BuildContext ctx) => ctx.Logger.Log("Build completed successfully.");
}

sealed class SpinnerScope : IDisposable
{
    readonly ConsoleUI _ui;
    readonly IsoBuilderOptions _opts;
    readonly CancellationTokenSource _cts;
    readonly Task _task;
    bool _succeeded;
    bool _disposed;

    public SpinnerScope(ConsoleUI ui, IsoBuilderOptions opts)
    {
        _ui = ui;
        _opts = opts;
        _cts = new CancellationTokenSource();

        if (!opts.NoSpinner && !opts.NoProgress && !opts.Silent)
            _task = SpinAsync(_cts.Token);
        else
            _task = Task.CompletedTask;
    }

    public void Succeed() { _succeeded = true; }
    public void Fail() { _succeeded = false; }

    async Task SpinAsync(CancellationToken ct)
    {
        char[] seq = ['/', '-', '\\', '|'];
        int idx = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                _ui.WriteInline($"\rBuilding ISO... {seq[idx]}");
                idx = (idx + 1) % seq.Length;
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        try
        {
            if (!_task.IsCompleted)
                _task.Wait(TimeSpan.FromMilliseconds(500));
        }
        catch (OperationCanceledException) { }
        catch { }
        _cts.Dispose();
        if (!_opts.Silent)
            _ui.WriteInline(_succeeded ? "\rBuilding ISO... done  \n" : "\rBuilding ISO... failed\n");
    }
}

sealed class ConsoleUI
{
    readonly object _lock;
    public ConsoleUI(object lockObj) { _lock = lockObj; }
    public void WriteLine(string s) { lock (_lock) Console.WriteLine(s); }
    public void Write(string s) { lock (_lock) Console.Write(s); }
    public void WriteInline(string s) { lock (_lock) Console.Write(s); }
}

static class PromptHelper
{
    public static bool ParseYesNo(string input)
    {
        string normalized = (input ?? string.Empty).Trim().ToLowerInvariant();
        return normalized == "y" || normalized == "yes";
    }
}

static class IsoBuilderService
{
    public static void BuildIso(BuildContext ctx)
    {
        string outPath = ctx.Config.IsoOutputPath;
        string tempPath = outPath + ".tmp";
        Stream? bootStream = null;

        try
        {
            var builder = new CDBuilder
            {
                UseJoliet = true,
                VolumeIdentifier = ctx.Config.VolumeId
            };

            int processed = 0;
            FileTreeScanner.AddToBuilder(builder, ctx.Config.IsoDir, ctx.Logger, ref processed, ctx.Config.ExcludePatterns, ctx.Config.ManifestEnabled, ctx.State.ManifestEntries);
            ctx.State.ProcessedFiles = processed;

            bool hasUefi = ctx.Config.IncludeUefi && File.Exists(ctx.Config.EfiImgPath);
            bool hasBios = ctx.Config.BiosEnabled && File.Exists(ctx.Config.BootBinPath);
            bool bootSet = false;

            if (hasUefi && hasBios)
            {
                bool preferBios = ctx.Config.BootPriority == BootPriorityMode.PreferBios;
                string chosenPath = preferBios ? ctx.Config.BootBinPath : ctx.Config.EfiImgPath;
                string chosenLabel = preferBios ? "BIOS (boot.bin)" : "UEFI (efiboot.img)";
                string droppedLabel = preferBios ? "UEFI (efiboot.img)" : "BIOS (boot.bin)";

                ctx.Logger.Warn($"Both BIOS and UEFI boot images are present, but the ISO writer only supports a single El Torito boot entry. Embedding {chosenLabel} and dropping {droppedLabel}. Use --boot-priority to control this choice.");
                if (!ctx.Opts.Silent) ctx.Ui.WriteLine($"Warning: only one boot method can be embedded in this ISO; using {chosenLabel}.");

                bootStream = File.OpenRead(chosenPath);
                builder.SetBootImage(bootStream, BootDeviceEmulation.NoEmulation, 0);
                bootSet = true;
            }
            else if (hasUefi)
            {
                bootStream = File.OpenRead(ctx.Config.EfiImgPath);
                builder.SetBootImage(bootStream, BootDeviceEmulation.NoEmulation, 0);
                bootSet = true;
            }
            else if (hasBios)
            {
                bootStream = File.OpenRead(ctx.Config.BootBinPath);
                builder.SetBootImage(bootStream, BootDeviceEmulation.NoEmulation, 0);
                bootSet = true;
            }

            if (!bootSet) ctx.Logger.Warn("No boot image configured. ISO will not be bootable.");

            using (var isoStream = builder.Build())
            {
                if (isoStream == null) throw new InvalidOperationException("CDBuilder.Build() returned null stream.");
                using var fileStream = File.Create(tempPath);
                isoStream.CopyTo(fileStream);
                fileStream.Flush();
            }

            SafeReplace(tempPath, outPath, ctx.Logger);
            ctx.State.ActualSizeBytes = new FileInfo(outPath).Length;
        }
        catch (Exception ex)
        {
            TryDelete(tempPath, ctx.Logger);
            ctx.State.Fail($"ISO build failed: {ex.Message}");
            ctx.Logger.Error($"ISO build exception: {ex.Message}");
        }
        finally
        {
            if (bootStream != null)
                try { bootStream.Dispose(); } catch (Exception ex) { ctx.Logger.Warn($"Failed to dispose boot stream: {ex.Message}"); }
        }
    }

    static void SafeReplace(string tempPath, string destPath, BuildLogger logger)
    {
        string backupPath = destPath + ".bak";
        bool hadExisting = File.Exists(destPath);
        const int maxRetries = 3;

        if (hadExisting)
        {
            for (int i = 0; i < maxRetries; i++)
            {
                try { File.Move(destPath, backupPath, overwrite: true); break; }
                catch (IOException) when (i < maxRetries - 1)
                {
                    logger.Warn($"Backup attempt {i + 1} failed, retrying...");
                    Thread.Sleep(100 * (i + 1));
                }
                catch (Exception ex) { logger.Warn($"Could not back up existing ISO: {ex.Message}"); hadExisting = false; break; }
            }
        }

        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                File.Move(tempPath, destPath, overwrite: false);
                if (hadExisting) TryDelete(backupPath, logger);
                return;
            }
            catch (IOException) when (i < maxRetries - 1)
            {
                logger.Warn($"Move attempt {i + 1} failed, retrying...");
                Thread.Sleep(100 * (i + 1));
            }
            catch (Exception ex) { logger.Warn($"File.Move failed, falling back to copy: {ex.Message}"); break; }
        }

        try
        {
            using var src = File.OpenRead(tempPath);
            using var dst = File.Create(destPath);
            src.CopyTo(dst);
            dst.Flush();
            TryDelete(tempPath, logger);
            if (hadExisting) TryDelete(backupPath, logger);
        }
        catch (Exception copyEx)
        {
            if (hadExisting)
            {
                logger.Warn($"Copy also failed, restoring backup: {copyEx.Message}");
                try { File.Move(backupPath, destPath, overwrite: true); } catch { }
            }
            throw;
        }
    }

    static void TryDelete(string path, BuildLogger logger)
    {
        if (!File.Exists(path)) return;
        const int maxRetries = 3;
        for (int i = 0; i < maxRetries; i++)
        {
            try { File.Delete(path); return; }
            catch (IOException) when (i < maxRetries - 1)
            {
                Thread.Sleep(50 * (i + 1));
            }
            catch (Exception ex) { logger.Warn($"Failed to delete {path}: {ex.Message}"); return; }
        }
    }
}

static class BootValidator
{
    public static bool Validate(string path, out BootImageType type, BuildLogger logger)
    {
        type = BootImageType.Unknown;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;

        try
        {
            long fileSize = new FileInfo(path).Length;
            if (fileSize < 512) return false;
            if (fileSize > 64 * 1024 * 1024) { logger.Warn($"Boot file suspiciously large: {fileSize} bytes"); return false; }

            int readLen = (int)Math.Min(fileSize, 4096);
            byte[] buf = new byte[readLen];

            using (var fs = File.OpenRead(path))
            {
                int total = 0;
                while (total < readLen)
                {
                    int n = fs.Read(buf, total, readLen - total);
                    if (n <= 0) break;
                    total += n;
                }
                readLen = total;
            }

            if (readLen < 512) return false;

            int biosScanLen = Math.Min(446, readLen);
            bool anyCode = false;
            for (int i = 0; i < biosScanLen; i++)
                if (buf[i] != 0) { anyCode = true; break; }

            if (anyCode && buf[510] == 0x55 && buf[511] == 0xAA)
            {
                type = BootImageType.BiosBootSector512;
                return true;
            }

            if (readLen < 0x40 || buf[0] != 0x4D || buf[1] != 0x5A) return false;
            if (fileSize < 64 * 1024) return false;

            int e_lfanew = BitConverter.ToInt32(buf, 0x3C);
            if (e_lfanew < 0 || e_lfanew + 4 > readLen) return false;

            if (e_lfanew + 0x1C <= readLen && buf[e_lfanew] == (byte)'P' && buf[e_lfanew + 1] == (byte)'E'
                && buf[e_lfanew + 2] == 0 && buf[e_lfanew + 3] == 0)
            {
                ushort machine = BitConverter.ToUInt16(buf, e_lfanew + 4);
                ushort optMagic = BitConverter.ToUInt16(buf, e_lfanew + 0x18);
                if (optMagic == 0x20b && machine == 0x8664)
                {
                    type = BootImageType.EfiImage;
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            logger.Error($"Boot validation error: {ex.Message}");
            return false;
        }
    }
}

static class DiskSpaceChecker
{
    public static bool Check(string targetPath, long predictedMb, BuildLogger logger, ConsoleUI ui, bool noPrompt, bool silent)
    {
        if (string.IsNullOrEmpty(targetPath)) return false;

        try
        {
            string fullPath = Path.GetFullPath(targetPath);
            string? root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root)) root = Path.GetPathRoot(Environment.CurrentDirectory);
            if (string.IsNullOrEmpty(root)) { logger.Warn("Could not determine drive root."); return false; }

            DriveInfo drive;
            try { drive = new DriveInfo(root); }
            catch (Exception ex) { logger.Warn($"Could not read drive info for '{root}': {ex.Message}"); return false; }

            long requiredMb = (long)Math.Ceiling(predictedMb * 1.5);
            long freeMb = drive.AvailableFreeSpace / (1024L * 1024L);
            if (freeMb >= requiredMb) return true;

            string msg = $"Insufficient disk space. Required: ~{requiredMb} MB (1.5x predicted), available: {freeMb} MB";
            ui.WriteLine(msg);
            logger.Warn(msg);

            if (noPrompt || silent) { logger.Warn("Aborting: no-prompt mode."); return false; }

            ui.Write("Continue anyway? (y/N): ");
            string a = Console.ReadLine() ?? string.Empty;
            return PromptHelper.ParseYesNo(a);
        }
        catch (Exception ex)
        {
            logger.Error($"Disk space check failed: {ex.Message}");
            return false;
        }
    }
}

static class FileTreeScanner
{
    static readonly HashSet<string> IgnoredFiles = new(StringComparer.OrdinalIgnoreCase)
        { ".DS_Store", "Thumbs.db", "desktop.ini", ".gitignore", ".gitattributes" };

    static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
        { ".git", ".svn", ".hg", "__pycache__", ".vs", "node_modules" };

    const int JolietMaxComponentLength = 64;

    public static int CountFiles(string dir, BuildLogger logger, IReadOnlyList<string> excludePatterns)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return 0;
        int count = 0;
        Traverse(dir, logger, excludePatterns, (d, f) =>
        {
            count++;
            if (count > 1_000_000) { logger.Warn("Too many files, scan capped at 1,000,000."); return false; }
            return true;
        });
        return count;
    }

    public static void AddToBuilder(CDBuilder builder, string rootDir, BuildLogger logger, ref int processedFiles, IReadOnlyList<string> excludePatterns, bool computeManifest, List<(string IsoPath, string Sha256, long Size)> manifestEntries)
    {
        if (string.IsNullOrEmpty(rootDir) || !Directory.Exists(rootDir)) return;
        string basePath = Path.GetFullPath(rootDir);
        int processed = 0;

        Traverse(rootDir, logger, excludePatterns, (dir, f) =>
        {
            string name = Path.GetFileName(f);
            string relDir = Path.GetRelativePath(basePath, dir);
            string isoPath = relDir == "." ? name : $"{relDir}/{name}".Replace('\\', '/');

            string sanitized = SanitizeIsoPath(isoPath);
            if (sanitized != isoPath)
                logger.Warn($"ISO path sanitized: '{isoPath}' -> '{sanitized}'");

            try
            {
                builder.AddFile(sanitized, f);
                processed++;

                if (computeManifest)
                {
                    string sha = FileHasher.ComputeSha256(f);
                    long size = 0;
                    try { size = new FileInfo(f).Length; } catch { }
                    manifestEntries.Add((sanitized, sha, size));
                }
            }
            catch (Exception ex) { logger.Warn($"Failed to add file {f} to ISO: {ex.Message}"); }
            return true;
        });

        processedFiles = processed;
    }

    static string SanitizeIsoPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        var parts = path.Split('/', '\\')
            .Where(p => !string.IsNullOrEmpty(p) && p != ".." && p != ".")
            .Select(p => SanitizePathComponent(p))
            .Where(p => !string.IsNullOrEmpty(p))
            .ToList();
        if (parts.Count == 0) return string.Empty;
        return string.Join("/", parts);
    }

    static string SanitizePathComponent(string component)
    {
        var sanitized = new string(component
            .Where(c => c != '\0' && !char.IsControl(c) && (char.IsLetterOrDigit(c) || "._-() ".Contains(c)))
            .ToArray());
        if (sanitized.Length > JolietMaxComponentLength) sanitized = sanitized[..JolietMaxComponentLength];
        return sanitized;
    }

    static bool IsExcludedFile(string basePath, string dir, string file, IReadOnlyList<string> patterns)
    {
        if (patterns.Count == 0) return false;
        string name = Path.GetFileName(file);
        string relDir = Path.GetRelativePath(basePath, dir);
        string relPath = relDir == "." ? name : $"{relDir}/{name}".Replace('\\', '/');
        return GlobMatcher.AnyMatch(name, relPath, patterns);
    }

    static bool IsExcludedDirectory(string basePath, string dir, IReadOnlyList<string> patterns)
    {
        if (patterns.Count == 0) return false;
        string name = Path.GetFileName(dir);
        string relPath = Path.GetRelativePath(basePath, dir).Replace('\\', '/');
        return GlobMatcher.AnyMatch(name, relPath, patterns);
    }

    static void Traverse(string rootDir, BuildLogger logger, IReadOnlyList<string> excludePatterns, Func<string, string, bool> onFileWithDir)
    {
        string basePath = Path.GetFullPath(rootDir);
        var queue = new Queue<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        queue.Enqueue(basePath);
        const int MaxQueueSize = 100_000;

        while (queue.Count > 0)
        {
            if (queue.Count > MaxQueueSize)
            {
                logger.Warn($"Directory traversal queue exceeded {MaxQueueSize}, aborting to prevent memory exhaustion.");
                break;
            }

            string current = queue.Dequeue();
            if (!visited.Add(current)) continue;

            DirectoryInfo di;
            try { di = new DirectoryInfo(current); }
            catch (IOException ex) { logger.Warn($"IO error inspecting {current}: {ex.Message}"); continue; }
            catch (UnauthorizedAccessException ex) { logger.Warn($"Access denied: {current}: {ex.Message}"); continue; }
            catch (Exception ex) { logger.Warn($"Failed to inspect directory {current}: {ex.Message}"); continue; }

            if ((di.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                logger.Warn($"Skipping symlink/junction: {current}");
                continue;
            }

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(current); }
            catch (IOException ex) { logger.Warn($"IO error enumerating files in {current}: {ex.Message}"); continue; }
            catch (UnauthorizedAccessException ex) { logger.Warn($"Access denied: {current}: {ex.Message}"); continue; }
            catch (Exception ex) { logger.Warn($"Failed to enumerate files in {current}: {ex.Message}"); continue; }

            bool stop = false;
            foreach (string f in files)
            {
                if (IgnoredFiles.Contains(Path.GetFileName(f))) continue;
                if (IsExcludedFile(basePath, current, f, excludePatterns)) continue;
                if (!onFileWithDir(current, f)) { stop = true; break; }
            }
            if (stop) return;

            IEnumerable<string> dirs;
            try { dirs = Directory.EnumerateDirectories(current); }
            catch (IOException ex) { logger.Warn($"IO error enumerating dirs in {current}: {ex.Message}"); continue; }
            catch (UnauthorizedAccessException ex) { logger.Warn($"Access denied: {current}: {ex.Message}"); continue; }
            catch (Exception ex) { logger.Warn($"Failed to enumerate dirs in {current}: {ex.Message}"); continue; }

            foreach (string d in dirs)
            {
                if (IgnoredDirs.Contains(Path.GetFileName(d))) continue;
                string resolved = Path.GetFullPath(d);
                if (IsExcludedDirectory(basePath, resolved, excludePatterns)) continue;
                if (!visited.Contains(resolved)) queue.Enqueue(resolved);
            }
        }
    }
}

static class SizePredictor
{
    public static long PredictIsoSizeMb(string isoRoot, string bootBinPath, string efiImgPath, BuildLogger logger)
    {
        long totalBytes = 0;
        const int maxDirSize = 500 * 1024 * 1024;

        if (Directory.Exists(isoRoot))
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(isoRoot, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        long size = new FileInfo(file).Length;
                        totalBytes += size;
                        if (totalBytes > maxDirSize)
                        {
                            logger.Warn($"ISO root directory exceeds {maxDirSize / (1024 * 1024)} MB, size prediction may be inaccurate.");
                            break;
                        }
                    }
                    catch (Exception ex) { logger.Warn($"Failed to stat {file}: {ex.Message}"); }
                }
            }
            catch (Exception ex) { logger.Warn($"Failed to enumerate ISO root for size prediction: {ex.Message}"); }
        }

        void AddIfExists(string path)
        {
            if (File.Exists(path))
                try { totalBytes += new FileInfo(path).Length; }
                catch (Exception ex) { logger.Warn($"Failed to stat {path}: {ex.Message}"); }
        }

        AddIfExists(bootBinPath);
        AddIfExists(efiImgPath);

        long mb = (totalBytes + (1024L * 1024L - 1)) / (1024L * 1024L);
        mb += 32;
        return mb;
    }
}

static class VolumeIdHelper
{
    public static string Normalize(string input, BuildLogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(input)) return "BUILDISO";
        string upper = new string(input.Trim().ToUpperInvariant()
            .Where(c => (char.IsLetterOrDigit(c) && char.IsAscii(c)) || c == '_').ToArray());
        if (upper.Length == 0) upper = "BUILDISO";
        if (upper.Length > 32) { upper = upper[..32]; logger?.Warn($"Volume ID truncated to 32 chars: {input}"); }
        if (upper != input.Trim().ToUpperInvariant())
            logger?.Warn($"Volume ID normalized: '{input}' -> '{upper}' (ISO9660 Level 1)");
        return upper;
    }
}

static class ReadmeGenerator
{
    public static void Write(string outputDir, string projectName, string appVersion, bool bios, bool uefi, bool secureBoot, BootPriorityMode bootPriority, bool manifestEnabled, BuildLogger logger)
    {
        try
        {
            string path = Path.Combine(outputDir, "README_BUILDISO.txt");
            var sb = new StringBuilder();
            sb.AppendLine($"BuildIso {appVersion}");
            sb.AppendLine();
            sb.AppendLine($"Project    : {projectName}");
            sb.AppendLine($"BIOS boot  : {(bios ? "YES" : "NO")}");
            sb.AppendLine($"UEFI boot  : {(uefi ? "YES" : "NO")}");
            sb.AppendLine($"Secure Boot: {(secureBoot ? "YES" : "NO")}");
            if (bios && uefi) sb.AppendLine($"Boot priority: {bootPriority}");
            sb.AppendLine($"Manifest   : {(manifestEnabled ? "YES" : "NO")}");
            sb.AppendLine();
            sb.AppendLine("This ISO was generated by BuildIso.");
            File.WriteAllText(path, sb.ToString(), BuildLogger.Utf8NoBom);
        }
        catch (Exception ex) { logger.Warn($"Failed to write README: {ex.Message}"); }
    }
}

sealed class BuildLogger
{
    readonly object _lock = new();
    readonly string _logPath;
    const int MaxLines = 50_000;
    const int MaxFileSize = 50 * 1024 * 1024;
    int _lineCount;

    public static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    public BuildLogger(string projectDir) => _logPath = Path.Combine(projectDir, "buildiso.log");

    public void Log(string msg) => Write("INFO", msg);
    public void Warn(string msg) => Write("WARN", msg);
    public void Error(string msg) => Write("ERROR", msg);

    void Write(string level, string msg)
    {
        lock (_lock)
        {
            if (_lineCount >= MaxLines) return;

            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {msg}";
            try
            {
                if (File.Exists(_logPath) && new FileInfo(_logPath).Length > MaxFileSize)
                    RotateLog();

                File.AppendAllText(_logPath, line + Environment.NewLine, Utf8NoBom);
                _lineCount++;
            }
            catch (Exception ex)
            {
                try { Console.Error.WriteLine($"[LOG_ERROR] {ex.Message}"); } catch { }
            }
        }
    }

    void RotateLog()
    {
        try
        {
            string backup = $"{_logPath}.{DateTime.Now:yyyyMMdd_HHmmss}.bak";
            File.Move(_logPath, backup, false);

            string logDir = Path.GetDirectoryName(_logPath) ?? ".";
            string[] oldBackups = Directory.GetFiles(logDir, "buildiso.log.*.bak")
                .OrderByDescending(f => new FileInfo(f).LastWriteTime)
                .Skip(3)
                .ToArray();

            foreach (string old in oldBackups)
                try { File.Delete(old); } catch { }
        }
        catch { }
    }

    public void Save() { }
}

sealed class BuildReport
{
    public string Version { get; init; } = string.Empty;
    public string ProjectDir { get; init; } = string.Empty;
    public string Output { get; init; } = string.Empty;
    public bool Success { get; init; }
    public string FailureMessage { get; init; } = string.Empty;
    public string BootType { get; init; } = string.Empty;
    public long PredictedSizeMb { get; init; }
    public long ActualSizeBytes { get; init; }
    public double BuildSeconds { get; init; }
    public double BuildSpeedMbPerSec { get; init; }
    public int TotalFiles { get; init; }
    public int ProcessedFiles { get; init; }
    public string IsoSha256 { get; init; } = string.Empty;
    public bool DryRun { get; init; }
    public IReadOnlyList<string> Plugins { get; init; } = Array.Empty<string>();
    public string GeneratedAtUtc { get; init; } = string.Empty;
}

static class BuildReportWriter
{
    public static void Write(string path, BuildContext ctx)
    {
        try
        {
            var report = new BuildReport
            {
                Version = BuildPaths.AppVersion,
                ProjectDir = ctx.Config.ProjectDir,
                Output = ctx.Config.IsoOutputPath,
                Success = !ctx.State.Failed,
                FailureMessage = ctx.State.FailureMessage,
                BootType = ctx.State.BootType.ToString(),
                PredictedSizeMb = ctx.State.PredictedSizeMb,
                ActualSizeBytes = ctx.State.ActualSizeBytes,
                BuildSeconds = ctx.State.BuildSeconds,
                BuildSpeedMbPerSec = ctx.State.BuildSpeedMbPerSec,
                TotalFiles = ctx.State.TotalFiles,
                ProcessedFiles = ctx.State.ProcessedFiles,
                IsoSha256 = ctx.State.IsoSha256,
                DryRun = ctx.Opts.DryRun,
                Plugins = ctx.Plugins.Plugins.Select(p => p.Name).ToArray(),
                GeneratedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            };

            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json, BuildLogger.Utf8NoBom);
            ctx.Logger.Log($"JSON report written: {path}");
        }
        catch (Exception ex) { ctx.Logger.Warn($"Failed to write JSON report: {ex.Message}"); }
    }
}

static class Program
{
    static readonly object ConsoleLock = new();

    static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            try { Console.Error.WriteLine($"Fatal unhandled exception: {e.ExceptionObject}"); } catch { }
        };
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            e.SetObserved();
            try { Console.Error.WriteLine($"Unobserved background task exception: {e.Exception}"); } catch { }
        };

        try
        {
            var opts = ConfigFileLoader.Merge(IsoBuilderOptions.Parse(args ?? Array.Empty<string>()));

            if (opts.BiosOnly && opts.UefiOnly)
            {
                Console.WriteLine("Error: --bios-only and --uefi-only cannot be used together.");
                return 1;
            }

            if (opts.Help) { PrintHelp(); return 0; }
            if (opts.Version) { Console.WriteLine($"BuildIso {BuildPaths.AppVersion}"); return 0; }
            if (opts.Init) return RunInit();

            var ui = new ConsoleUI(ConsoleLock);

            string projectDir = ResolveProjectDir(opts, ui);
            if (string.IsNullOrEmpty(projectDir)) return ExitError("No project directory specified.", ui, opts);
            if (!Directory.Exists(projectDir)) return ExitError($"Project directory does not exist: {projectDir}", ui, opts);

            var logger = new BuildLogger(projectDir);

            BuildConfig? config = BuildConfigFactory.Create(opts, projectDir, ui, logger);
            if (config == null)
            {
                logger.Save();
                return ExitError("Configuration failed.", ui, opts);
            }

            var plugins = new PluginManager(opts.NoPrompt, opts.Silent);
            plugins.Configure(opts, logger);

            string exeDir = AppContext.BaseDirectory;
            plugins.LoadFromDirectory(Path.Combine(exeDir, BuildPaths.PluginsDirName), logger, ui);
            plugins.LoadFromDirectory(Path.Combine(projectDir, BuildPaths.PluginsDirName), logger, ui);

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); logger.Log("Build cancelled by user (Ctrl+C)."); };
            if (opts.TimeoutSeconds > 0)
            {
                cts.CancelAfter(TimeSpan.FromSeconds(opts.TimeoutSeconds));
                logger.Log($"Timeout guard enabled: {opts.TimeoutSeconds} seconds.");
            }

            var ctx = new BuildContext(config, opts, ui, logger, plugins, cts.Token);

            if (!opts.Silent)
            {
                ui.WriteLine(string.Empty);
                ui.WriteLine("=== Build Configuration ===");
                ui.WriteLine($"Project    : {config.ProjectDir}");
                ui.WriteLine($"Boot dir   : {config.BootDir}");
                ui.WriteLine($"ISO root   : {config.IsoDir}");
                ui.WriteLine($"BIOS       : {(config.BiosEnabled ? "YES" : "NO")}");
                ui.WriteLine($"UEFI       : {(config.IncludeUefi ? "YES" : "NO")}");
                ui.WriteLine($"Secure Boot: {(config.SecureBoot ? "YES" : "NO")}");
                if (config.BiosEnabled && config.IncludeUefi) ui.WriteLine($"Boot prio. : {config.BootPriority}");
                ui.WriteLine($"Output     : {config.IsoOutputPath}");
                ui.WriteLine($"Volume ID  : {config.VolumeId}");
                if (config.ExcludePatterns.Count > 0) ui.WriteLine($"Excludes   : {string.Join(", ", config.ExcludePatterns)}");
                if (config.MaxSizeMb > 0) ui.WriteLine($"Max size   : {config.MaxSizeMb} MB");
                if (config.MaxFiles > 0) ui.WriteLine($"Max files  : {config.MaxFiles}");
                if (config.ManifestEnabled) ui.WriteLine("Manifest   : enabled");
                if (opts.DryRun) ui.WriteLine("Mode       : DRY RUN (no ISO will be written)");
                ui.WriteLine($"Plugins    : {(plugins.Plugins.Count > 0 ? string.Join(", ", plugins.Plugins.Select(p => p.Name)) : "none")}");
                ui.WriteLine("===========================");
            }

            if (!opts.NoPrompt && !opts.Silent)
            {
                ui.Write("Build ISO now? (Y/n): ");
                string c = Console.ReadLine() ?? string.Empty;
                string normalized = c.Trim().ToLowerInvariant();
                if (!PromptHelper.ParseYesNo(c) && (normalized == "n" || normalized == "no"))
                    return ExitError("Build cancelled by user.", ui, opts);
            }

            plugins.OnBeforeBuild(ctx);

            var pipeline = BuildPipeline.CreateDefault();

            foreach (var step in pipeline.Steps)
            {
                if (ctx.State.Failed || ctx.CancellationToken.IsCancellationRequested) break;

                if (opts.DryRun && step is StepVerifyIso or StepWriteReadme or StepWriteManifest)
                {
                    if (opts.Verbose) ui.WriteLine($"[STEP] {step.Name} (skipped: dry run)");
                    continue;
                }

                if (opts.Verbose) ui.WriteLine($"[STEP] {step.Name}");
                plugins.OnStepExecuting(ctx, step);
                try
                {
                    if (ctx.CancellationToken.IsCancellationRequested) { ctx.State.Fail("Build cancelled."); break; }
                    step.Execute(ctx);
                }
                catch (Exception ex)
                {
                    ctx.State.Fail($"Step {step.Name} failed: {ex.Message}");
                    ctx.Logger.Error($"Step {step.Name} exception: {ex}");
                }
                if (ctx.State.Failed) break;
                plugins.OnStepExecuted(ctx, step);
            }

            if (!string.IsNullOrEmpty(opts.JsonReportPath))
                BuildReportWriter.Write(opts.JsonReportPath, ctx);

            if (ctx.State.Failed)
            {
                logger.Save();
                return ExitError(ctx.State.FailureMessage, ui, opts);
            }

            if (!opts.Silent)
            {
                ui.WriteLine(string.Empty);
                if (opts.DryRun)
                {
                    ui.WriteLine("Dry run completed successfully. No ISO file was written.");
                    ui.WriteLine($"Files that would be added: {ctx.State.ProcessedFiles}");
                    ui.WriteLine($"Predicted size: {ctx.State.PredictedSizeMb} MB");
                }
                else
                {
                    ui.WriteLine("ISO created successfully:");
                    ui.WriteLine($"Path : {config.IsoOutputPath}");
                    ui.WriteLine($"Size : {(ctx.State.ActualSizeBytes / (1024.0 * 1024.0)).ToString("F2", CultureInfo.InvariantCulture)} MB");
                    ui.WriteLine($"Time : {ctx.State.BuildSeconds.ToString("F2", CultureInfo.InvariantCulture)} s");
                    ui.WriteLine($"Speed: {ctx.State.BuildSpeedMbPerSec.ToString("F2", CultureInfo.InvariantCulture)} MB/s");
                }
            }

            if (!opts.DryRun)
            {
                logger.Log($"ISO created: {config.IsoOutputPath}");
                logger.Log($"Size bytes: {ctx.State.ActualSizeBytes}");
                logger.Log($"Time: {ctx.State.BuildSeconds.ToString("F2", CultureInfo.InvariantCulture)} s");
                logger.Log($"Speed: {ctx.State.BuildSpeedMbPerSec.ToString("F2", CultureInfo.InvariantCulture)} MB/s");
            }
            else
            {
                logger.Log("Dry run completed successfully.");
            }
            logger.Save();

            plugins.OnAfterBuild(ctx);
            Pause(ui, opts);
            return 0;
        }
        catch (Exception ex)
        {
            try { Console.Error.WriteLine($"Fatal error: {ex}"); } catch { }
            Console.WriteLine($"Fatal error: {ex.Message}");
            return 1;
        }
    }

    static string ResolveProjectDir(IsoBuilderOptions opts, ConsoleUI ui)
    {
        if (!string.IsNullOrWhiteSpace(opts.ProjectDir)) return opts.ProjectDir.Trim();
        if (opts.Silent || opts.NoPrompt) return string.Empty;
        ui.WriteLine("Enter project path:");
        ui.Write("> ");
        return (Console.ReadLine() ?? string.Empty).Trim();
    }

    static int RunInit()
    {
        try
        {
            string dir = Directory.GetCurrentDirectory();
            string isoRoot = Path.Combine(dir, "iso_root");
            string bootDir = Path.Combine(dir, "boot");
            string bootAsm = Path.Combine(bootDir, "boot.asm");

            if (!Directory.Exists(isoRoot)) Directory.CreateDirectory(isoRoot);
            if (!Directory.Exists(bootDir)) Directory.CreateDirectory(bootDir);
            if (!File.Exists(bootAsm)) File.WriteAllText(bootAsm, string.Empty, BuildLogger.Utf8NoBom);

            Console.WriteLine("Initialized:");
            Console.WriteLine(" iso_root/");
            Console.WriteLine(" boot/");
            Console.WriteLine(" boot/boot.asm");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine($"Error: {ex.Message}"); return 1; }
    }

    static void PrintHelp()
    {
        Console.WriteLine($"BuildIso {BuildPaths.AppVersion}");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  BuildIso init");
        Console.WriteLine("  BuildIso -p <project> [options]");
        Console.WriteLine();
        Console.WriteLine("Structure:");
        Console.WriteLine("  -p, --project <dir>");
        Console.WriteLine("  -b, --boot <dir>");
        Console.WriteLine("  -i, --iso <dir>");
        Console.WriteLine("  -o, --output <file>");
        Console.WriteLine("  -V, --volume-id <name>");
        Console.WriteLine();
        Console.WriteLine("Boot:");
        Console.WriteLine("  --uefi              Add UEFI boot (in addition to BIOS)");
        Console.WriteLine("  --secureboot        Secure Boot note (requires --uefi)");
        Console.WriteLine("  --bios-only         BIOS-only boot");
        Console.WriteLine("  --uefi-only         UEFI-only boot");
        Console.WriteLine("  --boot-priority <uefi|bios|auto>  Which boot image wins when both are present");
        Console.WriteLine();
        Console.WriteLine("Guards:");
        Console.WriteLine("  --max-size <mb>     Abort if predicted ISO size exceeds this limit");
        Console.WriteLine("  --max-files <n>     Abort if scanned file count exceeds this limit");
        Console.WriteLine("  --timeout <seconds> Abort the build if it runs longer than this");
        Console.WriteLine("  --exclude <glob>    Exclude files/directories matching a glob pattern (repeatable)");
        Console.WriteLine();
        Console.WriteLine("Plugins:");
        Console.WriteLine("  --plugins-off               Disable all plugins");
        Console.WriteLine("  -Pu, --plugin <name>        Allow specific plugin by name");
        Console.WriteLine("  --plugin-hash <name>=<sha256>  Pin an allow-listed plugin to an exact hash");
        Console.WriteLine();
        Console.WriteLine("Output:");
        Console.WriteLine("  --dry-run            Validate the build without writing the ISO");
        Console.WriteLine("  --manifest           Write a MANIFEST.SHA256SUMS file next to the ISO");
        Console.WriteLine("  --json-report <file> Write a JSON build report to this path");
        Console.WriteLine("  --config <file>      Load default options from a JSON config file");
        Console.WriteLine();
        Console.WriteLine("Behavior:");
        Console.WriteLine("  --silent        Suppress all output");
        Console.WriteLine("  --no-prompt     Skip interactive prompts");
        Console.WriteLine("  --no-spinner    Disable spinner");
        Console.WriteLine("  --no-progress   Disable progress indicators");
        Console.WriteLine("  --verbose       Verbose step output");
        Console.WriteLine();
        Console.WriteLine("Meta:");
        Console.WriteLine("  init            Initialize project structure in current directory");
        Console.WriteLine("  version         Print version");
        Console.WriteLine("  help            Print this help");
    }

    static int ExitError(string msg, ConsoleUI ui, IsoBuilderOptions opts)
    {
        if (!opts.Silent) ui.WriteLine($"Error: {msg}");
        try { Console.Error.WriteLine($"Error: {msg}"); } catch { }
        Pause(ui, opts);
        return 1;
    }

    static void Pause(ConsoleUI ui, IsoBuilderOptions opts)
    {
        if (opts.Silent || opts.NoPrompt) return;
        ui.WriteLine(string.Empty);
        ui.WriteLine("Press ENTER to exit...");
        Console.ReadLine();
    }
}

static class BuildConfigFactory
{
    public static BuildConfig? Create(IsoBuilderOptions opts, string projectDir, ConsoleUI ui, BuildLogger logger)
    {
        string bootDir = !string.IsNullOrEmpty(opts.BootDir)
            ? Path.GetFullPath(opts.BootDir)
            : Path.GetFullPath(Path.Combine(projectDir, "boot"));

        string isoDir = !string.IsNullOrEmpty(opts.IsoDir)
            ? Path.GetFullPath(opts.IsoDir)
            : Path.GetFullPath(Path.Combine(projectDir, "iso_root"));

        string bootBinPath = Path.Combine(bootDir, BuildPaths.BootBinFilename);
        string efiImgPath = Path.Combine(bootDir, BuildPaths.EfiImgFilename);

        string isoOutputPath = !string.IsNullOrEmpty(opts.Output)
            ? Path.GetFullPath(opts.Output)
            : Path.GetFullPath(Path.Combine(projectDir, "output.iso"));

        string volSource = !string.IsNullOrEmpty(opts.VolumeId)
            ? opts.VolumeId
            : (Path.GetFileName(projectDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? "BUILDISO");

        string volumeId = VolumeIdHelper.Normalize(volSource, logger);
        BootPriorityMode bootPriority = ParseBootPriority(opts.BootPriority);

        bool includeUefi, biosEnabled;
        if (opts.UefiOnly) { includeUefi = true; biosEnabled = false; }
        else if (opts.BiosOnly) { includeUefi = false; biosEnabled = true; }
        else if (opts.Uefi) { includeUefi = true; biosEnabled = true; }
        else { includeUefi = false; biosEnabled = true; }

        if (!opts.NoPrompt && !opts.Silent && !opts.BiosOnly && !opts.UefiOnly && !opts.Uefi)
        {
            ui.Write("Include UEFI? (y/N): ");
            string u = Console.ReadLine() ?? string.Empty;
            includeUefi = PromptHelper.ParseYesNo(u);
            biosEnabled = true;

            if (includeUefi && !File.Exists(efiImgPath))
            {
                if (!opts.Silent) ui.WriteLine($"Error: UEFI selected but efiboot.img not found: {efiImgPath}");
                return null;
            }

            if (includeUefi && !opts.SecureBoot)
            {
                ui.Write("Enable Secure Boot? (y/N): ");
                string s = Console.ReadLine() ?? string.Empty;
                bool sb = PromptHelper.ParseYesNo(s);
                return BuildAndValidate(opts, projectDir, bootDir, isoDir, bootBinPath, efiImgPath,
                    isoOutputPath, volumeId, includeUefi, biosEnabled, sb, bootPriority, ui, logger);
            }
        }

        bool secureBoot = opts.SecureBoot && includeUefi;
        if (opts.SecureBoot && !includeUefi)
            logger.Warn("Secure Boot requested via --secureboot but UEFI is disabled; ignoring Secure Boot.");

        return BuildAndValidate(opts, projectDir, bootDir, isoDir, bootBinPath, efiImgPath,
            isoOutputPath, volumeId, includeUefi, biosEnabled, secureBoot, bootPriority, ui, logger);
    }

    static BootPriorityMode ParseBootPriority(string value) => value.Trim().ToLowerInvariant() switch
    {
        "uefi" => BootPriorityMode.PreferUefi,
        "bios" => BootPriorityMode.PreferBios,
        _ => BootPriorityMode.Auto
    };

    static BuildConfig? BuildAndValidate(
        IsoBuilderOptions opts, string projectDir, string bootDir, string isoDir,
        string bootBinPath, string efiImgPath, string isoOutputPath,
        string volumeId, bool includeUefi, bool biosEnabled, bool secureBoot,
        BootPriorityMode bootPriority, ConsoleUI ui, BuildLogger logger)
    {
        if (!Directory.Exists(bootDir))
        {
            ui.WriteLine($"Error: Boot directory not found: {bootDir}");
            return null;
        }
        if (!Directory.Exists(isoDir))
        {
            ui.WriteLine($"Error: ISO root directory not found: {isoDir}");
            return null;
        }
        if (biosEnabled && !File.Exists(bootBinPath))
        {
            ui.WriteLine($"Error: boot.bin not found: {bootBinPath}");
            return null;
        }
        if (includeUefi && !File.Exists(efiImgPath))
        {
            ui.WriteLine($"Error: efiboot.img not found: {efiImgPath}");
            return null;
        }

        string? outputParent = Path.GetDirectoryName(isoOutputPath);
        if (!string.IsNullOrEmpty(outputParent) && !Directory.Exists(outputParent))
        {
            try { Directory.CreateDirectory(outputParent); }
            catch (Exception ex) { ui.WriteLine($"Error: Cannot create output directory: {ex.Message}"); return null; }
        }

        return new BuildConfig
        {
            ProjectDir = projectDir,
            BootDir = bootDir,
            IsoDir = isoDir,
            BootBinPath = bootBinPath,
            EfiImgPath = efiImgPath,
            IsoOutputPath = isoOutputPath,
            VolumeId = volumeId,
            IncludeUefi = includeUefi,
            BiosEnabled = biosEnabled,
            SecureBoot = secureBoot,
            BootPriority = bootPriority,
            ExcludePatterns = opts.ExcludePatterns,
            MaxSizeMb = opts.MaxSizeMb,
            MaxFiles = opts.MaxFiles,
            ManifestEnabled = opts.Manifest
        };
    }
}
