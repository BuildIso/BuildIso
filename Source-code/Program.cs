#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using DiscUtils.Iso9660;
using System.Reflection.Metadata;

enum BootImageType
{
    Unknown = 0,
    BiosBootSector512 = 1,
    EfiImage = 2,
    FatEspImage = 3
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

enum BuildStatus
{
    Success = 0,
    Failed = 1,
    Cancelled = 2,
    ValidationFailed = 3
}

enum PathCollisionMode
{
    Rename = 0,
    Fail = 1,
    Skip = 2
}

enum IssueSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2
}

static class ExitCodes
{
    public const int Success = 0;
    public const int Error = 1;
    public const int InvalidArguments = 2;
    public const int PreflightFailed = 3;
    public const int ValidationFailed = 4;
    public const int Cancelled = 5;
    public const int BootTestFailed = 6;
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

sealed record IsoBuilderOptions
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
    public bool Check { get; init; }
    public bool Strict { get; init; }
    public bool FixSafe { get; init; }
    public bool Reproducible { get; init; }
    public bool NoOverwrite { get; init; }
    public bool KeepBackup { get; init; }
    public bool NoLog { get; init; }
    public bool DeepVerify { get; init; }
    public bool AllowUnknownBoot { get; init; }
    public bool RequirePluginHash { get; init; }
    public bool PluginStrict { get; init; }
    public bool BootTest { get; init; }
    public string BootPriority { get; init; } = "auto";
    public string Profile { get; init; } = string.Empty;
    public string ReportFormat { get; init; } = string.Empty;
    public string ExpectSha256 { get; init; } = string.Empty;
    public string CollisionPolicy { get; init; } = string.Empty;
    public string CachePath { get; init; } = string.Empty;
    public string LogPath { get; init; } = string.Empty;
    public string QemuPath { get; init; } = string.Empty;
    public string OvmfPath { get; init; } = string.Empty;
    public string InspectPath { get; init; } = string.Empty;
    public string VerifyIsoPath { get; init; } = string.Empty;
    public long MaxSizeMb { get; init; }
    public long MaxFileSizeMb { get; init; }
    public long SourceDateEpoch { get; init; } = -1;
    public int MaxFiles { get; init; }
    public int MaxDepth { get; init; }
    public int TimeoutSeconds { get; init; }
    public int Threads { get; init; }
    public int PluginTimeoutSeconds { get; init; }
    public int BootTestTimeoutSeconds { get; init; }
    public string JsonReportPath { get; init; } = string.Empty;
    public string ConfigPath { get; init; } = string.Empty;
    public IReadOnlyList<string> PluginAllowList { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ExcludePatterns { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> IncludePatterns { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> PluginHashPins { get; init; } = new Dictionary<string, string>();
    public IReadOnlySet<string> Explicit { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public static IsoBuilderOptions Parse(string[] args)
    {
        string project = string.Empty, output = string.Empty, bootDir = string.Empty,
               isoDir = string.Empty, volumeId = string.Empty, bootPriority = "auto",
               jsonReportPath = string.Empty, configPath = string.Empty, profile = string.Empty,
               reportFormat = string.Empty, expectSha256 = string.Empty, collisionPolicy = string.Empty,
               cachePath = string.Empty, logPath = string.Empty, qemuPath = string.Empty,
               ovmfPath = string.Empty, inspectPath = string.Empty, verifyIsoPath = string.Empty;
        bool uefi = false, secureBoot = false, silent = false, noPrompt = false,
             noSpinner = false, noProgress = false, biosOnly = false, uefiOnly = false,
             init = false, version = false, help = false, verbose = false, pluginsOff = false,
             dryRun = false, manifest = false, check = false, strict = false, fixSafe = false,
             reproducible = false, noOverwrite = false, keepBackup = false, noLog = false,
             deepVerify = false, allowUnknownBoot = false, requirePluginHash = false,
             pluginStrict = false, bootTest = false;
        long maxSizeMb = 0, maxFileSizeMb = 0, sourceDateEpoch = -1;
        int maxFiles = 0, timeoutSeconds = 0, maxDepth = 0, threads = 0,
            pluginTimeoutSeconds = 0, bootTestTimeoutSeconds = 0;
        var allowList = new List<string>();
        var excludeList = new List<string>();
        var includeList = new List<string>();
        var pluginHashPins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var explicitKeys = new HashSet<string>(StringComparer.Ordinal);
        var errors = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string raw = (args[i] ?? string.Empty).Trim();
            if (raw.Length == 0) continue;
            int eq = raw.IndexOf('=');
            bool hasEmb = eq > 0;
            string key = hasEmb ? raw[..eq] : raw;
            string emb = hasEmb ? raw[(eq + 1)..].Trim().Trim('"') : string.Empty;

            string Next()
            {
                if (hasEmb)
                {
                    if (emb.Length == 0) errors.Add($"Missing value for argument: {key}");
                    return emb;
                }
                if (i + 1 < args.Length)
                {
                    i++;
                    return (args[i] ?? string.Empty).Trim().Trim('"');
                }
                errors.Add($"Missing value for argument: {key}");
                return string.Empty;
            }

            bool Flag()
            {
                if (!hasEmb) return true;
                switch (emb.ToLowerInvariant())
                {
                    case "true": case "1": case "yes": case "on": return true;
                    case "false": case "0": case "no": case "off": return false;
                    default:
                        errors.Add($"Invalid boolean value for {key}: {emb}");
                        return false;
                }
            }

            bool Num(long min, long max, out long value)
            {
                string text = Next();
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) && parsed >= min && parsed <= max)
                {
                    value = parsed;
                    return true;
                }
                if (!string.IsNullOrEmpty(text)) errors.Add($"Invalid value for {key}: {text}");
                value = 0;
                return false;
            }

            switch (key)
            {
                case "init": init = true; break;
                case "version": case "--version": version = true; break;
                case "help": case "-h": case "--help": help = true; break;
                case "--uefi": uefi = Flag(); explicitKeys.Add("uefi"); break;
                case "--secureboot": secureBoot = Flag(); explicitKeys.Add("secureboot"); break;
                case "--silent": silent = Flag(); explicitKeys.Add("silent"); break;
                case "--no-prompt": noPrompt = Flag(); explicitKeys.Add("noprompt"); break;
                case "--no-spinner": noSpinner = Flag(); explicitKeys.Add("nospinner"); break;
                case "--no-progress": noProgress = Flag(); explicitKeys.Add("noprogress"); break;
                case "--bios-only": biosOnly = Flag(); explicitKeys.Add("biosonly"); break;
                case "--uefi-only":
                    uefiOnly = Flag();
                    explicitKeys.Add("uefionly");
                    if (uefiOnly) { uefi = true; explicitKeys.Add("uefi"); }
                    break;
                case "--verbose": verbose = Flag(); explicitKeys.Add("verbose"); break;
                case "--plugins-off": pluginsOff = Flag(); explicitKeys.Add("pluginsoff"); break;
                case "--dry-run": dryRun = Flag(); explicitKeys.Add("dryrun"); break;
                case "--manifest": manifest = Flag(); explicitKeys.Add("manifest"); break;
                case "--check": check = Flag(); explicitKeys.Add("check"); break;
                case "--strict": strict = Flag(); explicitKeys.Add("strict"); break;
                case "--fix-safe": fixSafe = Flag(); explicitKeys.Add("fixsafe"); break;
                case "--reproducible": reproducible = Flag(); explicitKeys.Add("reproducible"); break;
                case "--no-overwrite": noOverwrite = Flag(); explicitKeys.Add("nooverwrite"); break;
                case "--keep-backup": keepBackup = Flag(); explicitKeys.Add("keepbackup"); break;
                case "--no-log": noLog = Flag(); explicitKeys.Add("nolog"); break;
                case "--deep-verify": deepVerify = Flag(); explicitKeys.Add("deepverify"); break;
                case "--allow-unknown-boot": allowUnknownBoot = Flag(); explicitKeys.Add("allowunknownboot"); break;
                case "--require-plugin-hash": requirePluginHash = Flag(); explicitKeys.Add("requirepluginhash"); break;
                case "--plugin-strict": pluginStrict = Flag(); explicitKeys.Add("pluginstrict"); break;
                case "--boot-test": bootTest = Flag(); explicitKeys.Add("boottest"); break;
                case "-Pu":
                case "--plugin":
                {
                    string pluginName = Next();
                    if (!string.IsNullOrWhiteSpace(pluginName)) allowList.Add(pluginName.Trim());
                    break;
                }
                case "--plugin-hash":
                {
                    string pinRaw = Next();
                    int sep = pinRaw.IndexOf('=');
                    if (sep > 0 && sep < pinRaw.Length - 1)
                        pluginHashPins[pinRaw[..sep].Trim()] = pinRaw[(sep + 1)..].Trim().ToLowerInvariant();
                    else if (!string.IsNullOrEmpty(pinRaw))
                        errors.Add($"Invalid value for --plugin-hash, expected Name=sha256: {pinRaw}");
                    break;
                }
                case "--exclude":
                {
                    string excludeGlob = Next();
                    if (!string.IsNullOrWhiteSpace(excludeGlob)) excludeList.Add(excludeGlob.Trim());
                    break;
                }
                case "--include":
                {
                    string includeGlob = Next();
                    if (!string.IsNullOrWhiteSpace(includeGlob)) includeList.Add(includeGlob.Trim());
                    break;
                }
                case "--max-size":
                    { if (Num(1, 100_000_000, out long v)) { maxSizeMb = v; explicitKeys.Add("maxsize"); } break; }
                case "--max-file-size":
                    { if (Num(1, 100_000_000, out long v)) { maxFileSizeMb = v; explicitKeys.Add("maxfilesize"); } break; }
                case "--max-files":
                    { if (Num(1, int.MaxValue, out long v)) { maxFiles = (int)v; explicitKeys.Add("maxfiles"); } break; }
                case "--max-depth":
                    { if (Num(1, 1024, out long v)) { maxDepth = (int)v; explicitKeys.Add("maxdepth"); } break; }
                case "--timeout":
                    { if (Num(1, 31_536_000, out long v)) { timeoutSeconds = (int)v; explicitKeys.Add("timeout"); } break; }
                case "--threads":
                    { if (Num(1, 64, out long v)) { threads = (int)v; explicitKeys.Add("threads"); } break; }
                case "--plugin-timeout":
                    { if (Num(1, 3600, out long v)) { pluginTimeoutSeconds = (int)v; explicitKeys.Add("plugintimeout"); } break; }
                case "--boot-test-timeout":
                    { if (Num(1, 3600, out long v)) { bootTestTimeoutSeconds = (int)v; explicitKeys.Add("boottesttimeout"); } break; }
                case "--source-date-epoch":
                    { if (Num(0, 4_102_444_799L, out long v)) { sourceDateEpoch = v; explicitKeys.Add("sourcedateepoch"); } break; }
                case "--boot-priority":
                {
                    string priorityRaw = Next().Trim().ToLowerInvariant();
                    if (priorityRaw is "uefi" or "bios" or "auto") { bootPriority = priorityRaw; explicitKeys.Add("bootpriority"); }
                    else if (!string.IsNullOrEmpty(priorityRaw)) errors.Add($"Invalid value for --boot-priority: {priorityRaw}");
                    break;
                }
                case "--collision-policy":
                {
                    string policyRaw = Next().Trim().ToLowerInvariant();
                    if (policyRaw is "rename" or "fail" or "skip") collisionPolicy = policyRaw;
                    else if (!string.IsNullOrEmpty(policyRaw)) errors.Add($"Invalid value for --collision-policy: {policyRaw}");
                    break;
                }
                case "--report-format":
                {
                    string formatRaw = Next().Trim().ToLowerInvariant();
                    if (formatRaw is "json" or "junit" or "sarif") reportFormat = formatRaw;
                    else if (!string.IsNullOrEmpty(formatRaw)) errors.Add($"Invalid value for --report-format: {formatRaw}");
                    break;
                }
                case "--profile": profile = Next().Trim().ToLowerInvariant(); break;
                case "--expect-sha256": expectSha256 = Next().Trim().ToLowerInvariant(); break;
                case "--cache": cachePath = Next(); break;
                case "--log": logPath = Next(); break;
                case "--qemu": qemuPath = Next(); break;
                case "--ovmf": ovmfPath = Next(); break;
                case "--inspect": inspectPath = Next(); break;
                case "--verify-iso": verifyIsoPath = Next(); break;
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
            Check = check,
            Strict = strict,
            FixSafe = fixSafe,
            Reproducible = reproducible,
            NoOverwrite = noOverwrite,
            KeepBackup = keepBackup,
            NoLog = noLog,
            DeepVerify = deepVerify,
            AllowUnknownBoot = allowUnknownBoot,
            RequirePluginHash = requirePluginHash,
            PluginStrict = pluginStrict,
            BootTest = bootTest,
            BootPriority = bootPriority,
            Profile = profile,
            ReportFormat = reportFormat,
            ExpectSha256 = expectSha256,
            CollisionPolicy = collisionPolicy,
            CachePath = cachePath,
            LogPath = logPath,
            QemuPath = qemuPath,
            OvmfPath = ovmfPath,
            InspectPath = inspectPath,
            VerifyIsoPath = verifyIsoPath,
            MaxSizeMb = maxSizeMb,
            MaxFileSizeMb = maxFileSizeMb,
            SourceDateEpoch = sourceDateEpoch,
            MaxFiles = maxFiles,
            MaxDepth = maxDepth,
            TimeoutSeconds = timeoutSeconds,
            Threads = threads,
            PluginTimeoutSeconds = pluginTimeoutSeconds,
            BootTestTimeoutSeconds = bootTestTimeoutSeconds,
            JsonReportPath = jsonReportPath,
            ConfigPath = configPath,
            PluginAllowList = allowList.AsReadOnly(),
            ExcludePatterns = excludeList.AsReadOnly(),
            IncludePatterns = includeList.AsReadOnly(),
            PluginHashPins = pluginHashPins,
            Explicit = explicitKeys,
            Errors = errors.AsReadOnly(),
            Warnings = Array.Empty<string>()
        };
    }
}

sealed class FileConfigModel
{
    public string? Project { get; set; }
    public string? Output { get; set; }
    public string? BootDir { get; set; }
    public string? IsoDir { get; set; }
    public string? VolumeId { get; set; }
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
    public bool? Strict { get; set; }
    public bool? FixSafe { get; set; }
    public bool? Reproducible { get; set; }
    public bool? NoOverwrite { get; set; }
    public bool? KeepBackup { get; set; }
    public bool? NoLog { get; set; }
    public bool? DeepVerify { get; set; }
    public bool? AllowUnknownBoot { get; set; }
    public bool? RequirePluginHash { get; set; }
    public bool? PluginStrict { get; set; }
    public bool? BootTest { get; set; }
    public string? BootPriority { get; set; }
    public string? Profile { get; set; }
    public string? ReportFormat { get; set; }
    public string? ExpectSha256 { get; set; }
    public string? CollisionPolicy { get; set; }
    public string? Cache { get; set; }
    public string? Log { get; set; }
    public string? Qemu { get; set; }
    public string? Ovmf { get; set; }
    public long? MaxSizeMb { get; set; }
    public long? MaxFileSizeMb { get; set; }
    public long? SourceDateEpoch { get; set; }
    public int? MaxFiles { get; set; }
    public int? MaxDepth { get; set; }
    public int? TimeoutSeconds { get; set; }
    public int? Threads { get; set; }
    public int? PluginTimeoutSeconds { get; set; }
    public int? BootTestTimeoutSeconds { get; set; }
    public string? JsonReportPath { get; set; }
    public List<string>? PluginAllowList { get; set; } = new();
    public List<string>? Exclude { get; set; } = new();
    public List<string>? Include { get; set; } = new();
    public Dictionary<string, string>? PluginHashPins { get; set; } = new();
}

static class ProfileDefaults
{
    public static bool IsValid(string name)
    {
        string n = (name ?? string.Empty).Trim().ToLowerInvariant();
        return n.Length == 0 || n is "development" or "release" or "reproducible" or "ci";
    }

    public static IReadOnlyDictionary<string, bool> Get(string name)
    {
        var d = new Dictionary<string, bool>(StringComparer.Ordinal);
        switch ((name ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "development":
                d["verbose"] = true;
                break;
            case "release":
                d["manifest"] = true;
                d["deepverify"] = true;
                d["strict"] = true;
                d["requirepluginhash"] = true;
                break;
            case "reproducible":
                d["reproducible"] = true;
                d["manifest"] = true;
                d["deepverify"] = true;
                d["pluginsoff"] = true;
                break;
            case "ci":
                d["noprompt"] = true;
                d["nospinner"] = true;
                d["noprogress"] = true;
                d["manifest"] = true;
                d["strict"] = true;
                break;
        }
        return d;
    }
}

static class ConfigFileLoader
{
    const long MaxConfigBytes = 1024 * 1024;

    static FileConfigModel? Load(string path, List<string> errors)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                errors.Add($"Config file not found: {path}");
                return null;
            }
            if (info.Length > MaxConfigBytes)
            {
                errors.Add($"Config file is too large (limit {MaxConfigBytes} bytes): {path}");
                return null;
            }

            string json = File.ReadAllText(path, Encoding.UTF8);
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                MaxDepth = 16,
                UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
            };
            FileConfigModel? model = JsonSerializer.Deserialize<FileConfigModel>(json, options);
            if (model == null)
            {
                errors.Add($"Config file is empty: {path}");
                return null;
            }
            return model;
        }
        catch (Exception ex)
        {
            errors.Add($"Failed to parse config file {path}: {ex.Message}");
            return null;
        }
    }

    public static IsoBuilderOptions Merge(IsoBuilderOptions cli)
    {
        var errors = new List<string>(cli.Errors);
        var warnings = new List<string>(cli.Warnings);
        var fc = new FileConfigModel();

        if (!string.IsNullOrWhiteSpace(cli.ConfigPath))
        {
            FileConfigModel? loaded = Load(cli.ConfigPath, errors);
            if (loaded != null) fc = loaded;
        }

        string profileName = !string.IsNullOrEmpty(cli.Profile) ? cli.Profile : (fc.Profile ?? string.Empty).Trim().ToLowerInvariant();
        if (!ProfileDefaults.IsValid(profileName))
            errors.Add($"Unknown profile: {profileName} (expected development, release, reproducible or ci).");
        IReadOnlyDictionary<string, bool> profile = ProfileDefaults.Get(profileName);

        var explicitKeys = new HashSet<string>(cli.Explicit, StringComparer.Ordinal);

        bool B(string key, bool cliValue, bool? fileValue)
        {
            if (cli.Explicit.Contains(key)) return cliValue;
            if (fileValue.HasValue)
            {
                explicitKeys.Add(key);
                return fileValue.Value;
            }
            if (profile.TryGetValue(key, out bool profileValue)) return profileValue;
            return false;
        }

        string S(string cliValue, string? fileValue) => !string.IsNullOrEmpty(cliValue) ? cliValue : (fileValue ?? string.Empty);

        var mergedPlugins = new List<string>(cli.PluginAllowList);
        foreach (string p in fc.PluginAllowList ?? new List<string>())
            if (!string.IsNullOrWhiteSpace(p) && !mergedPlugins.Contains(p, StringComparer.OrdinalIgnoreCase)) mergedPlugins.Add(p.Trim());

        var mergedExcludes = new List<string>(cli.ExcludePatterns);
        foreach (string p in fc.Exclude ?? new List<string>())
            if (!string.IsNullOrWhiteSpace(p) && !mergedExcludes.Contains(p, StringComparer.Ordinal)) mergedExcludes.Add(p.Trim());

        var mergedIncludes = new List<string>(cli.IncludePatterns);
        foreach (string p in fc.Include ?? new List<string>())
            if (!string.IsNullOrWhiteSpace(p) && !mergedIncludes.Contains(p, StringComparer.Ordinal)) mergedIncludes.Add(p.Trim());

        var mergedPins = new Dictionary<string, string>(cli.PluginHashPins, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in fc.PluginHashPins ?? new Dictionary<string, string>())
            if (!string.IsNullOrWhiteSpace(kv.Key) && !mergedPins.ContainsKey(kv.Key))
                mergedPins[kv.Key] = (kv.Value ?? string.Empty).Trim().ToLowerInvariant();

        string bootPriority = cli.Explicit.Contains("bootpriority")
            ? cli.BootPriority
            : (string.IsNullOrWhiteSpace(fc.BootPriority) ? cli.BootPriority : fc.BootPriority.Trim().ToLowerInvariant());
        if (!(bootPriority is "uefi" or "bios" or "auto"))
        {
            errors.Add($"Invalid boot priority: {bootPriority}");
            bootPriority = "auto";
        }

        string reportFormat = S(cli.ReportFormat, fc.ReportFormat).Trim().ToLowerInvariant();
        if (reportFormat.Length == 0) reportFormat = "json";
        string collisionPolicy = S(cli.CollisionPolicy, fc.CollisionPolicy).Trim().ToLowerInvariant();
        if (collisionPolicy.Length == 0) collisionPolicy = "rename";

        var merged = cli with
        {
            ProjectDir = S(cli.ProjectDir, fc.Project),
            Output = S(cli.Output, fc.Output),
            BootDir = S(cli.BootDir, fc.BootDir),
            IsoDir = S(cli.IsoDir, fc.IsoDir),
            VolumeId = S(cli.VolumeId, fc.VolumeId),
            Uefi = B("uefi", cli.Uefi, fc.Uefi),
            SecureBoot = B("secureboot", cli.SecureBoot, fc.SecureBoot),
            Silent = B("silent", cli.Silent, fc.Silent),
            NoPrompt = B("noprompt", cli.NoPrompt, fc.NoPrompt),
            NoSpinner = B("nospinner", cli.NoSpinner, fc.NoSpinner),
            NoProgress = B("noprogress", cli.NoProgress, fc.NoProgress),
            BiosOnly = B("biosonly", cli.BiosOnly, fc.BiosOnly),
            UefiOnly = B("uefionly", cli.UefiOnly, fc.UefiOnly),
            Verbose = B("verbose", cli.Verbose, fc.Verbose),
            PluginsOff = B("pluginsoff", cli.PluginsOff, fc.PluginsOff),
            DryRun = B("dryrun", cli.DryRun, fc.DryRun),
            Manifest = B("manifest", cli.Manifest, fc.Manifest),
            Check = cli.Check,
            Strict = B("strict", cli.Strict, fc.Strict),
            FixSafe = B("fixsafe", cli.FixSafe, fc.FixSafe),
            Reproducible = B("reproducible", cli.Reproducible, fc.Reproducible),
            NoOverwrite = B("nooverwrite", cli.NoOverwrite, fc.NoOverwrite),
            KeepBackup = B("keepbackup", cli.KeepBackup, fc.KeepBackup),
            NoLog = B("nolog", cli.NoLog, fc.NoLog),
            DeepVerify = B("deepverify", cli.DeepVerify, fc.DeepVerify),
            AllowUnknownBoot = B("allowunknownboot", cli.AllowUnknownBoot, fc.AllowUnknownBoot),
            RequirePluginHash = B("requirepluginhash", cli.RequirePluginHash, fc.RequirePluginHash),
            PluginStrict = B("pluginstrict", cli.PluginStrict, fc.PluginStrict),
            BootTest = B("boottest", cli.BootTest, fc.BootTest),
            BootPriority = bootPriority,
            Profile = profileName,
            ReportFormat = reportFormat,
            ExpectSha256 = S(cli.ExpectSha256, fc.ExpectSha256).Trim().ToLowerInvariant(),
            CollisionPolicy = collisionPolicy,
            CachePath = S(cli.CachePath, fc.Cache),
            LogPath = S(cli.LogPath, fc.Log),
            QemuPath = S(cli.QemuPath, fc.Qemu),
            OvmfPath = S(cli.OvmfPath, fc.Ovmf),
            MaxSizeMb = cli.MaxSizeMb > 0 ? cli.MaxSizeMb : (fc.MaxSizeMb ?? 0),
            MaxFileSizeMb = cli.MaxFileSizeMb > 0 ? cli.MaxFileSizeMb : (fc.MaxFileSizeMb ?? 0),
            SourceDateEpoch = cli.SourceDateEpoch >= 0 ? cli.SourceDateEpoch : (fc.SourceDateEpoch ?? -1),
            MaxFiles = cli.MaxFiles > 0 ? cli.MaxFiles : (fc.MaxFiles ?? 0),
            MaxDepth = cli.MaxDepth > 0 ? cli.MaxDepth : (fc.MaxDepth ?? 0),
            TimeoutSeconds = cli.TimeoutSeconds > 0 ? cli.TimeoutSeconds : (fc.TimeoutSeconds ?? 0),
            Threads = cli.Threads > 0 ? cli.Threads : (fc.Threads ?? 0),
            PluginTimeoutSeconds = cli.PluginTimeoutSeconds > 0 ? cli.PluginTimeoutSeconds : (fc.PluginTimeoutSeconds ?? 0),
            BootTestTimeoutSeconds = cli.BootTestTimeoutSeconds > 0 ? cli.BootTestTimeoutSeconds : (fc.BootTestTimeoutSeconds ?? 0),
            JsonReportPath = S(cli.JsonReportPath, fc.JsonReportPath),
            PluginAllowList = mergedPlugins.AsReadOnly(),
            ExcludePatterns = mergedExcludes.AsReadOnly(),
            IncludePatterns = mergedIncludes.AsReadOnly(),
            PluginHashPins = mergedPins,
            Explicit = explicitKeys,
            Errors = errors.AsReadOnly(),
            Warnings = warnings.AsReadOnly()
        };

        var validationErrors = new List<string>();
        var validationWarnings = new List<string>();
        OptionsValidator.Validate(merged, validationErrors, validationWarnings);
        errors.AddRange(validationErrors);
        warnings.AddRange(validationWarnings);

        return merged with { Errors = errors.AsReadOnly(), Warnings = warnings.AsReadOnly() };
    }
}

static class OptionsValidator
{
    static readonly Regex Sha256Pattern = new("^[0-9a-f]{64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsSha256(string value) => !string.IsNullOrEmpty(value) && Sha256Pattern.IsMatch(value);

    public static void Validate(IsoBuilderOptions o, List<string> errors, List<string> warnings)
    {
        if (o.BiosOnly && o.UefiOnly) errors.Add("--bios-only and --uefi-only cannot be used together.");
        if (o.SecureBoot && o.BiosOnly) warnings.Add("--secureboot has no effect together with --bios-only.");
        if (o.Check && o.DryRun) warnings.Add("--check already implies a no-write run; --dry-run adds the ISO layout simulation only without --check.");
        if (o.BootTest && (o.DryRun || o.Check)) warnings.Add("--boot-test is ignored in --dry-run and --check modes.");
        if (o.BootTest && o.BiosOnly == false && o.UefiOnly == false && string.IsNullOrEmpty(o.OvmfPath) && o.Uefi)
            warnings.Add("--boot-test without --ovmf cannot test UEFI boot; only BIOS boot will be tested.");
        if (o.PluginsOff && (o.PluginAllowList.Count > 0 || o.PluginHashPins.Count > 0))
            warnings.Add("Plugins are disabled; plugin allow-list and hash pins are ignored.");
        if (o.RequirePluginHash && o.PluginsOff) warnings.Add("--require-plugin-hash has no effect when plugins are disabled.");
        if (!string.IsNullOrEmpty(o.ExpectSha256) && !IsSha256(o.ExpectSha256)) errors.Add("--expect-sha256 must be a 64 character lowercase hexadecimal SHA-256 value.");
        if (!string.IsNullOrEmpty(o.ExpectSha256) && (o.DryRun || o.Check)) warnings.Add("--expect-sha256 is ignored in --dry-run and --check modes.");
        if (!string.IsNullOrEmpty(o.ReportFormat) && !(o.ReportFormat is "json" or "junit" or "sarif")) errors.Add($"Invalid report format: {o.ReportFormat}");
        if (!string.IsNullOrEmpty(o.CollisionPolicy) && !(o.CollisionPolicy is "rename" or "fail" or "skip")) errors.Add($"Invalid collision policy: {o.CollisionPolicy}");
        if (o.SourceDateEpoch > 4_102_444_799L) errors.Add("--source-date-epoch is out of the supported range.");
        if (o.SourceDateEpoch >= 0 && !o.Reproducible) warnings.Add("--source-date-epoch has no effect without --reproducible.");
        if (!string.IsNullOrEmpty(o.ReportFormat) && o.ReportFormat != "json" && string.IsNullOrEmpty(o.JsonReportPath))
            warnings.Add("--report-format has no effect without --json-report.");
        if (o.Threads < 0 || o.Threads > 64) errors.Add("--threads must be between 1 and 64.");

        foreach (var pin in o.PluginHashPins)
        {
            if (string.IsNullOrWhiteSpace(pin.Key)) errors.Add("--plugin-hash requires a plugin name.");
            else if (!IsSha256(pin.Value)) errors.Add($"Plugin hash pin for '{pin.Key}' must be a 64 character hexadecimal SHA-256 value.");
        }

        if (!string.IsNullOrEmpty(o.InspectPath) && !string.IsNullOrEmpty(o.VerifyIsoPath))
            errors.Add("--inspect and --verify-iso cannot be used together.");
    }
}
sealed class PathComparer : IEqualityComparer<string>
{
    public static readonly PathComparer Instance = new();

    static readonly StringComparer Inner = (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public bool Equals(string? x, string? y) => Inner.Equals(x, y);
    public int GetHashCode(string obj) => Inner.GetHashCode(obj);
}

static class TextSanitizer
{
    public static string Clean(string? input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        bool dirty = false;
        foreach (char c in input)
            if (char.IsControl(c)) { dirty = true; break; }
        if (!dirty) return input;

        var sb = new StringBuilder(input.Length);
        foreach (char c in input)
            sb.Append(char.IsControl(c) ? ' ' : c);
        return sb.ToString();
    }

    public static string FormatBytes(double bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int unit = 0;
        double value = Math.Max(0, bytes);
        while (value >= 1024.0 && unit < units.Length - 1)
        {
            value /= 1024.0;
            unit++;
        }
        return unit == 0
            ? $"{value.ToString("F0", CultureInfo.InvariantCulture)} {units[unit]}"
            : $"{value.ToString("F1", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    public static string FormatDuration(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) return "--";
        if (seconds < 60) return $"{Math.Ceiling(seconds).ToString("F0", CultureInfo.InvariantCulture)} s";
        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h{ts.Minutes:D2}m";
        return $"{ts.Minutes}m{ts.Seconds:D2}s";
    }
}

static class GlobMatcher
{
    static readonly ConcurrentDictionary<string, Regex> Cache = new(StringComparer.Ordinal);

    static Regex Compile(string pattern) => Cache.GetOrAdd(pattern, p =>
        new Regex(
            "^" + Regex.Escape(p.Replace('\\', '/')).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(2)));

    public static bool IsMatch(string input, string pattern)
    {
        if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(input)) return false;
        try { return Compile(pattern).IsMatch(input); }
        catch (RegexMatchTimeoutException) { return false; }
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
    const int MaxAttempts = 3;
    const int BufferSize = 1024 * 1024;

    public static string ComputeSha256(string path) => ComputeSha256(path, CancellationToken.None, null);

    public static string ComputeSha256(string path, CancellationToken ct, Action<long>? onProgress = null)
    {
        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
                return ComputeSha256(fs, ct, onProgress);
            }
            catch (OperationCanceledException) { throw; }
            catch (IOException) when (attempt < MaxAttempts - 1)
            {
                Thread.Sleep(100 * (attempt + 1));
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        return string.Empty;
    }

    public static string ComputeSha256(Stream stream, CancellationToken ct, Action<long>? onProgress = null)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[BufferSize];
        long total = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            hasher.AppendData(buffer, 0, read);
            total += read;
            onProgress?.Invoke(total);
        }
        return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
    }

    public static string ComputeSha256Bytes(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static string Truncate(string hash, int length = 16)
    {
        if (string.IsNullOrEmpty(hash)) return hash;
        return hash.Length <= length ? hash : hash[..length];
    }
}

sealed class HashCache
{
    public sealed class CacheRecord
    {
        public long Size { get; set; }
        public long WriteTicks { get; set; }
        public string Sha256 { get; set; } = string.Empty;
    }

    public sealed class CacheFile
    {
        public string Version { get; set; } = string.Empty;
        public Dictionary<string, CacheRecord> Entries { get; set; } = new();
    }

    const long MaxCacheBytes = 256L * 1024 * 1024;

    readonly ConcurrentDictionary<string, CacheRecord> _entries = new(PathComparer.Instance);
    readonly string _path;
    int _dirty;

    HashCache(string path) { _path = path; }

    public int Count => _entries.Count;

    public static HashCache Load(string path, BuildLogger logger)
    {
        var cache = new HashCache(path);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return cache;
            if (info.Length > MaxCacheBytes)
            {
                logger.Warn($"Hash cache is too large and will be ignored: {path}");
                return cache;
            }

            string json = File.ReadAllText(path, Encoding.UTF8);
            CacheFile? file = JsonSerializer.Deserialize<CacheFile>(json);
            if (file == null || !string.Equals(file.Version, BuildPaths.AppVersion, StringComparison.Ordinal))
            {
                logger.Log("Hash cache version mismatch; cache will be rebuilt.");
                return cache;
            }

            foreach (var kv in file.Entries)
                if (kv.Value != null && OptionsValidator.IsSha256(kv.Value.Sha256))
                    cache._entries[kv.Key] = kv.Value;
        }
        catch (Exception ex)
        {
            logger.Warn($"Failed to load hash cache {path}: {ex.Message}");
        }
        return cache;
    }

    public bool TryGet(string fullPath, long size, long writeTicks, out string sha256)
    {
        sha256 = string.Empty;
        if (_entries.TryGetValue(fullPath, out CacheRecord? record) && record.Size == size && record.WriteTicks == writeTicks)
        {
            sha256 = record.Sha256;
            return true;
        }
        return false;
    }

    public void Set(string fullPath, long size, long writeTicks, string sha256)
    {
        _entries[fullPath] = new CacheRecord { Size = size, WriteTicks = writeTicks, Sha256 = sha256 };
        Interlocked.Exchange(ref _dirty, 1);
    }

    public void Prune(IEnumerable<string> liveKeys)
    {
        var live = new HashSet<string>(liveKeys, PathComparer.Instance);
        foreach (string key in _entries.Keys)
            if (!live.Contains(key) && _entries.TryRemove(key, out _))
                Interlocked.Exchange(ref _dirty, 1);
    }

    public void Save(BuildLogger logger)
    {
        if (Interlocked.CompareExchange(ref _dirty, 0, 1) == 0) return;
        try
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(_path));
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var file = new CacheFile { Version = BuildPaths.AppVersion };
            foreach (var kv in _entries) file.Entries[kv.Key] = kv.Value;

            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(file), BuildLogger.Utf8NoBom);
            File.Move(tmp, _path, true);
            logger.Log($"Hash cache saved: {_path} ({file.Entries.Count} entries)");
        }
        catch (Exception ex)
        {
            logger.Warn($"Failed to save hash cache {_path}: {ex.Message}");
        }
    }
}

sealed class PluginManager
{
    const int MaxPlugins = 64;
    const long MaxPluginBytes = 64L * 1024 * 1024;
    const int MaxFailuresPerPlugin = 3;

    readonly List<IBuildPlugin> _plugins = new();
    readonly HashSet<string> _allowedNames = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<IBuildPlugin> _disabled = new();
    readonly Dictionary<IBuildPlugin, int> _failures = new();
    readonly List<AssemblyLoadContext> _contexts = new();
    Dictionary<string, string> _hashPins = new(StringComparer.OrdinalIgnoreCase);
    readonly bool _noPrompt;
    readonly bool _silent;
    bool _requireHash;
    bool _strict;
    int _timeoutMs = 30_000;

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
        _requireHash = opts.RequirePluginHash;
        _strict = opts.PluginStrict;
        _timeoutMs = (opts.PluginTimeoutSeconds > 0 ? opts.PluginTimeoutSeconds : 30) * 1000;

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

        CheckDirectoryPermissions(dir, logger);

        string[] dlls;
        try { dlls = Directory.GetFiles(dir, "*.dll"); }
        catch (Exception ex) { logger.Warn($"Failed to enumerate plugins in {dir}: {ex.Message}"); return; }
        Array.Sort(dlls, StringComparer.Ordinal);

        foreach (string dll in dlls)
        {
            if (_plugins.Count >= MaxPlugins)
            {
                logger.Warn($"Plugin limit of {MaxPlugins} reached; remaining assemblies are ignored.");
                break;
            }

            try { LoadAssembly(dll, logger, ui); }
            catch (Exception ex) { logger.Warn($"Failed to load plugin assembly {dll}: {ex.Message}"); }
        }
    }

    void LoadAssembly(string dll, BuildLogger logger, ConsoleUI ui)
    {
        var info = new FileInfo(dll);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            logger.Warn($"Skipping plugin assembly that is a link: {dll}");
            return;
        }
        if (info.Length <= 0 || info.Length > MaxPluginBytes)
        {
            logger.Warn($"Skipping plugin assembly with unsupported size ({info.Length} bytes): {dll}");
            return;
        }

        byte[] bytes = File.ReadAllBytes(dll);
        string hash = FileHasher.ComputeSha256Bytes(bytes);
        string stem = Path.GetFileNameWithoutExtension(dll);

        IReadOnlyList<string> candidates = ScanPluginTypeNames(bytes, logger, dll);
        if (candidates.Count == 0)
        {
            logger.Log($"No plugin types found in {Path.GetFileName(dll)}.");
            return;
        }

        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (string name in candidates)
        {
            if (IsPluginAllowed(name, stem, dll, hash, logger, ui)) allowed.Add(name);
            else logger.Warn($"Plugin blocked: {name} ({dll})");
        }
        if (allowed.Count == 0) return;

        var alc = new AssemblyLoadContext($"plugin_{stem}_{FileHasher.Truncate(hash, 8)}", isCollectible: true);
        _contexts.Add(alc);

        Assembly asm;
        using (var ms = new MemoryStream(bytes, false))
            asm = alc.LoadFromStream(ms);

        IEnumerable<Type> types = asm.GetTypes().Where(t =>
            typeof(IBuildPlugin).IsAssignableFrom(t)
            && !t.IsAbstract
            && t.GetConstructor(Type.EmptyTypes) != null
            && allowed.Contains(t.Name));

        foreach (Type t in types)
        {
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

    static IReadOnlyList<string> ScanPluginTypeNames(byte[] assemblyBytes, BuildLogger logger, string dll)
    {
        var result = new List<string>();
        try
        {
            using var ms = new MemoryStream(assemblyBytes, false);
            using var pe = new System.Reflection.PortableExecutable.PEReader(ms);
            if (!pe.HasMetadata) return result;

            System.Reflection.Metadata.MetadataReader md = pe.GetMetadataReader();
            foreach (System.Reflection.Metadata.TypeDefinitionHandle handle in md.TypeDefinitions)
            {
                System.Reflection.Metadata.TypeDefinition td = md.GetTypeDefinition(handle);
                if ((td.Attributes & TypeAttributes.Abstract) != 0 || (td.Attributes & TypeAttributes.Interface) != 0) continue;

                foreach (System.Reflection.Metadata.InterfaceImplementationHandle ih in td.GetInterfaceImplementations())
                {
                    System.Reflection.Metadata.InterfaceImplementation impl = md.GetInterfaceImplementation(ih);
                    string? interfaceName = null;
                    if (impl.Interface.Kind == System.Reflection.Metadata.HandleKind.TypeReference)
                        interfaceName = md.GetString(md.GetTypeReference((System.Reflection.Metadata.TypeReferenceHandle)impl.Interface).Name);
                    else if (impl.Interface.Kind == System.Reflection.Metadata.HandleKind.TypeDefinition)
                        interfaceName = md.GetString(md.GetTypeDefinition((System.Reflection.Metadata.TypeDefinitionHandle)impl.Interface).Name);

                    if (string.Equals(interfaceName, nameof(IBuildPlugin), StringComparison.Ordinal))
                    {
                        result.Add(md.GetString(td.Name));
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.Warn($"Plugin assembly is not a valid managed assembly: {dll}: {ex.Message}");
            result.Clear();
        }
        return result;
    }

    static void CheckDirectoryPermissions(string dir, BuildLogger logger)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            UnixFileMode mode = File.GetUnixFileMode(dir);
            if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
                logger.Warn($"Plugin directory is writable by group or others: {dir}");
        }
        catch (Exception ex)
        {
            logger.Log($"Could not inspect plugin directory permissions for {dir}: {ex.Message}");
        }
    }

    bool TryFindPin(string name, string stem, out string pinned)
    {
        if (_hashPins.TryGetValue(name, out string? byName) && !string.IsNullOrEmpty(byName)) { pinned = byName; return true; }
        if (_hashPins.TryGetValue(stem, out string? byStem) && !string.IsNullOrEmpty(byStem)) { pinned = byStem; return true; }
        pinned = string.Empty;
        return false;
    }

    bool IsPluginAllowed(string name, string stem, string dllPath, string hash, BuildLogger logger, ConsoleUI ui)
    {
        if (TryFindPin(name, stem, out string pinned))
        {
            if (!string.Equals(pinned, hash, StringComparison.OrdinalIgnoreCase))
            {
                logger.Error($"Plugin hash mismatch for pinned plugin '{name}': expected {pinned}, got {hash}. Refusing to load.");
                return false;
            }
        }
        else if (_requireHash)
        {
            logger.Error($"Plugin '{name}' has no --plugin-hash pin and --require-plugin-hash is set. Refusing to load.");
            return false;
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

    static string SafeName(IBuildPlugin p)
    {
        try { return TextSanitizer.Clean(p.Name); }
        catch { return p.GetType().Name; }
    }

    int Invoke(BuildLogger logger, string methodName, Action<IBuildPlugin> call)
    {
        List<IBuildPlugin> snapshot;
        lock (_plugins) snapshot = new List<IBuildPlugin>(_plugins);

        int failures = 0;
        foreach (var p in snapshot)
        {
            if (_disabled.Contains(p)) continue;
            string pluginName = SafeName(p);

            try
            {
                Task task = Task.Run(() => call(p));
                if (!task.Wait(_timeoutMs))
                {
                    logger.Warn($"Plugin {pluginName} {methodName} timed out after {_timeoutMs / 1000} s; plugin disabled.");
                    _disabled.Add(p);
                    failures++;
                }
            }
            catch (AggregateException ae)
            {
                failures++;
                logger.Warn($"Plugin {pluginName} {methodName} failed: {ae.GetBaseException().Message}");
                int count = _failures.TryGetValue(p, out int previous) ? previous + 1 : 1;
                _failures[p] = count;
                if (count >= MaxFailuresPerPlugin)
                {
                    _disabled.Add(p);
                    logger.Warn($"Plugin {pluginName} disabled after {count} failures.");
                }
            }
        }
        return failures;
    }

    void Dispatch(BuildContext ctx, string methodName, Action<IBuildPlugin> call)
    {
        int failures = Invoke(ctx.Logger, methodName, call);
        if (_strict && failures > 0)
            ctx.State.Fail($"Plugin failure during {methodName} (--plugin-strict).");
    }

    public void OnBeforeBuild(BuildContext ctx) => Dispatch(ctx, nameof(OnBeforeBuild), p => p.OnBeforeBuild(ctx));
    public void OnAfterBuild(BuildContext ctx) => Dispatch(ctx, nameof(OnAfterBuild), p => p.OnAfterBuild(ctx));
    public void OnStepExecuting(BuildContext ctx, IBuildStep step) => Dispatch(ctx, nameof(OnStepExecuting), p => p.OnStepExecuting(ctx, step));
    public void OnStepExecuted(BuildContext ctx, IBuildStep step) => Dispatch(ctx, nameof(OnStepExecuted), p => p.OnStepExecuted(ctx, step));
}
sealed class BuildPaths
{
    public const string AppVersion = "2027.2";
    public const string BootBinFilename = "boot.bin";
    public const string EfiImgFilename = "efiboot.img";
    public const string PluginsDirName = "plugins";
    public const string ManifestFilename = "MANIFEST.SHA256SUMS";
    public const string ReadmeFilename = "README_BUILDISO.txt";
    public const string LogFilename = "buildiso.log";
    public const string EfiExtraIsoPath = "BOOT/EFIBOOT.IMG";
}

sealed class BuildConfig
{
    public string ProjectDir { get; init; } = string.Empty;
    public string BootDir { get; init; } = string.Empty;
    public string IsoDir { get; init; } = string.Empty;
    public string BootBinPath { get; init; } = string.Empty;
    public string EfiImgPath { get; init; } = string.Empty;
    public string IsoOutputPath { get; init; } = string.Empty;
    public string TempIsoPath { get; init; } = string.Empty;
    public string BackupIsoPath { get; init; } = string.Empty;
    public string FailedIsoPath { get; init; } = string.Empty;
    public string LockPath { get; init; } = string.Empty;
    public string ManifestPath { get; init; } = string.Empty;
    public string ReadmePath { get; init; } = string.Empty;
    public string CachePath { get; init; } = string.Empty;
    public string VolumeId { get; init; } = string.Empty;
    public bool IncludeUefi { get; init; }
    public bool BiosEnabled { get; init; }
    public bool SecureBoot { get; init; }
    public bool Strict { get; init; }
    public bool DeepVerify { get; init; }
    public bool Reproducible { get; init; }
    public long SourceDateEpoch { get; init; }
    public string ExpectSha256 { get; init; } = string.Empty;
    public int Threads { get; init; } = 1;
    public BootPriorityMode BootPriority { get; init; } = BootPriorityMode.Auto;
    public PathCollisionMode CollisionMode { get; init; } = PathCollisionMode.Rename;
    public IReadOnlyList<string> ExcludePatterns { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> IncludePatterns { get; init; } = Array.Empty<string>();
    public IReadOnlySet<string> ReservedPaths { get; init; } = new HashSet<string>(PathComparer.Instance);
    public long MaxSizeMb { get; init; }
    public long MaxFileSizeMb { get; init; }
    public int MaxFiles { get; init; }
    public int MaxDepth { get; init; } = 64;
    public bool ManifestEnabled { get; init; }
}

sealed class InventoryEntry
{
    public string FullPath { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;
    public string IsoPath { get; init; } = string.Empty;
    public long Size { get; init; }
    public long LastWriteTicks { get; init; }
    public string Sha256 { get; set; } = string.Empty;
}

sealed class FileInventory
{
    public List<InventoryEntry> Files { get; } = new();
    public List<string> EmptyDirectories { get; } = new();
    public List<string> Renames { get; } = new();
    public int DirectoryCount { get; set; }
    public int SkippedFiles { get; set; }
    public int FailedFiles { get; set; }
    public int Collisions { get; set; }
    public long TotalBytes { get; set; }
    public bool Truncated { get; set; }
    public bool MaxFilesExceeded { get; set; }
    public string TruncationReason { get; set; } = string.Empty;
}

sealed class StepResult
{
    public string Name { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public double Seconds { get; init; }
    public string Message { get; init; } = string.Empty;
}

sealed class PreflightIssue
{
    public IssueSeverity Severity { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string Hint { get; init; } = string.Empty;
}

sealed class BootTestResult
{
    public string Mode { get; init; } = string.Empty;
    public bool Passed { get; init; }
    public string Message { get; init; } = string.Empty;
    public double Seconds { get; init; }
}

sealed class BuildState
{
    public bool Failed { get; set; }
    public string FailureMessage { get; set; } = string.Empty;
    public BuildStatus Status { get; set; } = BuildStatus.Success;
    public BootImageType BootType { get; set; } = BootImageType.Unknown;
    public long PredictedSizeMb { get; set; }
    public long ActualSizeBytes { get; set; }
    public long ExactSizeBytes { get; set; }
    public double BuildSeconds { get; set; }
    public double BuildSpeedMbPerSec { get; set; }
    public int TotalFiles { get; set; }
    public int ProcessedFiles { get; set; }
    public int SkippedFiles { get; set; }
    public int FailedFiles { get; set; }
    public string IsoSha256 { get; set; } = string.Empty;
    public bool OutputInstalled { get; set; }
    public string BackupPath { get; set; } = string.Empty;
    public string ExtraEfiIsoPath { get; set; } = string.Empty;
    public string FailedStep { get; set; } = string.Empty;
    public bool BootTestFailed { get; set; }
    public bool VerifyPassed { get; set; }
    public bool TimedOut { get; set; }
    public FileInventory? Inventory { get; set; }
    public List<(string IsoPath, string Sha256, long Size)> ManifestEntries { get; } = new();
    public List<StepResult> Steps { get; } = new();
    public List<PreflightIssue> PreflightIssues { get; } = new();
    public List<string> Renames { get; } = new();
    public List<BootCatalogEntry> BootEntries { get; } = new();
    public Dictionary<string, string> ToolHashes { get; } = new(StringComparer.Ordinal);
    public List<BootTestResult> BootTests { get; } = new();

    public void Fail(string message)
    {
        if (Failed) return;
        Failed = true;
        FailureMessage = message;
        Status = BuildStatus.Failed;
    }

    public void FailValidation(string message)
    {
        if (Failed) return;
        Failed = true;
        FailureMessage = message;
        Status = BuildStatus.ValidationFailed;
    }

    public void Cancel(string message)
    {
        if (Failed) return;
        Failed = true;
        FailureMessage = message;
        Status = BuildStatus.Cancelled;
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
    public SpinnerScope? Progress { get; set; }
    public OutputLockHandle? OutputLock { get; set; }
    public HashCache? HashCache { get; set; }

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
            new StepPreflight(),
            new StepScanFiles(),
            new StepValidateBoot(),
            new StepPredictSize(),
            new StepCheckDiskSpace(),
            new StepBuildIso(),
            new StepVerifyIso(),
            new StepBootTest(),
            new StepWriteManifest(),
            new StepWriteReadme(),
            new StepFinalize()
        }
    };

    public static bool IsSkipped(IBuildStep step, IsoBuilderOptions opts)
    {
        if (opts.Check)
            return step is StepBuildIso or StepVerifyIso or StepBootTest or StepWriteManifest or StepWriteReadme;
        if (opts.DryRun)
            return step is StepVerifyIso or StepBootTest or StepWriteReadme or StepWriteManifest;
        if (!opts.BootTest)
            return step is StepBootTest;
        return false;
    }

    public static bool IsPreBuild(string stepName) =>
        stepName is "Preflight" or "ScanFiles" or "ValidateBoot" or "PredictSize" or "CheckDiskSpace";
}

static class ExecutableLocator
{
    public static string? Find(string name)
    {
        try
        {
            string? path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(path)) return null;
            string[] candidates = OperatingSystem.IsWindows() ? new[] { name + ".exe", name + ".cmd", name } : new[] { name };
            foreach (string dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (string candidate in candidates)
                {
                    string full = Path.Combine(dir.Trim().Trim('"'), candidate);
                    if (File.Exists(full)) return full;
                }
            }
        }
        catch { }
        return null;
    }
}

static class PathTools
{
    static readonly StringComparison Comparison = (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public static bool IsInside(string root, string path)
    {
        try
        {
            string r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string p = Path.GetFullPath(path);
            return p.StartsWith(r, Comparison);
        }
        catch { return false; }
    }

    public static string NearestExistingAncestor(string path)
    {
        string? current = Path.GetDirectoryName(Path.GetFullPath(path));
        while (!string.IsNullOrEmpty(current) && !Directory.Exists(current))
            current = Path.GetDirectoryName(current);
        return current ?? string.Empty;
    }

    public static bool CanWriteTo(string directory)
    {
        try
        {
            string probe = Path.Combine(directory, ".buildiso_probe_" + Guid.NewGuid().ToString("N"));
            using (var fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
                fs.WriteByte(0);
            return true;
        }
        catch { return false; }
    }
}

sealed class StepPreflight : IBuildStep
{
    public string Name => "Preflight";

    public void Execute(BuildContext ctx)
    {
        BuildConfig cfg = ctx.Config;
        IsoBuilderOptions opts = ctx.Opts;
        List<PreflightIssue> issues = ctx.State.PreflightIssues;
        bool mutating = !opts.Check && !opts.DryRun;

        void Add(IssueSeverity severity, string code, string message, string hint) =>
            issues.Add(new PreflightIssue { Severity = severity, Code = code, Message = message, Hint = hint });

        Add(IssueSeverity.Info, "PF000", $"BuildIso {BuildPaths.AppVersion} on {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription} / {System.Runtime.InteropServices.RuntimeInformation.OSDescription}", string.Empty);

        if (PathComparer.Instance.Equals(cfg.IsoOutputPath, cfg.BootBinPath) || PathComparer.Instance.Equals(cfg.IsoOutputPath, cfg.EfiImgPath))
            Add(IssueSeverity.Error, "PF101", "The output path is the same file as a boot image.", "Choose a different --output path.");

        if (PathTools.IsInside(cfg.IsoDir, cfg.IsoOutputPath))
            Add(IssueSeverity.Warning, "PF102", "The output file is inside the ISO root; it will be excluded from the image.", "Use an output path outside of the ISO root.");

        if (Directory.Exists(cfg.IsoOutputPath))
            Add(IssueSeverity.Error, "PF103", "The output path is an existing directory.", "Choose a file path for --output.");
        else if (File.Exists(cfg.IsoOutputPath))
        {
            if (opts.NoOverwrite)
                Add(IssueSeverity.Error, "PF104", "The output file already exists and --no-overwrite is set.", "Remove the file or drop --no-overwrite.");
            else
                Add(IssueSeverity.Info, "PF104", "The existing output file will be replaced; the previous ISO is kept as .bak until the new one is verified.", string.Empty);
        }

        string outputParent = Path.GetDirectoryName(cfg.IsoOutputPath) ?? string.Empty;
        if (outputParent.Length > 0 && !Directory.Exists(outputParent))
        {
            if (opts.FixSafe && mutating)
            {
                try
                {
                    Directory.CreateDirectory(outputParent);
                    Add(IssueSeverity.Info, "PF105", $"Created output directory: {outputParent}", string.Empty);
                }
                catch (Exception ex)
                {
                    Add(IssueSeverity.Error, "PF105", $"Cannot create output directory: {ex.Message}", "Create the directory manually or choose another --output.");
                }
            }
            else
            {
                string ancestor = PathTools.NearestExistingAncestor(cfg.IsoOutputPath);
                if (ancestor.Length == 0 || !PathTools.CanWriteTo(ancestor))
                    Add(IssueSeverity.Error, "PF105", "The output directory does not exist and cannot be created.", "Choose a writable --output location.");
                else
                    Add(IssueSeverity.Warning, "PF105", "The output directory does not exist.", "It will be created during a real build, or use --fix-safe.");
            }
        }
        else if (outputParent.Length > 0 && !PathTools.CanWriteTo(outputParent))
        {
            Add(IssueSeverity.Error, "PF106", $"The output directory is not writable: {outputParent}", "Fix the directory permissions or choose another --output.");
        }

        if (File.Exists(cfg.TempIsoPath))
        {
            if (opts.FixSafe && mutating)
            {
                try
                {
                    File.Delete(cfg.TempIsoPath);
                    Add(IssueSeverity.Info, "PF107", $"Removed stale temporary file: {cfg.TempIsoPath}", string.Empty);
                }
                catch (Exception ex)
                {
                    Add(IssueSeverity.Warning, "PF107", $"Could not remove stale temporary file: {ex.Message}", "Delete it manually.");
                }
            }
            else
                Add(IssueSeverity.Info, "PF107", "A stale temporary ISO file exists and will be overwritten.", "Use --fix-safe to remove it beforehand.");
        }

        if (!string.Equals(Path.GetExtension(cfg.IsoOutputPath), ".iso", StringComparison.OrdinalIgnoreCase))
            Add(IssueSeverity.Warning, "PF108", "The output file does not have an .iso extension.", "Use a .iso extension for better tool compatibility.");

        if (OperatingSystem.IsWindows() && cfg.IsoOutputPath.Length > 240)
            Add(IssueSeverity.Warning, "PF109", "The output path is very long and may exceed the Windows path limit.", "Use a shorter output path.");

        if (opts.Reproducible && ctx.Plugins.Plugins.Count > 0)
            Add(IssueSeverity.Warning, "PF110", "Plugins are loaded; they may alter the output and break reproducibility.", "Use --plugins-off for reproducible builds.");

        if (opts.BootTest)
        {
            string? qemu = ResolveQemu(opts);
            if (qemu == null)
                Add(IssueSeverity.Error, "PF111", "--boot-test requires QEMU but qemu-system-x86_64 was not found.", "Install QEMU or pass --qemu <path>.");
            if (cfg.IncludeUefi && (string.IsNullOrEmpty(opts.OvmfPath) || !File.Exists(opts.OvmfPath)))
                Add(IssueSeverity.Warning, "PF112", "UEFI boot test will be skipped: no valid OVMF firmware was given.", "Pass --ovmf <path to OVMF code image>.");
        }

        if (cfg.BiosEnabled && cfg.IncludeUefi && cfg.BootPriority == BootPriorityMode.Auto)
            Add(IssueSeverity.Info, "PF113", "Both boot modes are enabled; BIOS is the default El Torito entry and UEFI is added as a platform section.", string.Empty);

        if (opts.Threads > Environment.ProcessorCount * 4)
            Add(IssueSeverity.Warning, "PF114", "The requested hashing thread count is far above the number of CPU cores.", "Use a lower --threads value.");

        int errors = 0;
        int warnings = 0;
        foreach (PreflightIssue issue in issues)
        {
            string text = $"{issue.Code} {issue.Message}";
            switch (issue.Severity)
            {
                case IssueSeverity.Error: errors++; ctx.Logger.Error(text); break;
                case IssueSeverity.Warning: warnings++; ctx.Logger.Warn(text); break;
                default: ctx.Logger.Log(text); break;
            }
        }

        if (!opts.Silent)
        {
            foreach (PreflightIssue issue in issues)
            {
                if (issue.Severity == IssueSeverity.Info && !opts.Check && !opts.Verbose) continue;
                string tag = issue.Severity switch { IssueSeverity.Error => "[ERROR]", IssueSeverity.Warning => "[WARN] ", _ => "[INFO] " };
                ctx.Ui.WriteLine($"{tag} {issue.Code} {issue.Message}");
                if (issue.Hint.Length > 0 && issue.Severity != IssueSeverity.Info) ctx.Ui.WriteLine($"         fix: {issue.Hint}");
            }
        }

        if (errors > 0)
            ctx.State.Fail($"Preflight failed: {errors} error(s), {warnings} warning(s).");
    }

    internal static string? ResolveQemu(IsoBuilderOptions opts)
    {
        if (!string.IsNullOrEmpty(opts.QemuPath)) return File.Exists(opts.QemuPath) ? opts.QemuPath : null;
        return ExecutableLocator.Find("qemu-system-x86_64");
    }
}

sealed class StepScanFiles : IBuildStep
{
    public string Name => "ScanFiles";

    public void Execute(BuildContext ctx)
    {
        FileInventory inv;
        using (var spinner = new SpinnerScope(ctx.Ui, ctx.Opts, "Scanning files"))
        {
            spinner.SetUnit(false);
            inv = FileTreeScanner.BuildInventory(ctx.Config, ctx.Logger, ctx.CancellationToken, true, found => spinner.Report(found, 0));
            spinner.Succeed();
        }

        ctx.State.Inventory = inv;
        ctx.State.TotalFiles = inv.Files.Count;
        ctx.State.ProcessedFiles = 0;
        ctx.State.SkippedFiles = inv.SkippedFiles;
        ctx.State.FailedFiles = inv.FailedFiles;
        ctx.State.Renames.AddRange(inv.Renames);

        int count = inv.Files.Count;
        if (!ctx.Opts.Silent) ctx.Ui.WriteLine($"Files to add: {count}");
        if (inv.Renames.Count > 0 && ctx.Opts.Verbose && !ctx.Opts.Silent)
            foreach (string rename in inv.Renames) ctx.Ui.WriteLine($"Renamed: {rename}");

        if (inv.MaxFilesExceeded)
        {
            ctx.State.Fail($"File count guard triggered: more than {ctx.Config.MaxFiles} files found, exceeding the configured limit of {ctx.Config.MaxFiles} (--max-files).");
            return;
        }

        if (inv.Truncated)
        {
            ctx.State.Fail($"File scan was truncated: {inv.TruncationReason}");
            return;
        }

        if (inv.FailedFiles > 0)
        {
            ctx.State.Fail($"{inv.FailedFiles} file(s) or directories cannot be included in the ISO (see the log for details).");
            return;
        }

        if (count == 0) ctx.Logger.Warn("No files were found in the ISO root; the ISO will only contain boot data.");
    }
}

sealed class StepValidateBoot : IBuildStep
{
    public string Name => "ValidateBoot";

    public void Execute(BuildContext ctx)
    {
        BuildConfig cfg = ctx.Config;

        if (cfg.BiosEnabled)
        {
            if (!BootValidator.Validate(cfg.BootBinPath, out BootImageType type, ctx.Logger))
            {
                if (ctx.Opts.AllowUnknownBoot)
                {
                    ctx.Logger.Warn("boot.bin was not recognized as a BIOS boot sector or EFI binary; continuing because of --allow-unknown-boot.");
                    ctx.State.BootType = BootImageType.Unknown;
                }
                else
                {
                    ctx.State.Fail("Invalid or unrecognized boot.bin.");
                    return;
                }
            }
            else
            {
                ctx.State.BootType = type;
            }

            RecordHash(ctx, BuildPaths.BootBinFilename, cfg.BootBinPath);
        }

        if (cfg.IncludeUefi)
        {
            if (!BootValidator.ValidateEfiImage(cfg.EfiImgPath, out BootImageType efiType, ctx.Logger))
            {
                string imgWarn = "Warning: efiboot.img does not look like a FAT EFI System Partition image.";
                if (!ctx.Opts.Silent) ctx.Ui.WriteLine(imgWarn);
                ctx.Logger.Warn(imgWarn);
            }
            else if (!cfg.BiosEnabled)
            {
                ctx.State.BootType = efiType;
            }

            RecordHash(ctx, BuildPaths.EfiImgFilename, cfg.EfiImgPath);

            string efiBoot = Path.Combine(cfg.IsoDir, "EFI", "BOOT", "BOOTX64.EFI");
            if (!File.Exists(efiBoot))
            {
                string warn = "Warning: EFI/BOOT/BOOTX64.EFI missing in ISO root.";
                if (!ctx.Opts.Silent) ctx.Ui.WriteLine(warn);
                ctx.Logger.Warn(warn);
            }
        }

        if (cfg.SecureBoot)
        {
            string note = "Note: Secure Boot flag enabled. Ensure efiboot.img is properly signed.";
            if (!ctx.Opts.Silent) ctx.Ui.WriteLine(note);
            ctx.Logger.Log(note);

            string efiBoot = Path.Combine(cfg.IsoDir, "EFI", "BOOT", "BOOTX64.EFI");
            if (File.Exists(efiBoot))
            {
                if (!PeInspector.TryInspect(efiBoot, out bool signed, out ushort machine))
                    ctx.Logger.Warn("EFI/BOOT/BOOTX64.EFI is not a valid PE image.");
                else
                {
                    if (!signed) ctx.Logger.Warn("EFI/BOOT/BOOTX64.EFI carries no Authenticode signature; Secure Boot firmware will refuse it.");
                    else ctx.Logger.Log("EFI/BOOT/BOOTX64.EFI carries an Authenticode signature entry.");
                    if (machine != 0x8664) ctx.Logger.Warn($"EFI/BOOT/BOOTX64.EFI targets machine type 0x{machine:X4}, not x64.");
                }
            }
        }

        if (cfg.BiosEnabled && cfg.IncludeUefi)
        {
            string prioNote = $"Both BIOS and UEFI boot images configured. Boot priority: {cfg.BootPriority}.";
            ctx.Logger.Log(prioNote);
        }
    }

    static void RecordHash(BuildContext ctx, string label, string path)
    {
        string hash = FileHasher.ComputeSha256(path, ctx.CancellationToken);
        if (hash.Length == 0) ctx.Logger.Warn($"Could not hash {label}.");
        else ctx.State.ToolHashes[label] = hash;
    }
}

sealed class StepPredictSize : IBuildStep
{
    public string Name => "PredictSize";

    public void Execute(BuildContext ctx)
    {
        if (ctx.State.Inventory == null)
        {
            ctx.State.Fail("File inventory is missing; the scan step did not run.");
            return;
        }

        long mb = SizePredictor.PredictIsoSizeMb(ctx.State.Inventory, ctx.Config.BootBinPath, ctx.Config.EfiImgPath, ctx.Config.BiosEnabled, ctx.Config.IncludeUefi, ctx.Logger);
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
        if (ctx.CancellationToken.IsCancellationRequested) { ctx.State.Cancel("Build cancelled."); return; }

        if (ctx.State.Inventory == null)
        {
            ctx.State.Fail("File inventory is missing; the scan step did not run.");
            return;
        }

        if (ctx.Opts.DryRun)
        {
            var swDry = Stopwatch.StartNew();
            try
            {
                IsoBuilderService.Simulate(ctx);
                swDry.Stop();
                ctx.State.BuildSeconds = swDry.Elapsed.TotalSeconds;
                if (!ctx.State.Failed)
                {
                    if (!ctx.Opts.Silent)
                    {
                        ctx.Ui.WriteLine("Dry run: ISO layout simulated in memory, no ISO written.");
                        ctx.Ui.WriteLine($"Exact ISO size from layout: {TextSanitizer.FormatBytes(ctx.State.ExactSizeBytes)}");
                    }
                    ctx.Logger.Log("Dry run: build step simulated without writing output.");
                }
            }
            catch (OperationCanceledException)
            {
                ctx.State.Cancel("Build cancelled.");
            }
            catch (Exception ex)
            {
                ctx.State.Fail($"Dry run validation failed: {ex.Message}");
                ctx.Logger.Error($"Dry run exception: {ex}");
            }
            return;
        }

        string? lockError;
        ctx.OutputLock = OutputLockHandle.Acquire(ctx.Config.LockPath, ctx.Logger, out lockError);
        if (ctx.OutputLock == null)
        {
            ctx.State.Fail(lockError ?? "Another build is already writing to this output path.");
            return;
        }

        var sw = Stopwatch.StartNew();
        using var spinner = new SpinnerScope(ctx.Ui, ctx.Opts);
        ctx.Progress = spinner;

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
        catch (OperationCanceledException)
        {
            sw.Stop();
            spinner.Fail();
            ctx.State.Cancel("Build cancelled.");
        }
        catch (Exception ex)
        {
            sw.Stop();
            spinner.Fail();
            ctx.State.Fail($"Build step failed: {ex.Message}");
            ctx.Logger.Error($"Build step exception: {ex}");
        }
        finally
        {
            ctx.Progress = null;
        }
    }
}

sealed class StepVerifyIso : IBuildStep
{
    public string Name => "VerifyIso";

    public void Execute(BuildContext ctx)
    {
        BuildConfig cfg = ctx.Config;
        if (!File.Exists(cfg.IsoOutputPath))
        {
            ctx.State.FailValidation("ISO output file not found after build.");
            return;
        }

        FileInfo finfo;
        try { finfo = new FileInfo(cfg.IsoOutputPath); }
        catch (Exception ex) { ctx.State.Fail($"Cannot verify ISO: {ex.Message}"); return; }

        long actual = finfo.Length;
        if (actual < 2048)
        {
            ctx.State.FailValidation($"ISO output file is suspiciously small: {actual} bytes.");
            return;
        }

        long predictedBytes = ctx.State.PredictedSizeMb * 1024L * 1024L;
        double ratio = predictedBytes > 0 ? (double)actual / predictedBytes : 1.0;
        if (ratio < 0.05 || ratio > 20.0)
            ctx.Logger.Warn($"ISO size ({actual} bytes) differs significantly from prediction ({predictedBytes} bytes).");

        var report = new IsoVerificationReport();
        FileInventory? inv = ctx.State.Inventory;

        try
        {
            using IsoLbaReader reader = IsoLbaReader.Open(cfg.IsoOutputPath);
            IsoVerifier.VerifyStructure(reader, actual, true, report);
            IsoVerifier.VerifyBoot(reader, cfg, report);
            ctx.State.BootEntries.AddRange(report.BootEntries);

            if (inv != null && report.Ok)
                IsoVerifier.VerifyFiles(reader, inv, ctx.State.ExtraEfiIsoPath, report, ctx.CancellationToken, ctx.Logger);

            bool needContent = cfg.ManifestEnabled || cfg.DeepVerify;
            if (needContent && inv != null && report.Ok)
            {
                if (cfg.DeepVerify)
                {
                    int hashFailures;
                    using (var hashSpinner = new SpinnerScope(ctx.Ui, ctx.Opts, "Hashing sources"))
                    {
                        hashSpinner.SetUnit(false);
                        hashFailures = FileTreeScanner.HashInventory(inv, ctx.HashCache, cfg.Threads, ctx.Logger, ctx.CancellationToken, (done, total) => hashSpinner.Report(done, total));
                        if (hashFailures == 0) hashSpinner.Succeed();
                    }
                    if (hashFailures > 0) report.AddFailure($"{hashFailures} source file(s) could not be hashed for deep verification.");
                }

                if (report.Ok)
                {
                    using var contentSpinner = new SpinnerScope(ctx.Ui, ctx.Opts, "Verifying content");
                    contentSpinner.SetUnit(true);
                    IsoVerifier.HashContent(reader, inv, ctx.State.ExtraEfiIsoPath, cfg.DeepVerify, report, ctx.CancellationToken, (done, total) => contentSpinner.Report(done, total));
                    if (report.Ok) contentSpinner.Succeed();
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            report.AddFailure($"ISO structure is unreadable: {ex.Message}");
        }

        foreach (string warning in report.Warnings) ctx.Logger.Warn(warning);
        if (!report.Ok)
        {
            foreach (string failure in report.Failures) ctx.Logger.Error(failure);
            string extra = report.Failures.Count > 1 ? $" (+{report.Failures.Count - 1} more, see the log)" : string.Empty;
            ctx.State.FailValidation($"ISO verification failed: {report.Failures[0]}{extra}");
            return;
        }

        ctx.State.ManifestEntries.Clear();
        ctx.State.ManifestEntries.AddRange(report.ContentHashes);

        string hash;
        using (var hashIsoSpinner = new SpinnerScope(ctx.Ui, ctx.Opts, "Hashing ISO"))
        {
            hashIsoSpinner.SetUnit(true);
            hash = FileHasher.ComputeSha256(cfg.IsoOutputPath, ctx.CancellationToken, done => hashIsoSpinner.Report(done, actual));
            if (hash.Length > 0) hashIsoSpinner.Succeed();
        }

        if (hash.Length == 0)
        {
            ctx.State.Fail("Could not compute the SHA-256 of the generated ISO.");
            return;
        }

        ctx.State.IsoSha256 = hash;
        ctx.Logger.Log($"ISO SHA256: {hash}");
        if (!ctx.Opts.Silent) ctx.Ui.WriteLine($"ISO SHA256: {hash}");

        if (cfg.ExpectSha256.Length > 0 && !string.Equals(cfg.ExpectSha256, hash, StringComparison.OrdinalIgnoreCase))
        {
            ctx.State.FailValidation($"ISO SHA-256 does not match --expect-sha256 (expected {cfg.ExpectSha256}, got {hash}).");
            return;
        }

        ctx.State.VerifyPassed = true;
    }
}

sealed class StepBootTest : IBuildStep
{
    public string Name => "BootTest";

    public void Execute(BuildContext ctx)
    {
        IsoBuilderOptions opts = ctx.Opts;
        BuildConfig cfg = ctx.Config;
        if (!opts.BootTest) return;

        string? qemu = StepPreflight.ResolveQemu(opts);
        if (qemu == null)
        {
            ctx.State.BootTestFailed = true;
            ctx.State.Fail("Boot test requested but QEMU was not found.");
            return;
        }

        int timeout = opts.BootTestTimeoutSeconds > 0 ? opts.BootTestTimeoutSeconds : 20;
        var modes = new List<(string Mode, string? Firmware)>();
        if (cfg.BiosEnabled) modes.Add(("BIOS", null));
        if (cfg.IncludeUefi)
        {
            if (!string.IsNullOrEmpty(opts.OvmfPath) && File.Exists(opts.OvmfPath)) modes.Add(("UEFI", opts.OvmfPath));
            else ctx.Logger.Warn("UEFI boot test skipped: no valid --ovmf firmware image.");
        }

        foreach (var (mode, firmware) in modes)
        {
            if (!opts.Silent) ctx.Ui.WriteLine($"Boot test ({mode}): running QEMU for up to {timeout} s...");
            BootTestResult result = BootTester.Run(ctx, mode, qemu, firmware, timeout);
            ctx.State.BootTests.Add(result);
            if (result.Passed) ctx.Logger.Log($"Boot test {mode} passed: {result.Message}");
            else ctx.Logger.Error($"Boot test {mode} failed: {result.Message}");
            if (!opts.Silent) ctx.Ui.WriteLine($"Boot test ({mode}): {(result.Passed ? "passed" : "FAILED")} - {result.Message}");
            if (!result.Passed)
            {
                ctx.State.BootTestFailed = true;
                ctx.State.FailValidation($"Boot test failed ({mode}): {result.Message}");
                return;
            }
        }
    }
}

sealed class StepWriteManifest : IBuildStep
{
    public string Name => "WriteManifest";

    public void Execute(BuildContext ctx)
    {
        if (!ctx.Config.ManifestEnabled) return;

        string manifestPath = ctx.Config.ManifestPath;
        string tmp = manifestPath + ".tmp";

        try
        {
            DateTime stamp = ctx.Config.Reproducible
                ? DateTimeOffset.FromUnixTimeSeconds(ctx.Config.SourceDateEpoch).UtcDateTime
                : DateTime.UtcNow;

            var sb = new StringBuilder();
            sb.AppendLine($"# BuildIso {BuildPaths.AppVersion} manifest");
            sb.AppendLine($"# Generated: {stamp:O}");
            sb.AppendLine($"# ISO: {Path.GetFileName(ctx.Config.IsoOutputPath)}");
            sb.AppendLine($"# ISO SHA256: {ctx.State.IsoSha256}");
            sb.AppendLine();

            foreach (var entry in ctx.State.ManifestEntries.OrderBy(e => e.IsoPath, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine($"{entry.Sha256}  {entry.Size,12}  {entry.IsoPath}");

            File.WriteAllText(tmp, sb.ToString(), BuildLogger.Utf8NoBom);
            File.Move(tmp, manifestPath, true);
            ctx.Logger.Log($"Manifest written: {manifestPath} ({ctx.State.ManifestEntries.Count} entries)");
            if (!ctx.Opts.Silent) ctx.Ui.WriteLine($"Manifest: {manifestPath}");
        }
        catch (Exception ex)
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            ctx.State.Fail($"Failed to write manifest: {ex.Message}");
        }
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
        ReadmeGenerator.Write(outputDir, projectName, BuildPaths.AppVersion, ctx.Config.BiosEnabled, ctx.Config.IncludeUefi, ctx.Config.SecureBoot, ctx.Config.BootPriority, ctx.Config.ManifestEnabled, ctx.Logger, ctx.State.IsoSha256, ctx.Config.Reproducible);
    }
}

sealed class StepFinalize : IBuildStep
{
    public string Name => "Finalize";

    public void Execute(BuildContext ctx)
    {
        if (ctx.Opts.Check)
        {
            ctx.Logger.Log("Preflight checks completed successfully.");
            return;
        }

        if (ctx.Opts.DryRun)
        {
            ctx.Logger.Log("Dry run completed successfully.");
            return;
        }

        IsoBuilderService.Commit(ctx);
        ctx.Logger.Log("Build completed successfully.");
    }
}
sealed class ConsoleUI
{
    readonly object _lock;

    public ConsoleUI(object lockObj) { _lock = lockObj; }

    public void WriteLine(string s) { lock (_lock) { try { Console.WriteLine(Sanitize(s)); } catch (IOException) { } } }
    public void Write(string s) { lock (_lock) { try { Console.Write(Sanitize(s)); } catch (IOException) { } } }
    public void WriteInline(string s) { lock (_lock) { try { Console.Write(Sanitize(s)); } catch (IOException) { } } }
    public void Error(string s) { lock (_lock) { try { Console.Error.WriteLine(Sanitize(s)); } catch (IOException) { } } }

    static string Sanitize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        bool dirty = false;
        foreach (char c in s)
            if (IsBad(c)) { dirty = true; break; }
        if (!dirty) return s;

        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            if (!IsBad(c)) sb.Append(c);
        return sb.ToString();
    }

    static bool IsBad(char c) => (c < 32 && c != '\r' && c != '\n' && c != '\t') || c == 0x7F || (c >= 0x80 && c <= 0x9F);
}

sealed class SpinnerScope : IDisposable
{
    static readonly bool Utf8Console = DetectUtf8();

    readonly ConsoleUI _ui;
    readonly IsoBuilderOptions _opts;
    readonly CancellationTokenSource _cts;
    readonly Task _task;
    readonly string _label;
    readonly bool _active;
    readonly Stopwatch _watch = Stopwatch.StartNew();
    long _done;
    long _total;
    bool _bytes = true;
    volatile bool _stopped;
    bool _succeeded;
    bool _disposed;
    int _lastLength;

    public SpinnerScope(ConsoleUI ui, IsoBuilderOptions opts, string label = "Building ISO")
    {
        _ui = ui;
        _opts = opts;
        _label = label;
        _cts = new CancellationTokenSource();

        bool redirected;
        try { redirected = Console.IsOutputRedirected; }
        catch { redirected = true; }

        _active = !opts.NoProgress && !opts.Silent && !redirected;
        if (_active)
            _task = SpinAsync(_cts.Token);
        else
            _task = Task.CompletedTask;
    }

    static bool DetectUtf8()
    {
        try { return Console.OutputEncoding.CodePage == 65001; }
        catch { return false; }
    }

    public void Succeed() { _succeeded = true; }
    public void Fail() { _succeeded = false; }
    public void SetUnit(bool bytes) { _bytes = bytes; }

    public void Report(long done, long total)
    {
        Interlocked.Exchange(ref _done, done);
        Interlocked.Exchange(ref _total, total);
    }

    string Render(int frame)
    {
        char[] seq = { '/', '-', '\\', '|' };
        string spin = _opts.NoSpinner ? string.Empty : seq[frame % seq.Length] + " ";
        long done = Interlocked.Read(ref _done);
        long total = Interlocked.Read(ref _total);
        string dot = Utf8Console ? "\u00B7" : "|";

        if (total > 0)
        {
            double frac = Math.Min(1.0, Math.Max(0.0, done / (double)total));
            const int width = 20;
            int filled = (int)(frac * width);
            char full = Utf8Console ? '\u2588' : '#';
            char empty = Utf8Console ? '\u2591' : '-';
            string bar = new string(full, filled) + new string(empty, width - filled);
            double elapsed = Math.Max(0.001, _watch.Elapsed.TotalSeconds);
            double speed = done / elapsed;
            double eta = speed > 0 ? (total - done) / speed : double.NaN;
            string pct = (frac * 100).ToString("F0", CultureInfo.InvariantCulture);
            string amount = _bytes
                ? $"{TextSanitizer.FormatBytes(done)}/{TextSanitizer.FormatBytes(total)} {dot} {TextSanitizer.FormatBytes(speed)}/s"
                : $"{done}/{total} files";
            return $"{_label} {spin}[{bar}] {pct} % {dot} {amount} {dot} ETA {TextSanitizer.FormatDuration(eta)}";
        }

        if (done > 0 && !_bytes) return $"{_label} {spin}{done} files";
        return $"{_label}... {spin}".TrimEnd();
    }

    void Draw(string text)
    {
        int width = 0;
        try { width = Console.WindowWidth; } catch { }
        if (width > 8 && text.Length > width - 1) text = text[..(width - 1)];
        string padded = text.PadRight(Math.Max(text.Length, _lastLength));
        _lastLength = text.Length;
        _ui.WriteInline("\r" + padded);
    }

    async Task SpinAsync(CancellationToken ct)
    {
        int frame = 0;
        try
        {
            while (!ct.IsCancellationRequested && !_stopped)
            {
                Draw(Render(frame));
                frame++;
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stopped = true;
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
        {
            string finalText = _succeeded ? $"{_label}... done" : $"{_label}... failed";
            string padded = finalText.PadRight(Math.Max(finalText.Length, _lastLength));
            _ui.WriteInline("\r" + padded + "\n");
        }
    }
}

static class PromptHelper
{
    public static bool ParseYesNo(string input)
    {
        string normalized = (input ?? string.Empty).Trim().ToLowerInvariant();
        return normalized == "y" || normalized == "yes";
    }
}

sealed class OutputLockHandle : IDisposable
{
    readonly FileStream _stream;

    OutputLockHandle(FileStream stream) { _stream = stream; }

    public static OutputLockHandle? Acquire(string lockPath, BuildLogger logger, out string? error)
    {
        error = null;
        try
        {
            var fs = new FileStream(lockPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            byte[] info = Encoding.UTF8.GetBytes($"pid={Environment.ProcessId}");
            fs.Write(info, 0, info.Length);
            fs.Flush();
            return new OutputLockHandle(fs);
        }
        catch (IOException ex)
        {
            error = $"Another build appears to be writing to this output path (lock: {lockPath}): {ex.Message}";
            logger.Error(error);
            return null;
        }
        catch (Exception ex)
        {
            error = $"Cannot create the output lock file {lockPath}: {ex.Message}";
            logger.Error(error);
            return null;
        }
    }

    public void Dispose()
    {
        try { _stream.Dispose(); } catch { }
    }
}

sealed class BuilderBundle : IDisposable
{
    public CDBuilder Builder { get; }
    public List<Stream> Streams { get; } = new();
    public string? ExtraEfiIsoPath { get; set; }

    public BuilderBundle(CDBuilder builder) { Builder = builder; }

    public void Dispose()
    {
        foreach (Stream s in Streams)
            try { s.Dispose(); } catch { }
        Streams.Clear();
    }
}

static class IsoBuilderService
{
    public static void BuildIso(BuildContext ctx)
    {
        BuildConfig cfg = ctx.Config;
        string outPath = cfg.IsoOutputPath;
        string tempPath = cfg.TempIsoPath;
        string extraEfi = string.Empty;

        try
        {
            using (BuilderBundle bundle = CreateBuilder(ctx))
            {
                extraEfi = bundle.ExtraEfiIsoPath ?? string.Empty;
                ctx.State.ExtraEfiIsoPath = extraEfi;

                using (Stream isoStream = bundle.Builder.Build())
                {
                    if (isoStream == null) throw new InvalidOperationException("CDBuilder.Build() returned null stream.");

                    long total = 0;
                    try { total = isoStream.Length; }
                    catch (NotSupportedException) { }
                    ctx.State.ExactSizeBytes = total;

                    if (cfg.MaxSizeMb > 0 && total > cfg.MaxSizeMb * 1024L * 1024L)
                        throw new InvalidOperationException($"Size guard triggered: ISO layout is {TextSanitizer.FormatBytes(total)}, exceeding the configured limit of {cfg.MaxSizeMb} MB (--max-size).");

                    using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan);
                    byte[] buffer = new byte[1024 * 1024];
                    long done = 0;
                    int read;
                    while ((read = isoStream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ctx.CancellationToken.ThrowIfCancellationRequested();
                        fileStream.Write(buffer, 0, read);
                        done += read;
                        ctx.Progress?.Report(done, total);
                    }
                    fileStream.Flush(true);
                }
            }

            if (cfg.IncludeUefi)
                BootCatalogPatcher.Apply(tempPath, cfg, extraEfi.Length > 0 ? extraEfi : null, ctx.Logger);

            if (cfg.Reproducible)
                IsoTimestampNormalizer.Apply(tempPath, DateTimeOffset.FromUnixTimeSeconds(cfg.SourceDateEpoch).UtcDateTime, ctx.Logger);

            ctx.CancellationToken.ThrowIfCancellationRequested();

            string? backup = SafeReplace(tempPath, outPath, cfg.BackupIsoPath, !ctx.Opts.NoOverwrite, ctx.Logger);
            ctx.State.BackupPath = backup ?? string.Empty;
            ctx.State.OutputInstalled = true;
            ctx.State.ActualSizeBytes = new FileInfo(outPath).Length;
        }
        catch (OperationCanceledException)
        {
            TryDelete(tempPath, ctx.Logger);
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(tempPath, ctx.Logger);
            ctx.State.Fail($"ISO build failed: {ex.Message}");
            ctx.Logger.Error($"ISO build exception: {ex.Message}");
        }
    }

    public static void Simulate(BuildContext ctx)
    {
        BuildConfig cfg = ctx.Config;
        using BuilderBundle bundle = CreateBuilder(ctx);
        using Stream stream = bundle.Builder.Build();
        if (stream == null) throw new InvalidOperationException("CDBuilder.Build() returned null stream.");

        long length = stream.Length;
        ctx.State.ExactSizeBytes = length;
        ctx.State.ExtraEfiIsoPath = bundle.ExtraEfiIsoPath ?? string.Empty;
        ctx.Logger.Log($"Dry run: exact ISO layout size {length} bytes.");

        if (cfg.MaxSizeMb > 0 && length > cfg.MaxSizeMb * 1024L * 1024L)
        {
            ctx.State.Fail($"Size guard triggered: ISO layout is {TextSanitizer.FormatBytes(length)}, exceeding the configured limit of {cfg.MaxSizeMb} MB (--max-size).");
            return;
        }

        if (!stream.CanSeek) return;

        try
        {
            using var reader = new IsoLbaReader(stream, true);
            var report = new IsoVerificationReport();
            IsoVerifier.VerifyStructure(reader, length, true, report);
            foreach (string warning in report.Warnings) ctx.Logger.Warn(warning);
            if (!report.Ok)
            {
                foreach (string failure in report.Failures) ctx.Logger.Error(failure);
                ctx.State.FailValidation($"Dry run layout validation failed: {report.Failures[0]}");
                return;
            }

            IsoBootCatalog? catalog = reader.ReadBootCatalog();
            if (catalog != null)
                ctx.Logger.Log($"Dry run: boot catalog at sector {catalog.Lba} with {catalog.Entries.Count} entr{(catalog.Entries.Count == 1 ? "y" : "ies")}.");
            else if (cfg.BiosEnabled || cfg.IncludeUefi)
                ctx.Logger.Warn("Dry run: the simulated layout contains no boot catalog.");
        }
        catch (Exception ex)
        {
            ctx.Logger.Warn($"Dry run could not inspect the simulated layout: {ex.Message}");
        }
    }

    static BuilderBundle CreateBuilder(BuildContext ctx)
    {
        BuildConfig cfg = ctx.Config;
        FileInventory inv = ctx.State.Inventory ?? throw new InvalidOperationException("File inventory is missing.");

        var builder = new CDBuilder
        {
            UseJoliet = true,
            VolumeIdentifier = cfg.VolumeId
        };
        var bundle = new BuilderBundle(builder);

        try
        {
            int processed = 0;
            FileTreeScanner.AddToBuilder(builder, inv, cfg, ctx.Logger, ref processed, ctx.CancellationToken, ctx.State);
            ctx.State.ProcessedFiles = processed;
            if (ctx.State.FailedFiles > 0)
                throw new InvalidOperationException($"{ctx.State.FailedFiles} file(s) could not be added to the ISO; refusing to produce an incomplete image.");

            bool hasUefi = cfg.IncludeUefi;
            bool hasBios = cfg.BiosEnabled;
            if (hasUefi && !File.Exists(cfg.EfiImgPath)) throw new FileNotFoundException($"efiboot.img not found: {cfg.EfiImgPath}");
            if (hasBios && !File.Exists(cfg.BootBinPath)) throw new FileNotFoundException($"boot.bin not found: {cfg.BootBinPath}");

            if (hasBios && hasUefi)
            {
                var bios = new FileStream(cfg.BootBinPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                bundle.Streams.Add(bios);
                builder.SetBootImage(bios, BootDeviceEmulation.NoEmulation, 0);

                var used = new HashSet<string>(inv.Files.Select(f => f.IsoPath), StringComparer.OrdinalIgnoreCase);
                string efiIso = BuildPaths.EfiExtraIsoPath;
                int counter = 1;
                while (used.Contains(efiIso)) efiIso = $"BOOT/EFIBOOT_{counter++}.IMG";
                builder.AddFile(FileTreeScanner.ToBuilderPath(efiIso), cfg.EfiImgPath);
                bundle.ExtraEfiIsoPath = efiIso;

                string order = cfg.BootPriority == BootPriorityMode.PreferUefi ? "UEFI first, BIOS second" : "BIOS first, UEFI second";
                ctx.Logger.Log($"Both BIOS and UEFI boot images are present; writing a multi-entry El Torito catalog ({order}).");
                if (!ctx.Opts.Silent) ctx.Ui.WriteLine($"Boot: BIOS and UEFI entries will both be embedded ({order}).");
            }
            else if (hasUefi)
            {
                var efi = new FileStream(cfg.EfiImgPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                bundle.Streams.Add(efi);
                builder.SetBootImage(efi, BootDeviceEmulation.NoEmulation, 0);
            }
            else if (hasBios)
            {
                var bios = new FileStream(cfg.BootBinPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                bundle.Streams.Add(bios);
                builder.SetBootImage(bios, BootDeviceEmulation.NoEmulation, 0);
            }
            else
            {
                ctx.Logger.Warn("No boot image configured. ISO will not be bootable.");
            }

            return bundle;
        }
        catch
        {
            bundle.Dispose();
            throw;
        }
    }

    public static void Commit(BuildContext ctx)
    {
        if (!ctx.State.OutputInstalled) return;
        string backup = ctx.State.BackupPath;
        if (backup.Length == 0 || !File.Exists(backup)) return;

        if (ctx.Opts.KeepBackup)
        {
            ctx.Logger.Log($"Previous ISO kept as backup: {backup}");
            return;
        }

        TryDelete(backup, ctx.Logger);
    }

    public static void Rollback(BuildContext ctx)
    {
        if (!ctx.State.OutputInstalled) return;
        BuildConfig cfg = ctx.Config;

        try
        {
            if (File.Exists(cfg.IsoOutputPath))
            {
                File.Move(cfg.IsoOutputPath, cfg.FailedIsoPath, true);
                ctx.Logger.Warn($"Unverified ISO moved to {cfg.FailedIsoPath}.");
            }

            string backup = ctx.State.BackupPath;
            if (backup.Length > 0 && File.Exists(backup))
            {
                File.Move(backup, cfg.IsoOutputPath, true);
                ctx.Logger.Log($"Previous ISO restored: {cfg.IsoOutputPath}");
            }

            ctx.State.OutputInstalled = false;
        }
        catch (Exception ex)
        {
            ctx.Logger.Error($"Rollback failed: {ex.Message}");
        }
    }

    static string? SafeReplace(string tempPath, string destPath, string backupPath, bool allowOverwrite, BuildLogger logger)
    {
        if (Directory.Exists(destPath)) throw new IOException($"Output path is a directory: {destPath}");
        bool hadExisting = File.Exists(destPath);
        if (hadExisting && !allowOverwrite) throw new IOException($"Output file already exists and --no-overwrite is set: {destPath}");

        string? backup = null;
        if (hadExisting)
        {
            MoveWithRetry(destPath, backupPath, true, logger, "back up the existing ISO");
            backup = backupPath;
        }

        try
        {
            try
            {
                MoveWithRetry(tempPath, destPath, false, logger, "install the new ISO");
            }
            catch (Exception moveEx)
            {
                logger.Warn($"File.Move failed, falling back to copy: {moveEx.Message}");
                CopyFile(tempPath, destPath);
                TryDelete(tempPath, logger);
            }
        }
        catch
        {
            TryDelete(destPath, logger);
            if (backup != null)
            {
                try { File.Move(backup, destPath, true); }
                catch (Exception restoreEx) { logger.Error($"Failed to restore previous ISO from {backup}: {restoreEx.Message}"); }
            }
            throw;
        }

        return backup;
    }

    static void MoveWithRetry(string source, string destination, bool overwrite, BuildLogger logger, string action)
    {
        const int maxRetries = 3;
        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                File.Move(source, destination, overwrite);
                return;
            }
            catch (IOException) when (i < maxRetries - 1)
            {
                logger.Warn($"Attempt {i + 1} to {action} failed, retrying...");
                Thread.Sleep(100 * (i + 1));
            }
        }
    }

    static void CopyFile(string source, string destination)
    {
        using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        using var dst = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan);
        src.CopyTo(dst);
        dst.Flush(true);
    }

    public static void TryDelete(string path, BuildLogger logger)
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

static class BootCatalogPatcher
{
    public static void Apply(string isoPath, BuildConfig cfg, string? extraEfiIsoPath, BuildLogger logger)
    {
        bool hasBios = cfg.BiosEnabled;
        bool hasUefi = cfg.IncludeUefi;
        if (!hasUefi) return;

        long efiBytes = new FileInfo(cfg.EfiImgPath).Length;
        ushort efiSectors = (ushort)Math.Min(65535L, Math.Max(1L, (efiBytes + 511) / 512));

        using var fs = new FileStream(isoPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var reader = new IsoLbaReader(fs, true);

        IsoBootCatalog? catalog = reader.ReadBootCatalog();
        if (catalog == null || catalog.Entries.Count == 0)
            throw new InvalidDataException("No El Torito boot catalog was found in the generated ISO.");

        BootCatalogEntry defaultEntry = catalog.Entries[0];
        byte[] newCatalog;

        if (hasBios && hasUefi)
        {
            IsoFileEntry? efiFile = null;
            if (extraEfiIsoPath != null) reader.TryResolve(extraEfiIsoPath, out efiFile);
            if (efiFile == null)
                throw new InvalidDataException("The EFI boot image could not be located inside the generated ISO.");
            if (efiFile.DataLba > uint.MaxValue)
                throw new InvalidDataException("The EFI boot image is located beyond the addressable El Torito range.");

            var bios = new BootCatalogEntry
            {
                PlatformId = 0x00,
                Bootable = true,
                MediaType = 0,
                LoadSegment = defaultEntry.LoadSegment,
                SystemType = 0,
                SectorCount = defaultEntry.SectorCount,
                LoadRba = defaultEntry.LoadRba
            };
            var efi = new BootCatalogEntry
            {
                PlatformId = 0xEF,
                Bootable = true,
                MediaType = 0,
                LoadSegment = 0,
                SystemType = 0,
                SectorCount = efiSectors,
                LoadRba = (uint)efiFile.DataLba
            };

            newCatalog = cfg.BootPriority == BootPriorityMode.PreferUefi
                ? BootCatalogBuilder.Build(efi, bios)
                : BootCatalogBuilder.Build(bios, efi);
        }
        else
        {
            var efi = new BootCatalogEntry
            {
                PlatformId = 0xEF,
                Bootable = true,
                MediaType = 0,
                LoadSegment = 0,
                SystemType = 0,
                SectorCount = efiSectors,
                LoadRba = defaultEntry.LoadRba
            };
            newCatalog = BootCatalogBuilder.Build(efi, null);
        }

        fs.Seek(catalog.Lba * IsoLbaReader.SectorSize, SeekOrigin.Begin);
        fs.Write(newCatalog, 0, newCatalog.Length);
        fs.Flush(true);
        logger.Log($"El Torito boot catalog rewritten at sector {catalog.Lba}.");
    }
}
sealed class IsoFileEntry
{
    public string Path { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public long ExtentLba { get; init; }
    public long DataLba { get; init; }
    public long Length { get; init; }
    public bool IsDirectory { get; init; }
    public bool IsMultiExtent { get; init; }
    public byte Flags { get; init; }
}

sealed class IsoVolumeDescriptor
{
    public byte Type { get; init; }
    public string Identifier { get; init; } = string.Empty;
    public byte Version { get; init; }
    public long Lba { get; init; }
    public byte[] Data { get; init; } = Array.Empty<byte>();
}

sealed class BootCatalogEntry
{
    public byte PlatformId { get; init; }
    public bool Bootable { get; init; }
    public byte MediaType { get; init; }
    public ushort LoadSegment { get; init; }
    public byte SystemType { get; init; }
    public ushort SectorCount { get; init; }
    public uint LoadRba { get; init; }
}

sealed class IsoBootCatalog
{
    public long Lba { get; init; }
    public bool ValidationOk { get; init; }
    public byte ValidationPlatform { get; init; }
    public IReadOnlyList<BootCatalogEntry> Entries { get; init; } = Array.Empty<BootCatalogEntry>();
}

sealed class IsoExtentStream : Stream
{
    readonly IsoLbaReader _reader;
    readonly long _start;
    readonly long _length;
    long _position;

    public IsoExtentStream(IsoLbaReader reader, long startByte, long length)
    {
        _reader = reader;
        _start = startByte;
        _length = length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            _position = value;
        }
    }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (buffer == null) throw new ArgumentNullException(nameof(buffer));
        if (offset < 0 || count < 0 || offset + count > buffer.Length) throw new ArgumentOutOfRangeException(nameof(count));
        long remaining = _length - _position;
        if (remaining <= 0 || count == 0) return 0;
        int toRead = (int)Math.Min(remaining, count);
        int read = _reader.ReadAt(_start + _position, buffer, offset, toRead);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        if (target < 0) throw new IOException("Attempt to seek before the beginning of the extent.");
        _position = target;
        return _position;
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

sealed class IsoLbaReader : IDisposable
{
    public const int SectorSize = 2048;
    const int FirstDescriptorLba = 16;
    const int MaxDescriptors = 64;
    const long MaxDirectoryBytes = 256L * 1024 * 1024;
    const int MaxEntries = 5_000_000;
    const int MaxDepth = 128;

    readonly Stream _stream;
    readonly bool _leaveOpen;
    readonly object _gate = new();
    readonly List<IsoVolumeDescriptor> _descriptors = new();
    IsoVolumeDescriptor? _primary;
    IsoVolumeDescriptor? _joliet;
    IsoVolumeDescriptor? _bootRecord;
    List<IsoFileEntry>? _entries;
    Dictionary<string, IsoFileEntry>? _index;
    bool _disposed;

    public IsoLbaReader(Stream stream, bool leaveOpen = false)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        if (!stream.CanRead || !stream.CanSeek) throw new ArgumentException("The stream must be readable and seekable.", nameof(stream));
        _leaveOpen = leaveOpen;
        ParseDescriptors();
    }

    public static IsoLbaReader Open(string path)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.RandomAccess);
        try { return new IsoLbaReader(fs, false); }
        catch { fs.Dispose(); throw; }
    }

    public IReadOnlyList<IsoVolumeDescriptor> Descriptors => _descriptors;
    public bool HasTerminator { get; private set; }
    public bool HasJoliet => _joliet != null;
    public bool HasBootRecord => _bootRecord != null;
    public long StreamLength => _stream.Length;
    public long TotalSectors => (_stream.Length + SectorSize - 1) / SectorSize;
    public long VolumeSpaceSizeSectors => BinaryPrimitives.ReadUInt32LittleEndian(_primary!.Data.AsSpan(80, 4));

    public string VolumeId => Encoding.Latin1.GetString(_primary!.Data, 40, 32).TrimEnd(' ', '\0');

    public int ReadAt(long byteOffset, byte[] buffer, int offset, int count)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(IsoLbaReader));
        lock (_gate)
        {
            _stream.Seek(byteOffset, SeekOrigin.Begin);
            int total = 0;
            while (total < count)
            {
                int n = _stream.Read(buffer, offset + total, count - total);
                if (n <= 0) break;
                total += n;
            }
            return total;
        }
    }

    public byte[] ReadSector(long lba) => ReadSectors(lba, 1);

    public byte[] ReadSectors(long lba, long count)
    {
        if (lba < 0 || count <= 0) throw new InvalidDataException($"Invalid sector range (LBA {lba}, count {count}).");
        long bytes = count * SectorSize;
        if (bytes > int.MaxValue) throw new InvalidDataException("Sector range is too large to read at once.");
        if (lba * SectorSize >= _stream.Length) throw new InvalidDataException($"LBA {lba} is beyond the end of the image.");
        byte[] buffer = new byte[bytes];
        ReadAt(lba * SectorSize, buffer, 0, (int)bytes);
        return buffer;
    }

    public byte[] ReadBytes(long lba, long offset, int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        long start = lba * SectorSize + offset;
        if (start < 0 || start >= _stream.Length) throw new InvalidDataException($"Byte offset {start} is beyond the end of the image.");
        byte[] buffer = new byte[count];
        int read = ReadAt(start, buffer, 0, count);
        if (read < count) Array.Resize(ref buffer, read);
        return buffer;
    }

    public byte[] ReadExtent(long lba, long length)
    {
        long sectors = (length + SectorSize - 1) / SectorSize;
        if (sectors <= 0) sectors = 1;
        return ReadSectors(lba, sectors);
    }

    static bool IsJoliet(byte[] d) => d[88] == (byte)'%' && d[89] == (byte)'/' && (d[90] == (byte)'@' || d[90] == (byte)'C' || d[90] == (byte)'E');

    static bool IsElTorito(byte[] d) => Encoding.ASCII.GetString(d, 7, 23) == "EL TORITO SPECIFICATION";

    void ParseDescriptors()
    {
        for (int i = 0; i < MaxDescriptors; i++)
        {
            long lba = FirstDescriptorLba + i;
            if (lba * SectorSize >= _stream.Length)
            {
                if (i == 0) throw new InvalidDataException("The image is too small to contain an ISO 9660 volume descriptor.");
                break;
            }

            byte[] data = ReadSector(lba);
            if (data[1] != (byte)'C' || data[2] != (byte)'D' || data[3] != (byte)'0' || data[4] != (byte)'0' || data[5] != (byte)'1')
            {
                if (i == 0) throw new InvalidDataException("ISO 9660 signature not found at sector 16.");
                break;
            }

            var descriptor = new IsoVolumeDescriptor { Type = data[0], Identifier = "CD001", Version = data[6], Lba = lba, Data = data };
            _descriptors.Add(descriptor);

            if (descriptor.Type == 255) { HasTerminator = true; break; }
            if (descriptor.Type == 1 && _primary == null) _primary = descriptor;
            else if (descriptor.Type == 2 && _joliet == null && IsJoliet(data)) _joliet = descriptor;
            else if (descriptor.Type == 0 && _bootRecord == null && IsElTorito(data)) _bootRecord = descriptor;
        }

        if (_primary == null) throw new InvalidDataException("Primary volume descriptor not found.");

        ushort blockSize = BinaryPrimitives.ReadUInt16LittleEndian(_primary.Data.AsSpan(128, 2));
        if (blockSize != SectorSize) throw new NotSupportedException($"Unsupported logical block size: {blockSize}.");
    }

    public static IEnumerable<int> EnumerateRecordOffsets(byte[] data, long limit)
    {
        int end = (int)Math.Min(limit, data.Length);
        int pos = 0;
        while (pos < end)
        {
            int len = data[pos];
            if (len == 0)
            {
                pos = (pos / SectorSize + 1) * SectorSize;
                continue;
            }
            if (len < 34 || pos + len > data.Length) throw new InvalidDataException($"Corrupt directory record at offset {pos}.");
            yield return pos;
            pos += len;
        }
    }

    static string DecodeName(byte[] data, int offset, int length, bool joliet, bool isDirectory)
    {
        string name = joliet
            ? Encoding.BigEndianUnicode.GetString(data, offset, length & ~1)
            : Encoding.Latin1.GetString(data, offset, length);

        if (!isDirectory)
        {
            int semi = name.LastIndexOf(';');
            if (semi >= 0 && semi < name.Length - 1 && name[(semi + 1)..].All(char.IsDigit)) name = name[..semi];
            if (!joliet && name.Length > 1 && name[^1] == '.') name = name[..^1];
        }
        return name;
    }

    List<IsoFileEntry> ReadDirectory(long extentLba, long length, string parentPath, bool joliet)
    {
        var result = new List<IsoFileEntry>();
        if (length <= 0) return result;
        if (length > MaxDirectoryBytes) throw new InvalidDataException("Directory extent is unreasonably large.");

        byte[] data = ReadExtent(extentLba, length);
        foreach (int pos in EnumerateRecordOffsets(data, length))
        {
            byte extAttr = data[pos + 1];
            long extent = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 2, 4));
            long dataLength = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 10, 4));
            byte flags = data[pos + 25];
            int nameLength = data[pos + 32];
            if (33 + nameLength > data[pos]) throw new InvalidDataException($"Corrupt directory record name at offset {pos}.");
            if (nameLength == 1 && (data[pos + 33] == 0 || data[pos + 33] == 1)) continue;

            bool isDirectory = (flags & 0x02) != 0;
            string name = DecodeName(data, pos + 33, nameLength, joliet, isDirectory);
            result.Add(new IsoFileEntry
            {
                Path = parentPath.Length == 0 ? name : parentPath + "/" + name,
                Name = name,
                ExtentLba = extent,
                DataLba = extent + extAttr,
                Length = dataLength,
                IsDirectory = isDirectory,
                IsMultiExtent = (flags & 0x80) != 0,
                Flags = flags
            });
        }
        return result;
    }

    public (long Lba, long Length) GetRootExtent(bool joliet)
    {
        IsoVolumeDescriptor? tree = joliet ? _joliet : _primary;
        if (tree == null) throw new InvalidDataException("The requested directory tree does not exist.");
        long lba = BinaryPrimitives.ReadUInt32LittleEndian(tree.Data.AsSpan(156 + 2, 4));
        long length = BinaryPrimitives.ReadUInt32LittleEndian(tree.Data.AsSpan(156 + 10, 4));
        return (lba, length);
    }

    public IReadOnlyList<IsoFileEntry> ReadTree(bool joliet)
    {
        var result = new List<IsoFileEntry>();
        if ((joliet ? _joliet : _primary) == null) return result;

        var (rootLba, rootLength) = GetRootExtent(joliet);
        var visited = new HashSet<long> { rootLba };
        var queue = new Queue<(long Lba, long Length, string Path, int Depth)>();
        queue.Enqueue((rootLba, rootLength, string.Empty, 0));

        while (queue.Count > 0)
        {
            var (lba, length, path, depth) = queue.Dequeue();
            foreach (IsoFileEntry entry in ReadDirectory(lba, length, path, joliet))
            {
                if (result.Count >= MaxEntries) throw new InvalidDataException("The image contains too many directory entries.");
                result.Add(entry);
                if (!entry.IsDirectory) continue;
                if (depth + 1 > MaxDepth) throw new InvalidDataException("Directory nesting is too deep.");
                if (visited.Add(entry.ExtentLba)) queue.Enqueue((entry.ExtentLba, entry.Length, entry.Path, depth + 1));
            }
        }
        return result;
    }

    public IReadOnlyList<IsoFileEntry> ReadAllEntries()
    {
        _entries ??= new List<IsoFileEntry>(ReadTree(_joliet != null));
        return _entries;
    }

    public static string NormalizeKey(string path)
    {
        string[] parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            string trimmed = parts[i].Length > 1 ? parts[i].TrimEnd('.') : parts[i];
            parts[i] = trimmed.Length == 0 ? parts[i] : trimmed;
        }
        return string.Join("/", parts).ToLowerInvariant();
    }

    public Dictionary<string, IsoFileEntry> GetFileIndex()
    {
        if (_index != null) return _index;
        var index = new Dictionary<string, IsoFileEntry>(StringComparer.Ordinal);
        foreach (IsoFileEntry entry in ReadAllEntries())
            if (!entry.IsDirectory) index.TryAdd(NormalizeKey(entry.Path), entry);
        _index = index;
        return index;
    }

    public bool TryResolve(string path, out IsoFileEntry? entry) => GetFileIndex().TryGetValue(NormalizeKey(path), out entry);

    public Stream OpenRead(IsoFileEntry entry)
    {
        if (entry.IsDirectory) throw new InvalidOperationException("Cannot open a directory as a file.");
        if (entry.IsMultiExtent) throw new NotSupportedException("Multi-extent files are not supported.");
        long start = entry.DataLba * SectorSize;
        if (start < 0 || start + entry.Length > _stream.Length)
            throw new InvalidDataException($"File extent of '{entry.Path}' lies outside of the image.");
        return new IsoExtentStream(this, start, entry.Length);
    }

    public IsoBootCatalog? ReadBootCatalog()
    {
        if (_bootRecord == null) return null;
        long catalogLba = BinaryPrimitives.ReadUInt32LittleEndian(_bootRecord.Data.AsSpan(71, 4));
        if (catalogLba <= 0 || catalogLba * SectorSize >= _stream.Length) return null;

        byte[] cat = ReadSector(catalogLba);
        int sum = 0;
        for (int i = 0; i < 32; i += 2) sum += cat[i] | (cat[i + 1] << 8);
        bool validationOk = cat[0] == 1 && cat[30] == 0x55 && cat[31] == 0xAA && (sum & 0xFFFF) == 0;
        byte validationPlatform = cat[1];

        var entries = new List<BootCatalogEntry> { ParseEntry(cat, 32, validationPlatform) };

        int pos = 64;
        while (pos + 32 <= cat.Length)
        {
            byte indicator = cat[pos];
            if (indicator != 0x90 && indicator != 0x91) break;

            byte platform = cat[pos + 1];
            int count = BinaryPrimitives.ReadUInt16LittleEndian(cat.AsSpan(pos + 2, 2));
            pos += 32;
            for (int i = 0; i < count && pos + 32 <= cat.Length; i++)
            {
                entries.Add(ParseEntry(cat, pos, platform));
                pos += 32;
                while (pos + 32 <= cat.Length && cat[pos] == 0x44) pos += 32;
            }
            if (indicator == 0x91) break;
        }

        return new IsoBootCatalog
        {
            Lba = catalogLba,
            ValidationOk = validationOk,
            ValidationPlatform = validationPlatform,
            Entries = entries
        };
    }

    static BootCatalogEntry ParseEntry(byte[] cat, int pos, byte platform) => new()
    {
        PlatformId = platform,
        Bootable = cat[pos] == 0x88,
        MediaType = (byte)(cat[pos + 1] & 0x0F),
        LoadSegment = BinaryPrimitives.ReadUInt16LittleEndian(cat.AsSpan(pos + 2, 2)),
        SystemType = cat[pos + 4],
        SectorCount = BinaryPrimitives.ReadUInt16LittleEndian(cat.AsSpan(pos + 6, 2)),
        LoadRba = BinaryPrimitives.ReadUInt32LittleEndian(cat.AsSpan(pos + 8, 4))
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_leaveOpen) _stream.Dispose();
    }
}

static class BootCatalogBuilder
{
    public static byte[] Build(BootCatalogEntry first, BootCatalogEntry? second)
    {
        byte[] cat = new byte[IsoLbaReader.SectorSize];
        cat[0] = 1;
        cat[1] = first.PlatformId;
        cat[30] = 0x55;
        cat[31] = 0xAA;

        int sum = 0;
        for (int i = 0; i < 32; i += 2) sum += cat[i] | (cat[i + 1] << 8);
        ushort checksum = (ushort)((0x10000 - (sum & 0xFFFF)) & 0xFFFF);
        BinaryPrimitives.WriteUInt16LittleEndian(cat.AsSpan(28, 2), checksum);

        WriteEntry(cat, 32, first);

        if (second != null)
        {
            cat[64] = 0x91;
            cat[65] = second.PlatformId;
            BinaryPrimitives.WriteUInt16LittleEndian(cat.AsSpan(66, 2), 1);
            WriteEntry(cat, 96, second);
        }

        return cat;
    }

    static void WriteEntry(byte[] cat, int pos, BootCatalogEntry entry)
    {
        cat[pos] = entry.Bootable ? (byte)0x88 : (byte)0x00;
        cat[pos + 1] = (byte)(entry.MediaType & 0x0F);
        BinaryPrimitives.WriteUInt16LittleEndian(cat.AsSpan(pos + 2, 2), entry.LoadSegment);
        cat[pos + 4] = entry.SystemType;
        BinaryPrimitives.WriteUInt16LittleEndian(cat.AsSpan(pos + 6, 2), entry.SectorCount);
        BinaryPrimitives.WriteUInt32LittleEndian(cat.AsSpan(pos + 8, 4), entry.LoadRba);
    }
}

static class IsoTimestampNormalizer
{
    static byte[] BuildRecordDate(DateTime utc) => new[]
    {
        (byte)(utc.Year - 1900), (byte)utc.Month, (byte)utc.Day,
        (byte)utc.Hour, (byte)utc.Minute, (byte)utc.Second, (byte)0
    };

    public static void Apply(string isoPath, DateTime utc, BuildLogger logger)
    {
        if (utc.Year < 1900 || utc.Year > 2155) throw new ArgumentOutOfRangeException(nameof(utc), "The ISO 9660 date range is 1900 to 2155.");

        using var fs = new FileStream(isoPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var reader = new IsoLbaReader(fs, true);

        byte[] volumeDate = Encoding.ASCII.GetBytes(utc.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "00");
        byte[] recordDate = BuildRecordDate(utc);
        int patchedRecords = 0;

        foreach (IsoVolumeDescriptor descriptor in reader.Descriptors)
        {
            if (descriptor.Type != 1 && descriptor.Type != 2) continue;
            byte[] sector = (byte[])descriptor.Data.Clone();
            foreach (int offset in new[] { 813, 830 })
            {
                Buffer.BlockCopy(volumeDate, 0, sector, offset, 16);
                sector[offset + 16] = 0;
            }
            Buffer.BlockCopy(recordDate, 0, sector, 156 + 18, 7);
            fs.Seek(descriptor.Lba * IsoLbaReader.SectorSize, SeekOrigin.Begin);
            fs.Write(sector, 0, sector.Length);
        }

        foreach (bool joliet in new[] { false, true })
        {
            if (joliet && !reader.HasJoliet) continue;
            var extents = new List<(long Lba, long Length)> { reader.GetRootExtent(joliet) };
            foreach (IsoFileEntry entry in reader.ReadTree(joliet))
                if (entry.IsDirectory) extents.Add((entry.ExtentLba, entry.Length));

            foreach (var (lba, length) in extents)
            {
                byte[] data = reader.ReadExtent(lba, length);
                foreach (int pos in IsoLbaReader.EnumerateRecordOffsets(data, length))
                {
                    Buffer.BlockCopy(recordDate, 0, data, pos + 18, 7);
                    patchedRecords++;
                }
                fs.Seek(lba * IsoLbaReader.SectorSize, SeekOrigin.Begin);
                fs.Write(data, 0, data.Length);
            }
        }

        fs.Flush(true);
        logger.Log($"Normalized ISO timestamps to {utc:O} ({patchedRecords} directory records).");
    }
}

sealed class IsoVerificationReport
{
    const int MaxStoredFailures = 50;
    int _suppressed;

    public List<string> Failures { get; } = new();
    public List<string> Warnings { get; } = new();
    public List<BootCatalogEntry> BootEntries { get; } = new();
    public List<(string IsoPath, string Sha256, long Size)> ContentHashes { get; } = new();
    public int FilesChecked { get; set; }
    public int FilesMissing { get; set; }
    public int SizeMismatches { get; set; }
    public int HashMismatches { get; set; }
    public long TotalBytes { get; set; }
    public bool Ok => Failures.Count == 0;

    public void AddFailure(string message)
    {
        if (Failures.Count < MaxStoredFailures) Failures.Add(message);
        else
        {
            _suppressed++;
            if (_suppressed == 1) Failures.Add("Further failures were suppressed.");
        }
    }
}

static class IsoVerifier
{
    public static void VerifyStructure(IsoLbaReader reader, long fileLength, bool expectJoliet, IsoVerificationReport report)
    {
        if (reader.VolumeSpaceSizeSectors * IsoLbaReader.SectorSize > fileLength)
            report.AddFailure($"The volume declares {reader.VolumeSpaceSizeSectors} sectors but the image holds only {fileLength / IsoLbaReader.SectorSize}; the image is truncated.");
        if (fileLength % IsoLbaReader.SectorSize != 0)
            report.Warnings.Add($"The image size ({fileLength} bytes) is not a multiple of {IsoLbaReader.SectorSize}.");
        if (!reader.HasTerminator)
            report.AddFailure("The volume descriptor set terminator is missing.");
        if (expectJoliet && !reader.HasJoliet)
            report.AddFailure("The Joliet extension is missing from the image.");

        try { reader.ReadTree(false); }
        catch (Exception ex) { report.AddFailure($"The primary directory tree is unreadable: {ex.Message}"); }

        if (reader.HasJoliet)
        {
            try { reader.ReadTree(true); }
            catch (Exception ex) { report.AddFailure($"The Joliet directory tree is unreadable: {ex.Message}"); }
        }
    }

    public static void VerifyBoot(IsoLbaReader reader, BuildConfig? cfg, IsoVerificationReport report)
    {
        bool expectBoot = cfg != null && (cfg.BiosEnabled || cfg.IncludeUefi);
        IsoBootCatalog? catalog;
        try { catalog = reader.ReadBootCatalog(); }
        catch (Exception ex)
        {
            report.AddFailure($"The El Torito boot catalog is unreadable: {ex.Message}");
            return;
        }

        if (catalog == null)
        {
            if (expectBoot) report.AddFailure("No El Torito boot catalog was found although boot images were configured.");
            return;
        }

        report.BootEntries.AddRange(catalog.Entries);
        if (!catalog.ValidationOk) report.AddFailure("The El Torito validation entry is invalid (header, key bytes or checksum).");
        if (catalog.Entries.Count == 0) report.AddFailure("The El Torito catalog has no boot entries.");

        foreach (BootCatalogEntry entry in catalog.Entries)
        {
            if (entry.LoadRba >= reader.TotalSectors)
                report.AddFailure($"A boot entry (platform 0x{entry.PlatformId:X2}) points to sector {entry.LoadRba}, beyond the end of the image.");
            if (!entry.Bootable)
                report.Warnings.Add($"A boot entry (platform 0x{entry.PlatformId:X2}) is not marked bootable.");
        }

        if (cfg == null) return;

        if (cfg.BiosEnabled)
        {
            BootCatalogEntry? bios = catalog.Entries.FirstOrDefault(e => e.PlatformId == 0x00);
            if (bios == null) report.AddFailure("No BIOS (x86) boot entry was found in the catalog.");
            else CompareHead(reader, bios.LoadRba, cfg.BootBinPath, "BIOS", report);
        }

        if (cfg.IncludeUefi)
        {
            BootCatalogEntry? efi = catalog.Entries.FirstOrDefault(e => e.PlatformId == 0xEF);
            if (efi == null) report.AddFailure("No UEFI (0xEF) boot entry was found in the catalog.");
            else CompareHead(reader, efi.LoadRba, cfg.EfiImgPath, "UEFI", report);
        }
    }

    static void CompareHead(IsoLbaReader reader, uint lba, string sourcePath, string label, IsoVerificationReport report)
    {
        try
        {
            byte[] expected = new byte[512];
            int expectedLength;
            using (var fs = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                expectedLength = fs.Read(expected, 0, expected.Length);
            if (expectedLength <= 0) return;

            byte[] actual = reader.ReadBytes(lba, 0, expectedLength);
            if (actual.Length < expectedLength || !actual.AsSpan(0, expectedLength).SequenceEqual(expected.AsSpan(0, expectedLength)))
                report.AddFailure($"The {label} boot image stored in the ISO differs from {Path.GetFileName(sourcePath)}.");
        }
        catch (Exception ex)
        {
            report.AddFailure($"Could not compare the {label} boot image: {ex.Message}");
        }
    }

    public static void VerifyFiles(IsoLbaReader reader, FileInventory inv, string extraEfiIsoPath, IsoVerificationReport report, CancellationToken ct, BuildLogger logger)
    {
        Dictionary<string, IsoFileEntry> index = reader.GetFileIndex();
        var expected = new HashSet<string>(StringComparer.Ordinal);

        foreach (InventoryEntry file in inv.Files)
        {
            ct.ThrowIfCancellationRequested();
            string key = IsoLbaReader.NormalizeKey(file.IsoPath);
            expected.Add(key);

            if (!index.TryGetValue(key, out IsoFileEntry? isoEntry))
            {
                report.FilesMissing++;
                report.AddFailure($"File missing from the ISO: {file.IsoPath}");
                continue;
            }

            report.FilesChecked++;
            report.TotalBytes += file.Size;

            if (isoEntry.IsMultiExtent)
            {
                report.Warnings.Add($"Multi-extent file cannot be size-checked: {file.IsoPath}");
                continue;
            }

            if (isoEntry.Length != file.Size)
            {
                report.SizeMismatches++;
                report.AddFailure($"Size mismatch for {file.IsoPath}: ISO {isoEntry.Length} bytes, source {file.Size} bytes.");
            }
        }

        if (extraEfiIsoPath.Length > 0) expected.Add(IsoLbaReader.NormalizeKey(extraEfiIsoPath));
        int extras = index.Keys.Count(k => !expected.Contains(k));
        if (extras > 0) logger.Log($"The ISO contains {extras} additional entr{(extras == 1 ? "y" : "ies")} generated by the ISO writer (boot catalog, boot images).");
    }

    public static void HashContent(IsoLbaReader reader, FileInventory inv, string extraEfiIsoPath, bool deep, IsoVerificationReport report, CancellationToken ct, Action<long, long>? progress)
    {
        Dictionary<string, IsoFileEntry> index = reader.GetFileIndex();
        var pairs = new List<(InventoryEntry File, IsoFileEntry Iso)>();
        foreach (InventoryEntry file in inv.Files)
            if (index.TryGetValue(IsoLbaReader.NormalizeKey(file.IsoPath), out IsoFileEntry? isoEntry) && !isoEntry.IsMultiExtent)
                pairs.Add((file, isoEntry));

        pairs.Sort((a, b) => a.Iso.DataLba.CompareTo(b.Iso.DataLba));
        long total = pairs.Sum(p => p.Iso.Length);
        long done = 0;

        foreach (var (file, isoEntry) in pairs)
        {
            ct.ThrowIfCancellationRequested();
            string hash;
            try
            {
                using Stream stream = reader.OpenRead(isoEntry);
                hash = FileHasher.ComputeSha256(stream, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                report.AddFailure($"Could not read {file.IsoPath} from the ISO: {ex.Message}");
                continue;
            }

            report.ContentHashes.Add((file.IsoPath, hash, isoEntry.Length));
            done += isoEntry.Length;
            progress?.Invoke(done, total);

            if (deep && file.Sha256.Length > 0 && !string.Equals(file.Sha256, hash, StringComparison.OrdinalIgnoreCase))
            {
                report.HashMismatches++;
                report.AddFailure($"Content mismatch for {file.IsoPath}: the source changed during the build or the ISO is corrupt.");
            }
        }
    }
}

static class ManifestFile
{
    static readonly Regex LinePattern = new(@"^([0-9a-fA-F]{64})\s+(\d+)\s+(.+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static List<(string Sha256, long Size, string Path)> Parse(string path)
    {
        var result = new List<(string, long, string)>();
        foreach (string line in File.ReadLines(path, Encoding.UTF8))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            Match m = LinePattern.Match(line.TrimEnd());
            if (!m.Success) continue;
            if (!long.TryParse(m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long size)) continue;
            result.Add((m.Groups[1].Value.ToLowerInvariant(), size, m.Groups[3].Value.Trim()));
        }
        return result;
    }
}

static class IsoTools
{
    public static int Inspect(string path, ConsoleUI ui)
    {
        try
        {
            if (!File.Exists(path)) { ui.Error($"Error: ISO file not found: {path}"); return ExitCodes.Error; }

            using IsoLbaReader reader = IsoLbaReader.Open(path);
            ui.WriteLine($"File            : {Path.GetFullPath(path)}");
            ui.WriteLine($"Size            : {TextSanitizer.FormatBytes(reader.StreamLength)} ({reader.TotalSectors} sectors)");
            ui.WriteLine($"Volume ID       : {reader.VolumeId}");
            ui.WriteLine($"Volume sectors  : {reader.VolumeSpaceSizeSectors}");
            ui.WriteLine($"Joliet          : {(reader.HasJoliet ? "yes" : "no")}");
            ui.WriteLine($"Descriptors     : {string.Join(", ", reader.Descriptors.Select(d => $"type {d.Type}@{d.Lba}"))}");

            IReadOnlyList<IsoFileEntry> entries = reader.ReadAllEntries();
            int files = entries.Count(e => !e.IsDirectory);
            int dirs = entries.Count - files;
            long bytes = entries.Where(e => !e.IsDirectory).Sum(e => e.Length);
            ui.WriteLine($"Directories     : {dirs}");
            ui.WriteLine($"Files           : {files} ({TextSanitizer.FormatBytes(bytes)})");

            IsoBootCatalog? catalog = reader.ReadBootCatalog();
            if (catalog == null) ui.WriteLine("Boot catalog    : none");
            else
            {
                ui.WriteLine($"Boot catalog    : sector {catalog.Lba}, validation {(catalog.ValidationOk ? "ok" : "INVALID")}");
                int index = 0;
                foreach (BootCatalogEntry e in catalog.Entries)
                {
                    string platform = e.PlatformId switch { 0x00 => "x86 BIOS", 0xEF => "UEFI", 0x01 => "PowerPC", 0x02 => "Mac", _ => $"0x{e.PlatformId:X2}" };
                    ui.WriteLine($"  Entry {index++}: {platform}, bootable={e.Bootable}, media={e.MediaType}, sectors={e.SectorCount}, LBA={e.LoadRba}");
                }
            }
            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            ui.Error($"Error: cannot inspect ISO: {ex.Message}");
            return ExitCodes.Error;
        }
    }

    public static int Verify(string path, ConsoleUI ui, IsoBuilderOptions opts, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(path)) { ui.Error($"Error: ISO file not found: {path}"); return ExitCodes.Error; }

            var report = new IsoVerificationReport();
            using IsoLbaReader reader = IsoLbaReader.Open(path);
            IsoVerifier.VerifyStructure(reader, reader.StreamLength, false, report);
            IsoVerifier.VerifyBoot(reader, null, report);

            string manifestPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".", BuildPaths.ManifestFilename);
            int manifestChecked = 0;
            if (report.Ok && File.Exists(manifestPath))
            {
                foreach (var (sha, size, entryPath) in ManifestFile.Parse(manifestPath))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!reader.TryResolve(entryPath, out IsoFileEntry? entry) || entry == null)
                    {
                        report.AddFailure($"Listed in the manifest but missing from the ISO: {entryPath}");
                        continue;
                    }
                    if (entry.Length != size)
                    {
                        report.AddFailure($"Size mismatch for {entryPath}: ISO {entry.Length}, manifest {size}.");
                        continue;
                    }
                    using Stream stream = reader.OpenRead(entry);
                    string actual = FileHasher.ComputeSha256(stream, ct);
                    if (!string.Equals(actual, sha, StringComparison.OrdinalIgnoreCase))
                        report.AddFailure($"Hash mismatch for {entryPath}.");
                    manifestChecked++;
                }
            }

            if (!opts.Silent)
            {
                foreach (string w in report.Warnings) ui.WriteLine($"[WARN] {w}");
                foreach (string f in report.Failures) ui.WriteLine($"[FAIL] {f}");
                ui.WriteLine(File.Exists(manifestPath) ? $"Manifest entries verified: {manifestChecked}" : "No manifest found next to the ISO; content hashes were not checked.");
                ui.WriteLine(report.Ok ? "ISO verification passed." : "ISO verification FAILED.");
            }
            return report.Ok ? ExitCodes.Success : ExitCodes.ValidationFailed;
        }
        catch (OperationCanceledException)
        {
            ui.Error("Error: verification cancelled.");
            return ExitCodes.Cancelled;
        }
        catch (Exception ex)
        {
            ui.Error($"Error: cannot verify ISO: {ex.Message}");
            return ExitCodes.ValidationFailed;
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

    public static bool ValidateEfiImage(string path, out BootImageType type, BuildLogger logger)
    {
        type = BootImageType.Unknown;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;

        try
        {
            long fileSize = new FileInfo(path).Length;
            if (fileSize < 32 * 1024) return false;

            byte[] buf = new byte[512];
            int total = 0;
            using (var fs = File.OpenRead(path))
            {
                while (total < buf.Length)
                {
                    int n = fs.Read(buf, total, buf.Length - total);
                    if (n <= 0) break;
                    total += n;
                }
            }
            if (total < 512) return false;
            if (buf[510] != 0x55 || buf[511] != 0xAA) return false;

            string fat16Label = Encoding.ASCII.GetString(buf, 54, 5);
            string fat32Label = Encoding.ASCII.GetString(buf, 82, 5);
            if (fat16Label is "FAT12" or "FAT16" || fat32Label == "FAT32")
            {
                type = BootImageType.FatEspImage;
                return true;
            }

            int bytesPerSector = BitConverter.ToUInt16(buf, 11);
            int sectorsPerCluster = buf[13];
            int fatCount = buf[16];
            bool plausibleFat = (bytesPerSector is 512 or 1024 or 2048 or 4096)
                && sectorsPerCluster != 0 && (sectorsPerCluster & (sectorsPerCluster - 1)) == 0
                && (fatCount == 1 || fatCount == 2);
            if (plausibleFat)
            {
                type = BootImageType.FatEspImage;
                return true;
            }

            byte firstPartitionType = buf[446 + 4];
            if (firstPartitionType == 0xEF || firstPartitionType == 0xEE)
            {
                type = BootImageType.FatEspImage;
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            logger.Error($"EFI image validation error: {ex.Message}");
            return false;
        }
    }
}

static class PeInspector
{
    public static bool TryInspect(string path, out bool signed, out ushort machine)
    {
        signed = false;
        machine = 0;
        try
        {
            byte[] buf = new byte[8192];
            int total = 0;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                while (total < buf.Length)
                {
                    int n = fs.Read(buf, total, buf.Length - total);
                    if (n <= 0) break;
                    total += n;
                }
            }

            if (total < 0x40 || buf[0] != 0x4D || buf[1] != 0x5A) return false;
            int peOffset = BitConverter.ToInt32(buf, 0x3C);
            if (peOffset < 0 || peOffset + 24 > total) return false;
            if (buf[peOffset] != (byte)'P' || buf[peOffset + 1] != (byte)'E' || buf[peOffset + 2] != 0 || buf[peOffset + 3] != 0) return false;

            machine = BitConverter.ToUInt16(buf, peOffset + 4);
            int optionalOffset = peOffset + 24;
            if (optionalOffset + 2 > total) return false;

            ushort magic = BitConverter.ToUInt16(buf, optionalOffset);
            int dataDirectoryOffset;
            if (magic == 0x20b) dataDirectoryOffset = optionalOffset + 112;
            else if (magic == 0x10b) dataDirectoryOffset = optionalOffset + 96;
            else return false;

            int securityEntry = dataDirectoryOffset + 4 * 8;
            if (securityEntry + 8 > total) return true;

            uint securitySize = BitConverter.ToUInt32(buf, securityEntry + 4);
            signed = securitySize > 0;
            return true;
        }
        catch
        {
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
            if (string.IsNullOrEmpty(root)) { logger.Warn("Could not determine drive root; skipping the free space check."); return true; }

            DriveInfo drive;
            try { drive = new DriveInfo(root); }
            catch (Exception ex) { logger.Warn($"Could not read drive info for '{root}': {ex.Message}; skipping the free space check."); return true; }

            long requiredMb = (long)Math.Ceiling(predictedMb * 1.5) + 32;
            long freeMb;
            try { freeMb = drive.AvailableFreeSpace / (1024L * 1024L); }
            catch (Exception ex) { logger.Warn($"Could not read free space for '{root}': {ex.Message}; skipping the free space check."); return true; }
            if (freeMb >= requiredMb) return true;

            string msg = $"Insufficient disk space. Required: ~{requiredMb} MB (1.5x predicted + 32 MB), available: {freeMb} MB";
            if (!silent) ui.WriteLine(msg);
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
    const int HardMaxFiles = 2_000_000;
    const int MaxQueueSize = 200_000;

    readonly record struct ScanItem(string Dir, string Rel, string Iso, int Depth);

    public static string ToBuilderPath(string isoPath) => isoPath.Replace('/', '\\');

    public static FileInventory BuildInventory(BuildConfig cfg, BuildLogger logger, CancellationToken ct, bool probeAccess, Action<int>? onProgress)
    {
        var inv = new FileInventory();
        if (string.IsNullOrEmpty(cfg.IsoDir) || !Directory.Exists(cfg.IsoDir)) return inv;

        string basePath = Path.GetFullPath(cfg.IsoDir);
        var queue = new Queue<ScanItem>();
        var visited = new HashSet<string>(PathComparer.Instance);
        queue.Enqueue(new ScanItem(basePath, string.Empty, string.Empty, 0));

        long maxFileBytes = cfg.MaxFileSizeMb > 0 ? cfg.MaxFileSizeMb * 1024L * 1024L : 0;
        int fileLimit = cfg.MaxFiles > 0 ? cfg.MaxFiles : HardMaxFiles;
        int maxDepth = cfg.MaxDepth > 0 ? cfg.MaxDepth : 64;

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            if (queue.Count > MaxQueueSize)
            {
                inv.Truncated = true;
                inv.TruncationReason = $"directory traversal queue exceeded {MaxQueueSize} entries.";
                logger.Error($"Directory traversal queue exceeded {MaxQueueSize}, aborting to prevent memory exhaustion.");
                break;
            }

            ScanItem item = queue.Dequeue();
            string current = item.Dir;
            if (!visited.Add(current)) continue;

            if (item.Depth > 0)
            {
                try
                {
                    var di = new DirectoryInfo(current);
                    if ((di.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        logger.Warn($"Skipping symlink/junction: {current}");
                        inv.SkippedFiles++;
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    logger.Error($"Failed to inspect directory {current}: {ex.Message}");
                    inv.FailedFiles++;
                    continue;
                }
            }

            List<string> files;
            List<string> dirs;
            try
            {
                files = Directory.EnumerateFiles(current).ToList();
                dirs = Directory.EnumerateDirectories(current).ToList();
            }
            catch (Exception ex)
            {
                logger.Error($"Failed to enumerate {current}: {ex.Message}");
                inv.FailedFiles++;
                continue;
            }

            files.Sort(StringComparer.Ordinal);
            dirs.Sort(StringComparer.Ordinal);

            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string f in files)
            {
                ct.ThrowIfCancellationRequested();
                string name = Path.GetFileName(f);
                if (IgnoredFiles.Contains(name)) { inv.SkippedFiles++; continue; }

                string rel = item.Rel.Length == 0 ? name : item.Rel + "/" + name;
                string full = Path.GetFullPath(f);
                if (cfg.ReservedPaths.Contains(full)) { inv.SkippedFiles++; continue; }
                if (GlobMatcher.AnyMatch(name, rel, cfg.ExcludePatterns)) { inv.SkippedFiles++; continue; }
                if (cfg.IncludePatterns.Count > 0 && !GlobMatcher.AnyMatch(name, rel, cfg.IncludePatterns)) { inv.SkippedFiles++; continue; }

                FileInfo fi;
                long size;
                try
                {
                    fi = new FileInfo(f);
                    if ((fi.Attributes & FileAttributes.ReparsePoint) != 0 || fi.LinkTarget != null)
                    {
                        logger.Warn($"Skipping symlink/reparse point: {f}");
                        inv.SkippedFiles++;
                        continue;
                    }
                    size = fi.Length;
                }
                catch (Exception ex)
                {
                    logger.Error($"Failed to inspect file {f}: {ex.Message}");
                    inv.FailedFiles++;
                    continue;
                }

                if (maxFileBytes > 0 && size > maxFileBytes)
                {
                    logger.Error($"File exceeds the per-file limit of {cfg.MaxFileSizeMb} MB (--max-file-size): {f}");
                    inv.FailedFiles++;
                    continue;
                }

                if (probeAccess)
                {
                    try { using (new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.None)) { } }
                    catch (Exception ex)
                    {
                        logger.Error($"File is not readable: {f}: {ex.Message}");
                        inv.FailedFiles++;
                        continue;
                    }
                }

                string? isoName = MapComponent(name, rel, usedNames, cfg.CollisionMode, logger, inv);
                if (isoName == null) continue;

                inv.Files.Add(new InventoryEntry
                {
                    FullPath = full,
                    RelativePath = rel,
                    IsoPath = item.Iso.Length == 0 ? isoName : item.Iso + "/" + isoName,
                    Size = size,
                    LastWriteTicks = fi.LastWriteTimeUtc.Ticks
                });
                inv.TotalBytes += size;

                if (inv.Files.Count > fileLimit)
                {
                    inv.Truncated = true;
                    inv.MaxFilesExceeded = cfg.MaxFiles > 0;
                    inv.TruncationReason = cfg.MaxFiles > 0
                        ? $"more than {fileLimit} files were found."
                        : $"the scan is capped at {HardMaxFiles} files.";
                    if (cfg.MaxFiles <= 0) logger.Error($"Too many files, scan capped at {HardMaxFiles}.");
                    break;
                }

                if ((inv.Files.Count & 255) == 0) onProgress?.Invoke(inv.Files.Count);
            }

            if (inv.Truncated) break;

            foreach (string d in dirs)
            {
                ct.ThrowIfCancellationRequested();
                string dname = Path.GetFileName(d);
                if (IgnoredDirs.Contains(dname)) continue;

                string drel = item.Rel.Length == 0 ? dname : item.Rel + "/" + dname;
                string dfull = Path.GetFullPath(d);
                if (cfg.ReservedPaths.Contains(dfull)) continue;
                if (GlobMatcher.AnyMatch(dname, drel, cfg.ExcludePatterns)) continue;

                if (item.Depth + 1 > maxDepth)
                {
                    logger.Error($"Directory depth limit of {maxDepth} exceeded (--max-depth): {d}");
                    inv.FailedFiles++;
                    continue;
                }

                string? isoDirName = MapComponent(dname, drel, usedNames, cfg.CollisionMode, logger, inv);
                if (isoDirName == null) continue;

                inv.DirectoryCount++;
                queue.Enqueue(new ScanItem(dfull, drel, item.Iso.Length == 0 ? isoDirName : item.Iso + "/" + isoDirName, item.Depth + 1));
            }

            if (files.Count == 0 && dirs.Count == 0 && item.Iso.Length > 0)
                inv.EmptyDirectories.Add(item.Iso);
        }

        onProgress?.Invoke(inv.Files.Count);
        return inv;
    }

    static string? MapComponent(string name, string rel, HashSet<string> used, PathCollisionMode mode, BuildLogger logger, FileInventory inv)
    {
        string sanitized = SanitizePathComponent(name);
        if (!string.Equals(sanitized, name, StringComparison.Ordinal))
            logger.Warn($"ISO path sanitized: '{rel}' -> component '{sanitized}'");

        if (used.Add(sanitized)) return sanitized;

        switch (mode)
        {
            case PathCollisionMode.Fail:
                logger.Error($"ISO path collision: '{rel}' maps to '{sanitized}', which is already used in the same directory.");
                inv.FailedFiles++;
                inv.Collisions++;
                return null;
            case PathCollisionMode.Skip:
                logger.Warn($"ISO path collision: '{rel}' maps to '{sanitized}', which is already used; entry skipped.");
                inv.SkippedFiles++;
                inv.Collisions++;
                return null;
            default:
                for (int n = 1; n < 100_000; n++)
                {
                    string candidate = AppendSuffix(sanitized, "_" + n.ToString(CultureInfo.InvariantCulture));
                    if (used.Add(candidate))
                    {
                        logger.Warn($"ISO path collision: '{rel}' renamed to '{candidate}'.");
                        inv.Renames.Add($"{rel} -> {candidate}");
                        inv.Collisions++;
                        return candidate;
                    }
                }
                logger.Error($"Could not find a free ISO name for '{rel}'.");
                inv.FailedFiles++;
                return null;
        }
    }

    static string AppendSuffix(string name, string suffix)
    {
        int dot = name.LastIndexOf('.');
        string baseName = name;
        string ext = string.Empty;
        if (dot > 0 && name.Length - dot <= 11)
        {
            baseName = name[..dot];
            ext = name[dot..];
        }

        int maxBase = Math.Max(1, JolietMaxComponentLength - ext.Length - suffix.Length);
        if (baseName.Length > maxBase) baseName = baseName[..maxBase];
        return baseName + suffix + ext;
    }

    static string SanitizePathComponent(string component)
    {
        var sb = new StringBuilder(component.Length);
        foreach (char c in component)
            if (c != '\0' && !char.IsControl(c) && !char.IsSurrogate(c) && (char.IsLetterOrDigit(c) || "._-() ".IndexOf(c) >= 0))
                sb.Append(c);

        string s = sb.ToString().Trim();
        if (s.Length == 0 || s.All(ch => ch == '.')) return "_";
        if (s.Length > JolietMaxComponentLength)
        {
            int dot = s.LastIndexOf('.');
            if (dot > 0 && s.Length - dot <= 11)
            {
                string ext = s[dot..];
                s = s[..Math.Min(dot, JolietMaxComponentLength - ext.Length)] + ext;
            }
            else
            {
                s = s[..JolietMaxComponentLength];
            }
            s = s.TrimEnd();
            if (s.Length == 0) s = "_";
        }
        return s;
    }

    public static int HashInventory(FileInventory inv, HashCache? cache, int threads, BuildLogger logger, CancellationToken ct, Action<long, long>? progress)
    {
        int failed = 0;
        long done = 0;
        long total = inv.Files.Count;
        var options = new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = Math.Max(1, threads)
        };

        Parallel.ForEach(inv.Files, options, entry =>
        {
            if (entry.Sha256.Length == 0)
            {
                if (cache != null && cache.TryGet(entry.FullPath, entry.Size, entry.LastWriteTicks, out string cached))
                {
                    entry.Sha256 = cached;
                }
                else
                {
                    string hash = FileHasher.ComputeSha256(entry.FullPath, ct);
                    if (hash.Length == 0)
                    {
                        Interlocked.Increment(ref failed);
                        logger.Error($"Could not hash source file: {entry.FullPath}");
                    }
                    else
                    {
                        entry.Sha256 = hash;
                        cache?.Set(entry.FullPath, entry.Size, entry.LastWriteTicks, hash);
                    }
                }
            }

            long finished = Interlocked.Increment(ref done);
            progress?.Invoke(finished, total);
        });

        cache?.Prune(inv.Files.Select(f => f.FullPath));
        return failed;
    }

    public static void AddToBuilder(CDBuilder builder, FileInventory inv, BuildConfig cfg, BuildLogger logger, ref int processedFiles, CancellationToken ct, BuildState state)
    {
        int processed = 0;

        foreach (string emptyDir in inv.EmptyDirectories)
        {
            try { builder.AddDirectory(ToBuilderPath(emptyDir)); }
            catch (Exception ex) { logger.Warn($"Failed to add empty directory {emptyDir} to ISO: {ex.Message}"); }
        }

        foreach (InventoryEntry entry in inv.Files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                builder.AddFile(ToBuilderPath(entry.IsoPath), entry.FullPath);
                processed++;
            }
            catch (Exception ex)
            {
                state.FailedFiles++;
                logger.Error($"Failed to add file {entry.FullPath} to ISO: {ex.Message}");
            }
        }

        processedFiles = processed;
    }
}

static class SizePredictor
{
    public static long PredictIsoSizeMb(FileInventory inv, string bootBinPath, string efiImgPath, bool biosEnabled, bool uefiEnabled, BuildLogger logger)
    {
        const long sector = 2048;
        long totalBytes = 16 * sector + 8 * sector;

        foreach (InventoryEntry file in inv.Files)
            totalBytes += (file.Size + sector - 1) / sector * sector;

        long directories = inv.DirectoryCount + 1;
        totalBytes += directories * sector * 2;
        totalBytes += inv.Files.Count * 180L;
        totalBytes += directories * 128L;

        void AddIfExists(string path)
        {
            if (!File.Exists(path)) return;
            try { totalBytes += (new FileInfo(path).Length + sector - 1) / sector * sector; }
            catch (Exception ex) { logger.Warn($"Failed to stat {path}: {ex.Message}"); }
        }

        if (biosEnabled) AddIfExists(bootBinPath);
        if (uefiEnabled) AddIfExists(efiImgPath);
        if (biosEnabled && uefiEnabled) AddIfExists(efiImgPath);
        totalBytes += sector;

        long mb = (totalBytes + (1024L * 1024L - 1)) / (1024L * 1024L);
        return Math.Max(1, mb);
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
    public static void Write(string outputDir, string projectName, string appVersion, bool bios, bool uefi, bool secureBoot, BootPriorityMode bootPriority, bool manifestEnabled, BuildLogger logger, string isoSha256 = "", bool reproducible = false)
    {
        string path = Path.Combine(outputDir, BuildPaths.ReadmeFilename);
        string tmp = path + ".tmp";
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"BuildIso {appVersion}");
            sb.AppendLine();
            sb.AppendLine($"Project    : {projectName}");
            sb.AppendLine($"BIOS boot  : {(bios ? "YES" : "NO")}");
            sb.AppendLine($"UEFI boot  : {(uefi ? "YES" : "NO")}");
            sb.AppendLine($"Secure Boot: {(secureBoot ? "YES" : "NO")}");
            if (bios && uefi) sb.AppendLine($"Boot priority: {bootPriority}");
            sb.AppendLine($"Manifest   : {(manifestEnabled ? "YES" : "NO")}");
            sb.AppendLine($"Reproducible: {(reproducible ? "YES" : "NO")}");
            if (!string.IsNullOrEmpty(isoSha256)) sb.AppendLine($"SHA256     : {isoSha256}");
            sb.AppendLine();
            sb.AppendLine("This ISO was generated by BuildIso.");
            File.WriteAllText(tmp, sb.ToString(), BuildLogger.Utf8NoBom);
            File.Move(tmp, path, true);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            logger.Warn($"Failed to write README: {ex.Message}");
        }
    }
}

sealed class BuildLogger
{
    readonly object _lock = new();
    readonly string _logPath;
    readonly bool _disabled;
    const int MaxLines = 50_000;
    const int MaxFileSize = 50 * 1024 * 1024;
    const int MaxCaptured = 500;
    int _lineCount;
    bool _truncatedNotice;
    readonly List<string> _warnings = new();
    readonly List<string> _errors = new();

    public static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    public BuildLogger(string projectDir, string? customPath = null, bool disabled = false)
    {
        _disabled = disabled;
        _logPath = string.IsNullOrWhiteSpace(customPath)
            ? Path.Combine(projectDir, BuildPaths.LogFilename)
            : Path.GetFullPath(customPath);
    }

    public string LogPath => _logPath;
    public int WarningCount { get; private set; }
    public int ErrorCount { get; private set; }

    public IReadOnlyList<string> Warnings { get { lock (_lock) return _warnings.ToArray(); } }
    public IReadOnlyList<string> Errors { get { lock (_lock) return _errors.ToArray(); } }

    public void Log(string msg) => Write("INFO", msg);
    public void Warn(string msg) => Write("WARN", msg);
    public void Error(string msg) => Write("ERROR", msg);

    void Write(string level, string msg)
    {
        string clean = TextSanitizer.Clean(msg);
        lock (_lock)
        {
            if (level == "WARN")
            {
                WarningCount++;
                if (_warnings.Count < MaxCaptured) _warnings.Add(clean);
            }
            else if (level == "ERROR")
            {
                ErrorCount++;
                if (_errors.Count < MaxCaptured) _errors.Add(clean);
            }

            if (_disabled) return;

            if (_lineCount >= MaxLines)
            {
                if (!_truncatedNotice)
                {
                    _truncatedNotice = true;
                    try { File.AppendAllText(_logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [WARN] Log line limit reached; further messages are not written.{Environment.NewLine}", Utf8NoBom); } catch { }
                }
                return;
            }

            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {clean}";
            try
            {
                string? dir = Path.GetDirectoryName(_logPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

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
            string pattern = Path.GetFileName(_logPath) + ".*.bak";
            string[] oldBackups = Directory.GetFiles(logDir, pattern)
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
    public string Status { get; init; } = string.Empty;
    public string FailureMessage { get; init; } = string.Empty;
    public string FailedStep { get; init; } = string.Empty;
    public string BootType { get; init; } = string.Empty;
    public long PredictedSizeMb { get; init; }
    public long ActualSizeBytes { get; init; }
    public long ExactSizeBytes { get; init; }
    public double BuildSeconds { get; init; }
    public double BuildSpeedMbPerSec { get; init; }
    public int TotalFiles { get; init; }
    public int ProcessedFiles { get; init; }
    public int SkippedFiles { get; init; }
    public int FailedFiles { get; init; }
    public string IsoSha256 { get; init; } = string.Empty;
    public bool DryRun { get; init; }
    public bool Check { get; init; }
    public bool Strict { get; init; }
    public bool Reproducible { get; init; }
    public long SourceDateEpoch { get; init; }
    public string Profile { get; init; } = string.Empty;
    public string DotNetVersion { get; init; } = string.Empty;
    public string IsoBackend { get; init; } = string.Empty;
    public IReadOnlyList<string> Plugins { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    public IReadOnlyList<StepResult> Steps { get; init; } = Array.Empty<StepResult>();
    public IReadOnlyList<PreflightIssue> PreflightIssues { get; init; } = Array.Empty<PreflightIssue>();
    public IReadOnlyList<string> BootEntries { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> ToolHashes { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<BootTestResult> BootTests { get; init; } = Array.Empty<BootTestResult>();
    public IReadOnlyList<string> Renames { get; init; } = Array.Empty<string>();
    public string GeneratedAtUtc { get; init; } = string.Empty;
}

static class BuildReportWriter
{
    public static void Write(string path, BuildContext ctx)
    {
        string tmp = path + ".tmp";
        try
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            BuildReport report = CreateReport(ctx);
            string format = string.IsNullOrEmpty(ctx.Opts.ReportFormat) ? "json" : ctx.Opts.ReportFormat;

            switch (format)
            {
                case "junit": WriteJUnit(tmp, report); break;
                case "sarif": WriteSarif(tmp, report, ctx); break;
                default:
                    File.WriteAllText(tmp, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), BuildLogger.Utf8NoBom);
                    break;
            }

            File.Move(tmp, path, true);
            ctx.Logger.Log($"{format.ToUpperInvariant()} report written: {path}");
        }
        catch (Exception ex)
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            ctx.Logger.Warn($"Failed to write {ctx.Opts.ReportFormat} report: {ex.Message}");
        }
    }

    static string StatusText(BuildStatus status) => status switch
    {
        BuildStatus.Success => "success",
        BuildStatus.Cancelled => "cancelled",
        BuildStatus.ValidationFailed => "validation_failed",
        _ => "failed"
    };

    static BuildReport CreateReport(BuildContext ctx)
    {
        string backend = "DiscUtils";
        try
        {
            AssemblyName an = typeof(CDBuilder).Assembly.GetName();
            backend = $"{an.Name} {an.Version}";
        }
        catch { }

        return new BuildReport
        {
            Version = BuildPaths.AppVersion,
            ProjectDir = ctx.Config.ProjectDir,
            Output = ctx.Config.IsoOutputPath,
            Success = !ctx.State.Failed,
            Status = StatusText(ctx.State.Status),
            FailureMessage = TextSanitizer.Clean(ctx.State.FailureMessage),
            FailedStep = ctx.State.FailedStep,
            BootType = ctx.State.BootType.ToString(),
            PredictedSizeMb = ctx.State.PredictedSizeMb,
            ActualSizeBytes = ctx.State.ActualSizeBytes,
            ExactSizeBytes = ctx.State.ExactSizeBytes,
            BuildSeconds = ctx.State.BuildSeconds,
            BuildSpeedMbPerSec = ctx.State.BuildSpeedMbPerSec,
            TotalFiles = ctx.State.TotalFiles,
            ProcessedFiles = ctx.State.ProcessedFiles,
            SkippedFiles = ctx.State.SkippedFiles,
            FailedFiles = ctx.State.FailedFiles,
            IsoSha256 = ctx.State.IsoSha256,
            DryRun = ctx.Opts.DryRun,
            Check = ctx.Opts.Check,
            Strict = ctx.Opts.Strict,
            Reproducible = ctx.Config.Reproducible,
            SourceDateEpoch = ctx.Config.Reproducible ? ctx.Config.SourceDateEpoch : -1,
            Profile = ctx.Opts.Profile,
            DotNetVersion = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            IsoBackend = backend,
            Plugins = ctx.Plugins.Plugins.Select(p => TextSanitizer.Clean(p.Name)).ToArray(),
            Warnings = ctx.Logger.Warnings,
            Errors = ctx.Logger.Errors,
            Steps = ctx.State.Steps.ToArray(),
            PreflightIssues = ctx.State.PreflightIssues.ToArray(),
            BootEntries = ctx.State.BootEntries.Select(e => $"platform=0x{e.PlatformId:X2} bootable={e.Bootable} sectors={e.SectorCount} lba={e.LoadRba}").ToArray(),
            ToolHashes = new Dictionary<string, string>(ctx.State.ToolHashes),
            BootTests = ctx.State.BootTests.ToArray(),
            Renames = ctx.State.Renames.ToArray(),
            GeneratedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        };
    }

    static void WriteJUnit(string path, BuildReport report)
    {
        var settings = new XmlWriterSettings { Indent = true, Encoding = BuildLogger.Utf8NoBom };
        using var writer = XmlWriter.Create(path, settings);

        int failures = report.Steps.Count(s => s.Status is "failed" or "cancelled") + report.BootTests.Count(t => !t.Passed);
        int tests = report.Steps.Count + report.BootTests.Count;

        writer.WriteStartDocument();
        writer.WriteStartElement("testsuites");
        writer.WriteStartElement("testsuite");
        writer.WriteAttributeString("name", "BuildIso");
        writer.WriteAttributeString("tests", tests.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("failures", failures.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("errors", "0");
        writer.WriteAttributeString("skipped", report.Steps.Count(s => s.Status == "skipped").ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("time", report.Steps.Sum(s => s.Seconds).ToString("F3", CultureInfo.InvariantCulture));
        writer.WriteAttributeString("timestamp", report.GeneratedAtUtc);

        foreach (StepResult step in report.Steps)
        {
            writer.WriteStartElement("testcase");
            writer.WriteAttributeString("classname", "BuildIso");
            writer.WriteAttributeString("name", step.Name);
            writer.WriteAttributeString("time", step.Seconds.ToString("F3", CultureInfo.InvariantCulture));
            if (step.Status == "skipped") writer.WriteElementString("skipped", string.Empty);
            else if (step.Status is "failed" or "cancelled")
            {
                writer.WriteStartElement("failure");
                writer.WriteAttributeString("message", TextSanitizer.Clean(step.Message));
                writer.WriteAttributeString("type", step.Status);
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
        }

        foreach (BootTestResult test in report.BootTests)
        {
            writer.WriteStartElement("testcase");
            writer.WriteAttributeString("classname", "BuildIso.BootTest");
            writer.WriteAttributeString("name", test.Mode);
            writer.WriteAttributeString("time", test.Seconds.ToString("F3", CultureInfo.InvariantCulture));
            if (!test.Passed)
            {
                writer.WriteStartElement("failure");
                writer.WriteAttributeString("message", TextSanitizer.Clean(test.Message));
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
        }

        writer.WriteStartElement("system-out");
        writer.WriteString(TextSanitizer.Clean($"status={report.Status} sha256={report.IsoSha256} warnings={report.Warnings.Count}"));
        writer.WriteEndElement();

        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    static void WriteSarif(string path, BuildReport report, BuildContext ctx)
    {
        var results = new List<object>();
        foreach (string error in report.Errors)
            results.Add(new Dictionary<string, object?> { ["ruleId"] = "BUILDISO-ERROR", ["level"] = "error", ["message"] = new Dictionary<string, object?> { ["text"] = error } });
        foreach (string warning in report.Warnings)
            results.Add(new Dictionary<string, object?> { ["ruleId"] = "BUILDISO-WARNING", ["level"] = "warning", ["message"] = new Dictionary<string, object?> { ["text"] = warning } });
        if (report.FailureMessage.Length > 0)
            results.Add(new Dictionary<string, object?> { ["ruleId"] = "BUILDISO-FAILURE", ["level"] = "error", ["message"] = new Dictionary<string, object?> { ["text"] = report.FailureMessage } });

        var rules = new object[]
        {
            new Dictionary<string, object?> { ["id"] = "BUILDISO-ERROR", ["name"] = "BuildError", ["shortDescription"] = new Dictionary<string, object?> { ["text"] = "An error was logged during the build." } },
            new Dictionary<string, object?> { ["id"] = "BUILDISO-WARNING", ["name"] = "BuildWarning", ["shortDescription"] = new Dictionary<string, object?> { ["text"] = "A warning was logged during the build." } },
            new Dictionary<string, object?> { ["id"] = "BUILDISO-FAILURE", ["name"] = "BuildFailure", ["shortDescription"] = new Dictionary<string, object?> { ["text"] = "The build did not complete successfully." } }
        };

        var sarif = new Dictionary<string, object?>
        {
            ["$schema"] = "https://json.schemastore.org/sarif-2.1.0.json",
            ["version"] = "2.1.0",
            ["runs"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["tool"] = new Dictionary<string, object?>
                    {
                        ["driver"] = new Dictionary<string, object?>
                        {
                            ["name"] = "BuildIso",
                            ["version"] = BuildPaths.AppVersion,
                            ["rules"] = rules
                        }
                    },
                    ["results"] = results,
                    ["invocations"] = new object[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["executionSuccessful"] = report.Success,
                            ["exitCode"] = Program.ExitCodeFor(ctx.State)
                        }
                    }
                }
            }
        };

        File.WriteAllText(path, JsonSerializer.Serialize(sarif, new JsonSerializerOptions { WriteIndented = true }), BuildLogger.Utf8NoBom);
    }
}

static class BootTester
{
    public static BootTestResult Run(BuildContext ctx, string mode, string qemuPath, string? firmwarePath, int timeoutSeconds)
    {
        var watch = Stopwatch.StartNew();
        var output = new StringBuilder();

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = qemuPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-m");
            psi.ArgumentList.Add("512");
            psi.ArgumentList.Add("-display");
            psi.ArgumentList.Add("none");
            psi.ArgumentList.Add("-no-reboot");
            psi.ArgumentList.Add("-boot");
            psi.ArgumentList.Add("order=d");
            if (firmwarePath != null)
            {
                psi.ArgumentList.Add("-drive");
                psi.ArgumentList.Add($"if=pflash,format=raw,readonly=on,file={firmwarePath.Replace(",", ",,")}");
            }
            psi.ArgumentList.Add("-drive");
            psi.ArgumentList.Add($"file={ctx.Config.IsoOutputPath.Replace(",", ",,")},media=cdrom,readonly=on,format=raw");

            using var process = new Process { StartInfo = psi };
            process.OutputDataReceived += (_, e) => { if (e.Data != null && output.Length < 4096) lock (output) output.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null && output.Length < 4096) lock (output) output.AppendLine(e.Data); };

            if (!process.Start())
                return new BootTestResult { Mode = mode, Passed = false, Message = "QEMU could not be started.", Seconds = watch.Elapsed.TotalSeconds };

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            while (!process.HasExited && watch.Elapsed.TotalSeconds < timeoutSeconds)
            {
                if (ctx.CancellationToken.IsCancellationRequested)
                {
                    TryKill(process);
                    ctx.CancellationToken.ThrowIfCancellationRequested();
                }
                process.WaitForExit(200);
            }

            if (process.HasExited)
            {
                process.WaitForExit();
                string text;
                lock (output) text = TextSanitizer.Clean(output.ToString()).Trim();
                if (text.Length > 300) text = text[..300];
                return new BootTestResult
                {
                    Mode = mode,
                    Passed = false,
                    Message = $"The virtual machine terminated early with exit code {process.ExitCode}.{(text.Length > 0 ? " " + text : string.Empty)}",
                    Seconds = watch.Elapsed.TotalSeconds
                };
            }

            TryKill(process);
            return new BootTestResult
            {
                Mode = mode,
                Passed = true,
                Message = $"The virtual machine kept running for {timeoutSeconds} s without resetting or halting.",
                Seconds = watch.Elapsed.TotalSeconds
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new BootTestResult { Mode = mode, Passed = false, Message = $"Boot test could not run: {ex.Message}", Seconds = watch.Elapsed.TotalSeconds };
        }
    }

    static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(true);
            process.WaitForExit(2000);
        }
        catch { }
    }
}
static class Program
{
    static readonly object ConsoleLock = new();
    static volatile BuildLogger? ActiveLogger;

    public static int ExitCodeFor(BuildState state)
    {
        if (!state.Failed) return ExitCodes.Success;
        if (state.BootTestFailed) return ExitCodes.BootTestFailed;
        return state.Status switch
        {
            BuildStatus.Cancelled => ExitCodes.Cancelled,
            BuildStatus.ValidationFailed => ExitCodes.ValidationFailed,
            _ => BuildPipeline.IsPreBuild(state.FailedStep) ? ExitCodes.PreflightFailed : ExitCodes.Error
        };
    }

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
            return Run(args ?? Array.Empty<string>());
        }
        catch (OperationCanceledException)
        {
            try { Console.Error.WriteLine("Error: operation cancelled."); } catch { }
            return ExitCodes.Cancelled;
        }
        catch (Exception ex)
        {
            try { Console.Error.WriteLine($"Fatal error: {ex}"); } catch { }
            return ExitCodes.Error;
        }
    }

    static void ConfigureConsole()
    {
        try
        {
            if (!Console.IsOutputRedirected) Console.OutputEncoding = new UTF8Encoding(false);
        }
        catch { }
    }

    static PosixSignalRegistration? TryRegisterTermination(CancellationTokenSource cts)
    {
        try
        {
            return PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                try { ActiveLogger?.Log("Build cancelled by termination signal."); cts.Cancel(); }
                catch (ObjectDisposedException) { }
            });
        }
        catch (PlatformNotSupportedException) { return null; }
        catch (Exception) { return null; }
    }

    static int Run(string[] args)
    {
        ConfigureConsole();

        IsoBuilderOptions parsed = IsoBuilderOptions.Parse(args);
        if (parsed.Help) { PrintHelp(); return ExitCodes.Success; }
        if (parsed.Version) { Console.WriteLine($"BuildIso {BuildPaths.AppVersion}"); return ExitCodes.Success; }
        if (parsed.Init && parsed.Errors.Count == 0) return RunInit();

        IsoBuilderOptions opts = ConfigFileLoader.Merge(parsed);
        if (opts.Errors.Count > 0)
        {
            foreach (string error in opts.Errors) Console.Error.WriteLine($"Error: {TextSanitizer.Clean(error)}");
            Console.Error.WriteLine("Run 'BuildIso help' for usage.");
            return ExitCodes.InvalidArguments;
        }

        bool inputRedirected;
        try { inputRedirected = Console.IsInputRedirected; }
        catch { inputRedirected = true; }
        if (inputRedirected || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")))
            opts = opts with { NoPrompt = true };

        var ui = new ConsoleUI(ConsoleLock);
        if (!opts.Silent)
            foreach (string warning in opts.Warnings) ui.WriteLine($"[WARN] {warning}");

        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (s, e) =>
        {
            if (cts.IsCancellationRequested) { e.Cancel = false; return; }
            e.Cancel = true;
            try
            {
                ActiveLogger?.Log("Build cancelled by user (Ctrl+C).");
                cts.Cancel();
            }
            catch (ObjectDisposedException) { }
        };

        Console.CancelKeyPress += cancelHandler;
        using PosixSignalRegistration? termination = TryRegisterTermination(cts);

        try
        {
            if (!string.IsNullOrEmpty(opts.InspectPath)) return IsoTools.Inspect(opts.InspectPath, ui);
            if (!string.IsNullOrEmpty(opts.VerifyIsoPath)) return IsoTools.Verify(opts.VerifyIsoPath, ui, opts, cts.Token);
            return RunBuild(opts, ui, cts);
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    static string CancelMessage(IsoBuilderOptions opts, Stopwatch watch)
    {
        if (opts.TimeoutSeconds > 0 && watch.Elapsed.TotalSeconds >= opts.TimeoutSeconds)
            return $"Build cancelled: the timeout of {opts.TimeoutSeconds} seconds was reached (--timeout).";
        return "Build cancelled.";
    }

    static int RunBuild(IsoBuilderOptions opts, ConsoleUI ui, CancellationTokenSource cts)
    {
        string projectDir = ResolveProjectDir(opts, ui);
        if (string.IsNullOrEmpty(projectDir)) return ExitError("No project directory specified.", ui, opts, ExitCodes.InvalidArguments);
        if (!Directory.Exists(projectDir)) return ExitError($"Project directory does not exist: {projectDir}", ui, opts, ExitCodes.InvalidArguments);
        projectDir = Path.GetFullPath(projectDir);

        var logger = new BuildLogger(projectDir, opts.LogPath, opts.NoLog);
        ActiveLogger = logger;

        BuildConfig? config = BuildConfigFactory.Create(opts, projectDir, ui, logger);
        if (config == null)
        {
            logger.Save();
            return ExitError("Configuration failed.", ui, opts, ExitCodes.PreflightFailed);
        }

        var plugins = new PluginManager(opts.NoPrompt, opts.Silent);
        plugins.Configure(opts, logger);

        string exeDir = AppContext.BaseDirectory;
        plugins.LoadFromDirectory(Path.Combine(exeDir, BuildPaths.PluginsDirName), logger, ui);
        plugins.LoadFromDirectory(Path.Combine(projectDir, BuildPaths.PluginsDirName), logger, ui);

        var totalWatch = Stopwatch.StartNew();
        if (opts.TimeoutSeconds > 0)
        {
            cts.CancelAfter(TimeSpan.FromSeconds(opts.TimeoutSeconds));
            logger.Log($"Timeout guard enabled: {opts.TimeoutSeconds} seconds.");
        }

        var ctx = new BuildContext(config, opts, ui, logger, plugins, cts.Token);
        if (config.CachePath.Length > 0 && config.DeepVerify)
            ctx.HashCache = HashCache.Load(config.CachePath, logger);

        PrintConfiguration(ctx);

        if (!opts.NoPrompt && !opts.Silent && !opts.Check)
        {
            ui.Write("Build ISO now? (Y/n): ");
            string c = Console.ReadLine() ?? string.Empty;
            string normalized = c.Trim().ToLowerInvariant();
            if (!PromptHelper.ParseYesNo(c) && (normalized == "n" || normalized == "no"))
                return ExitError("Build cancelled by user.", ui, opts, ExitCodes.Cancelled);
        }

        try
        {
            plugins.OnBeforeBuild(ctx);
            ExecutePipeline(ctx, totalWatch);

            if (!ctx.State.Failed && cts.IsCancellationRequested)
                ctx.State.Cancel(CancelMessage(opts, totalWatch));

            if (ctx.State.Failed) IsoBuilderService.Rollback(ctx);
            else ctx.HashCache?.Save(logger);
        }
        finally
        {
            ctx.OutputLock?.Dispose();
            ctx.OutputLock = null;
        }

        if (!string.IsNullOrEmpty(opts.JsonReportPath))
            BuildReportWriter.Write(Path.GetFullPath(opts.JsonReportPath), ctx);

        if (ctx.State.Failed)
        {
            logger.Error(ctx.State.FailureMessage);
            logger.Save();
            return ExitError(ctx.State.FailureMessage, ui, opts, ExitCodeFor(ctx.State));
        }

        PrintSummary(ctx);
        logger.Save();

        plugins.OnAfterBuild(ctx);
        if (ctx.State.Failed)
        {
            logger.Save();
            return ExitError(ctx.State.FailureMessage, ui, opts, ExitCodeFor(ctx.State));
        }

        Pause(ui, opts);
        return ExitCodes.Success;
    }

    static void ExecutePipeline(BuildContext ctx, Stopwatch totalWatch)
    {
        IsoBuilderOptions opts = ctx.Opts;
        BuildLogger logger = ctx.Logger;
        BuildPipeline pipeline = BuildPipeline.CreateDefault();

        foreach (IBuildStep step in pipeline.Steps)
        {
            if (ctx.State.Failed) break;

            if (ctx.CancellationToken.IsCancellationRequested)
            {
                ctx.State.Cancel(CancelMessage(opts, totalWatch));
                break;
            }

            if (BuildPipeline.IsSkipped(step, opts))
            {
                if (opts.Verbose && !opts.Silent) ctx.Ui.WriteLine($"[STEP] {step.Name} (skipped)");
                ctx.State.Steps.Add(new StepResult { Name = step.Name, Status = "skipped", Seconds = 0, Message = string.Empty });
                continue;
            }

            if (opts.Verbose && !opts.Silent) ctx.Ui.WriteLine($"[STEP] {step.Name}");

            ctx.Plugins.OnStepExecuting(ctx, step);
            if (ctx.State.Failed)
            {
                ctx.State.FailedStep = step.Name;
                break;
            }

            var stepWatch = Stopwatch.StartNew();
            try
            {
                if (ctx.CancellationToken.IsCancellationRequested) throw new OperationCanceledException(ctx.CancellationToken);
                step.Execute(ctx);
            }
            catch (OperationCanceledException)
            {
                ctx.State.Cancel(CancelMessage(opts, totalWatch));
            }
            catch (Exception ex)
            {
                ctx.State.Fail($"Step {step.Name} failed: {ex.Message}");
                logger.Error($"Step {step.Name} exception: {ex}");
            }
            stepWatch.Stop();

            if (ctx.Config.Strict && !ctx.State.Failed && logger.WarningCount > 0)
            {
                IReadOnlyList<string> warnings = logger.Warnings;
                ctx.State.Fail($"Strict mode: {(warnings.Count > 0 ? warnings[0] : "a warning was raised")} ({logger.WarningCount} warning(s) so far).");
            }

            string status = ctx.State.Failed
                ? (ctx.State.Status == BuildStatus.Cancelled ? "cancelled" : "failed")
                : "passed";
            ctx.State.Steps.Add(new StepResult
            {
                Name = step.Name,
                Status = status,
                Seconds = stepWatch.Elapsed.TotalSeconds,
                Message = ctx.State.Failed ? TextSanitizer.Clean(ctx.State.FailureMessage) : string.Empty
            });

            if (ctx.State.Failed)
            {
                ctx.State.FailedStep = step.Name;
                break;
            }

            ctx.Plugins.OnStepExecuted(ctx, step);
            if (ctx.State.Failed)
            {
                ctx.State.FailedStep = step.Name;
                break;
            }
        }
    }

    static void PrintConfiguration(BuildContext ctx)
    {
        BuildConfig config = ctx.Config;
        IsoBuilderOptions opts = ctx.Opts;
        PluginManager plugins = ctx.Plugins;
        ConsoleUI ui = ctx.Ui;
        if (opts.Silent) return;

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
        if (config.IncludePatterns.Count > 0) ui.WriteLine($"Includes   : {string.Join(", ", config.IncludePatterns)}");
        if (config.MaxSizeMb > 0) ui.WriteLine($"Max size   : {config.MaxSizeMb} MB");
        if (config.MaxFileSizeMb > 0) ui.WriteLine($"Max file   : {config.MaxFileSizeMb} MB");
        if (config.MaxFiles > 0) ui.WriteLine($"Max files  : {config.MaxFiles}");
        ui.WriteLine($"Max depth  : {config.MaxDepth}");
        if (config.ManifestEnabled) ui.WriteLine("Manifest   : enabled");
        if (config.DeepVerify) ui.WriteLine("Deep verify: enabled");
        if (config.Reproducible) ui.WriteLine($"Reproducible: enabled (epoch {config.SourceDateEpoch})");
        if (config.Strict) ui.WriteLine("Strict     : enabled");
        if (opts.Profile.Length > 0) ui.WriteLine($"Profile    : {opts.Profile}");
        if (opts.BootTest) ui.WriteLine("Boot test  : enabled");
        ui.WriteLine($"Collisions : {config.CollisionMode}");
        if (opts.Check) ui.WriteLine("Mode       : CHECK (diagnostics only)");
        else if (opts.DryRun) ui.WriteLine("Mode       : DRY RUN (no ISO will be written)");
        ui.WriteLine($"Plugins    : {(plugins.Plugins.Count > 0 ? string.Join(", ", plugins.Plugins.Select(p => TextSanitizer.Clean(p.Name))) : "none")}");
        ui.WriteLine("===========================");
    }

    static void PrintSummary(BuildContext ctx)
    {
        BuildConfig config = ctx.Config;
        IsoBuilderOptions opts = ctx.Opts;
        ConsoleUI ui = ctx.Ui;
        BuildLogger logger = ctx.Logger;

        if (!opts.Silent)
        {
            ui.WriteLine(string.Empty);
            if (opts.Check)
            {
                ui.WriteLine("Preflight checks completed successfully. No ISO file was written.");
                ui.WriteLine($"Files that would be added: {ctx.State.TotalFiles}");
                ui.WriteLine($"Predicted size: {ctx.State.PredictedSizeMb} MB");
            }
            else if (opts.DryRun)
            {
                ui.WriteLine("Dry run completed successfully. No ISO file was written.");
                ui.WriteLine($"Files that would be added: {ctx.State.ProcessedFiles}");
                ui.WriteLine($"Predicted size: {ctx.State.PredictedSizeMb} MB");
                ui.WriteLine($"Exact size from layout: {(ctx.State.ExactSizeBytes / (1024.0 * 1024.0)).ToString("F2", CultureInfo.InvariantCulture)} MB");
            }
            else
            {
                ui.WriteLine("ISO created successfully:");
                ui.WriteLine($"Path : {config.IsoOutputPath}");
                ui.WriteLine($"Size : {(ctx.State.ActualSizeBytes / (1024.0 * 1024.0)).ToString("F2", CultureInfo.InvariantCulture)} MB");
                ui.WriteLine($"Time : {ctx.State.BuildSeconds.ToString("F2", CultureInfo.InvariantCulture)} s");
                ui.WriteLine($"Speed: {ctx.State.BuildSpeedMbPerSec.ToString("F2", CultureInfo.InvariantCulture)} MB/s");
                ui.WriteLine($"Files: {ctx.State.ProcessedFiles} added, {ctx.State.SkippedFiles} skipped");
                if (ctx.State.BootEntries.Count > 0) ui.WriteLine($"Boot : {ctx.State.BootEntries.Count} El Torito entr{(ctx.State.BootEntries.Count == 1 ? "y" : "ies")}");
                if (logger.WarningCount > 0) ui.WriteLine($"Warnings: {logger.WarningCount} (see {logger.LogPath})");
            }
        }

        if (!opts.DryRun && !opts.Check)
        {
            logger.Log($"ISO created: {config.IsoOutputPath}");
            logger.Log($"Size bytes: {ctx.State.ActualSizeBytes}");
            logger.Log($"Time: {ctx.State.BuildSeconds.ToString("F2", CultureInfo.InvariantCulture)} s");
            logger.Log($"Speed: {ctx.State.BuildSpeedMbPerSec.ToString("F2", CultureInfo.InvariantCulture)} MB/s");
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
            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ExitCodes.Error;
        }
    }

    static void PrintHelp()
    {
        Console.WriteLine($"BuildIso {BuildPaths.AppVersion}");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  BuildIso init");
        Console.WriteLine("  BuildIso -p <project> [options]");
        Console.WriteLine("  BuildIso --inspect <iso>");
        Console.WriteLine("  BuildIso --verify-iso <iso>");
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
        Console.WriteLine("  --secureboot        Secure Boot checks (requires --uefi)");
        Console.WriteLine("  --bios-only         BIOS-only boot");
        Console.WriteLine("  --uefi-only         UEFI-only boot");
        Console.WriteLine("  --boot-priority <uefi|bios|auto>  Which El Torito entry comes first when both are present");
        Console.WriteLine("  --allow-unknown-boot  Continue when boot.bin is not a recognized boot sector");
        Console.WriteLine();
        Console.WriteLine("Guards:");
        Console.WriteLine("  --max-size <mb>     Abort if the ISO size exceeds this limit");
        Console.WriteLine("  --max-files <n>     Abort if scanned file count exceeds this limit");
        Console.WriteLine("  --max-file-size <mb>  Abort if any single file exceeds this size");
        Console.WriteLine("  --max-depth <n>     Maximum directory depth (default 64)");
        Console.WriteLine("  --timeout <seconds> Abort the build if it runs longer than this");
        Console.WriteLine("  --exclude <glob>    Exclude files/directories matching a glob pattern (repeatable)");
        Console.WriteLine("  --include <glob>    Only include files matching a glob pattern (repeatable)");
        Console.WriteLine("  --collision-policy <rename|fail|skip>  What to do when two names collide in the ISO");
        Console.WriteLine();
        Console.WriteLine("Plugins:");
        Console.WriteLine("  --plugins-off               Disable all plugins");
        Console.WriteLine("  -Pu, --plugin <name>        Allow specific plugin by name");
        Console.WriteLine("  --plugin-hash <name>=<sha256>  Pin an allow-listed plugin to an exact hash");
        Console.WriteLine("  --require-plugin-hash       Refuse plugins that have no hash pin");
        Console.WriteLine("  --plugin-timeout <seconds>  Time limit for each plugin callback (default 30)");
        Console.WriteLine("  --plugin-strict             Fail the build when a plugin callback fails");
        Console.WriteLine();
        Console.WriteLine("Verification:");
        Console.WriteLine("  --check              Run diagnostics only, no ISO is built");
        Console.WriteLine("  --dry-run            Simulate the ISO layout without writing the ISO");
        Console.WriteLine("  --strict             Treat warnings as failures");
        Console.WriteLine("  --fix-safe           Apply non-destructive fixes found by the preflight");
        Console.WriteLine("  --deep-verify        Re-read every file from the ISO and compare it with its source");
        Console.WriteLine("  --expect-sha256 <hex>  Fail unless the final ISO has this SHA-256");
        Console.WriteLine("  --boot-test          Smoke-test the ISO in QEMU");
        Console.WriteLine("  --qemu <path>        Path to qemu-system-x86_64");
        Console.WriteLine("  --ovmf <path>        OVMF firmware image for the UEFI boot test");
        Console.WriteLine("  --boot-test-timeout <seconds>  How long the virtual machine must stay alive (default 20)");
        Console.WriteLine("  --inspect <iso>      Print the structure and boot catalog of an ISO");
        Console.WriteLine("  --verify-iso <iso>   Verify an existing ISO (and its manifest, if present)");
        Console.WriteLine();
        Console.WriteLine("Output:");
        Console.WriteLine("  --manifest           Write a MANIFEST.SHA256SUMS file next to the ISO");
        Console.WriteLine("  --json-report <file> Write a build report to this path");
        Console.WriteLine("  --report-format <json|junit|sarif>  Format of the build report");
        Console.WriteLine("  --no-overwrite       Fail if the output file already exists");
        Console.WriteLine("  --keep-backup        Keep the previous ISO as .bak after a successful build");
        Console.WriteLine("  --reproducible       Normalize timestamps and ordering for repeatable output");
        Console.WriteLine("  --source-date-epoch <seconds>  Timestamp used by --reproducible");
        Console.WriteLine();
        Console.WriteLine("Configuration:");
        Console.WriteLine("  --config <file>      Load default options from a JSON config file");
        Console.WriteLine("  --profile <development|release|reproducible|ci>  Apply a set of defaults");
        Console.WriteLine("  --threads <n>        Hashing threads (default 1)");
        Console.WriteLine("  --cache <file>       Hash cache used by --deep-verify");
        Console.WriteLine("  --log <file>         Log file path (default: buildiso.log in the project)");
        Console.WriteLine("  --no-log             Do not write a log file");
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
        Console.WriteLine();
        Console.WriteLine("Boolean options accept =true or =false (for example --uefi=false).");
        Console.WriteLine("Exit codes: 0 success, 1 error, 2 invalid arguments, 3 preflight failed, 4 validation failed, 5 cancelled, 6 boot test failed.");
    }

    static int ExitError(string msg, ConsoleUI ui, IsoBuilderOptions opts, int code = ExitCodes.Error)
    {
        ui.Error($"Error: {msg}");
        Pause(ui, opts);
        return code;
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

        if (!opts.NoPrompt && !opts.Silent && !opts.BiosOnly && !opts.UefiOnly && !opts.Explicit.Contains("uefi"))
        {
            ui.Write("Include UEFI? (y/N): ");
            string u = Console.ReadLine() ?? string.Empty;
            includeUefi = PromptHelper.ParseYesNo(u);
            biosEnabled = true;

            if (includeUefi && !File.Exists(efiImgPath))
            {
                ui.WriteLine($"Error: UEFI selected but efiboot.img not found: {efiImgPath}");
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

    static PathCollisionMode ParseCollision(string value) => value.Trim().ToLowerInvariant() switch
    {
        "fail" => PathCollisionMode.Fail,
        "skip" => PathCollisionMode.Skip,
        _ => PathCollisionMode.Rename
    };

    static long ResolveEpoch(IsoBuilderOptions opts)
    {
        if (opts.SourceDateEpoch >= 0) return opts.SourceDateEpoch;
        string? env = Environment.GetEnvironmentVariable("SOURCE_DATE_EPOCH");
        if (!string.IsNullOrWhiteSpace(env)
            && long.TryParse(env.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
            && parsed >= 0 && parsed <= 4_102_444_799L)
            return parsed;
        return 946684800L;
    }

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
        bool mutating = !opts.DryRun && !opts.Check;
        if (mutating && !string.IsNullOrEmpty(outputParent) && !Directory.Exists(outputParent))
        {
            try { Directory.CreateDirectory(outputParent); }
            catch (Exception ex) { ui.WriteLine($"Error: Cannot create output directory: {ex.Message}"); return null; }
        }

        string outputDir = outputParent ?? projectDir;
        string manifestPath = Path.Combine(outputDir, BuildPaths.ManifestFilename);
        string readmePath = Path.Combine(outputDir, BuildPaths.ReadmeFilename);
        string cachePath = string.IsNullOrEmpty(opts.CachePath) ? string.Empty : Path.GetFullPath(opts.CachePath);

        var reserved = new HashSet<string>(PathComparer.Instance)
        {
            isoOutputPath,
            isoOutputPath + ".tmp",
            isoOutputPath + ".bak",
            isoOutputPath + ".failed",
            isoOutputPath + ".lock",
            manifestPath,
            manifestPath + ".tmp",
            readmePath,
            readmePath + ".tmp",
            Path.GetFullPath(logger.LogPath)
        };
        if (!string.IsNullOrEmpty(opts.JsonReportPath))
        {
            string reportPath = Path.GetFullPath(opts.JsonReportPath);
            reserved.Add(reportPath);
            reserved.Add(reportPath + ".tmp");
        }
        if (cachePath.Length > 0)
        {
            reserved.Add(cachePath);
            reserved.Add(cachePath + ".tmp");
        }

        int threads = opts.Threads > 0 ? opts.Threads : 1;

        return new BuildConfig
        {
            ProjectDir = projectDir,
            BootDir = bootDir,
            IsoDir = isoDir,
            BootBinPath = bootBinPath,
            EfiImgPath = efiImgPath,
            IsoOutputPath = isoOutputPath,
            TempIsoPath = isoOutputPath + ".tmp",
            BackupIsoPath = isoOutputPath + ".bak",
            FailedIsoPath = isoOutputPath + ".failed",
            LockPath = isoOutputPath + ".lock",
            ManifestPath = manifestPath,
            ReadmePath = readmePath,
            CachePath = cachePath,
            VolumeId = volumeId,
            IncludeUefi = includeUefi,
            BiosEnabled = biosEnabled,
            SecureBoot = secureBoot,
            Strict = opts.Strict,
            DeepVerify = opts.DeepVerify,
            Reproducible = opts.Reproducible,
            SourceDateEpoch = ResolveEpoch(opts),
            ExpectSha256 = opts.ExpectSha256,
            Threads = threads,
            BootPriority = bootPriority,
            CollisionMode = opts.Strict ? PathCollisionMode.Fail : ParseCollision(opts.CollisionPolicy),
            ExcludePatterns = opts.ExcludePatterns,
            IncludePatterns = opts.IncludePatterns,
            ReservedPaths = reserved,
            MaxSizeMb = opts.MaxSizeMb,
            MaxFileSizeMb = opts.MaxFileSizeMb,
            MaxFiles = opts.MaxFiles,
            MaxDepth = opts.MaxDepth > 0 ? opts.MaxDepth : 64,
            ManifestEnabled = opts.Manifest
        };
    }
}
