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
    public IReadOnlyList<string> PluginAllowList { get; init; } = Array.Empty<string>();

    public static IsoBuilderOptions Parse(string[] args)
    {
        string project = string.Empty, output = string.Empty, bootDir = string.Empty,
               isoDir = string.Empty, volumeId = string.Empty;
        bool uefi = false, secureBoot = false, silent = false, noPrompt = false,
             noSpinner = false, noProgress = false, biosOnly = false, uefiOnly = false,
             init = false, version = false, help = false, verbose = false, pluginsOff = false;
        var allowList = new List<string>();
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
                case "-Pu": case "--plugin":
                    string pn = Next();
                    if (!string.IsNullOrWhiteSpace(pn)) allowList.Add(pn.Trim());
                    break;
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
            ProjectDir = project, Output = output, BootDir = bootDir, IsoDir = isoDir,
            VolumeId = volumeId, Uefi = uefi, SecureBoot = secureBoot, Silent = silent,
            NoPrompt = noPrompt, NoSpinner = noSpinner, NoProgress = noProgress,
            BiosOnly = biosOnly, UefiOnly = uefiOnly, Init = init, Version = version,
            Help = help, Verbose = verbose, PluginsOff = pluginsOff,
            PluginAllowList = allowList.AsReadOnly()
        };
    }
}

sealed class PluginManager
{
    readonly List<IBuildPlugin> _plugins = new();
    readonly HashSet<string> _allowedNames = new(StringComparer.OrdinalIgnoreCase);
    readonly bool _noPrompt;
    readonly bool _silent;

    public IReadOnlyList<IBuildPlugin> Plugins => _plugins;
    public PluginTrustMode TrustMode { get; private set; } = PluginTrustMode.Disabled;

    public PluginManager(bool noPrompt, bool silent)
    {
        _noPrompt = noPrompt;
        _silent = silent;
    }

    public void Configure(IsoBuilderOptions opts)
    {
        if (opts.PluginsOff) { TrustMode = PluginTrustMode.Disabled; return; }
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
                string hash = ComputeFileHash(dll);
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
                            lock (_plugins) _plugins.Add(plugin);
                            logger.Log($"Loaded plugin: {plugin.Name} from {Path.GetFileName(dll)} [SHA256: {hash[..16]}...]");
                        }
                    }
                    catch (Exception ex) { logger.Error($"Failed to instantiate plugin {t.FullName}: {ex}"); }
                }
            }
            catch (Exception ex) { logger.Warn($"Failed to load plugin assembly {dll}: {ex.Message}"); }
        }
    }

    static string ComputeFileHash(string path)
    {
        const int maxAttempts = 3;
        const long maxStreamSize = 512 * 1024 * 1024;
        
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                FileInfo finfo = new FileInfo(path);
                if (finfo.Length > maxStreamSize)
                    return "file_too_large";

                using var fs = File.OpenRead(path);
                byte[] hash = SHA256.HashData(fs);
                return Convert.ToHexString(hash).ToLowerInvariant();
            }
            catch (IOException) when (attempt < maxAttempts - 1)
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

    bool IsPluginAllowed(string name, string dllPath, string hash, BuildLogger logger, ConsoleUI ui)
    {
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
    public const string AppVersion = "2026.9";
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
        int count = FileTreeScanner.CountFiles(ctx.Config.IsoDir, ctx.Logger);
        ctx.State.TotalFiles = count;
        ctx.State.ProcessedFiles = 0;
        if (!ctx.Opts.Silent) ctx.Ui.WriteLine($"Files to add: {count}");
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

        byte[] isoHeader = new byte[32768];
        try
        {
            using (var fs = File.OpenRead(ctx.Config.IsoOutputPath))
            {
                if (fs.Read(isoHeader, 0, 32768) < 32768)
                {
                    ctx.Logger.Warn("ISO file too small to read full header.");
                }
                else if (!(isoHeader[1] == (byte)'C' && isoHeader[2] == (byte)'D' && isoHeader[3] == (byte)'0' && isoHeader[4] == (byte)'0' && isoHeader[5] == (byte)'1'))
                {
                    ctx.Logger.Warn("ISO 9660 signature not found at expected position.");
                }
            }
        }
        catch (Exception ex) { ctx.Logger.Warn($"Failed to validate ISO header: {ex.Message}"); }

        string hash = ComputeHash(ctx.Config.IsoOutputPath, ctx.Logger);
        ctx.Logger.Log($"ISO SHA256: {hash}");
        if (!ctx.Opts.Silent) ctx.Ui.WriteLine($"ISO SHA256: {hash}");
    }

    static string ComputeHash(string path, BuildLogger logger)
    {
        try
        {
            using (var fs = File.OpenRead(path))
            {
                byte[] h = SHA256.HashData(fs);
                return Convert.ToHexString(h).ToLowerInvariant();
            }
        }
        catch (Exception ex) { logger.Error($"Hash computation failed: {ex.Message}"); return "unknown"; }
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
        ReadmeGenerator.Write(outputDir, projectName, BuildPaths.AppVersion, ctx.Config.BiosEnabled, ctx.Config.IncludeUefi, ctx.Config.SecureBoot, ctx.Logger);
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
            FileTreeScanner.AddToBuilder(builder, ctx.Config.IsoDir, ctx.Logger, ref processed);
            ctx.State.ProcessedFiles = processed;

            bool bootSet = false;
            if (ctx.Config.IncludeUefi && File.Exists(ctx.Config.EfiImgPath))
            {
                bootStream = File.OpenRead(ctx.Config.EfiImgPath);
                builder.SetBootImage(bootStream, BootDeviceEmulation.NoEmulation, 0);
                bootSet = true;
            }
            else if (ctx.Config.BiosEnabled && File.Exists(ctx.Config.BootBinPath))
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

    public static int CountFiles(string dir, BuildLogger logger)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return 0;
        int count = 0;
        Traverse(dir, logger, (f) =>
        {
            count++;
            if (count > 1_000_000) { logger.Warn("Too many files, scan capped at 1,000,000."); return false; }
            return true;
        }, null);
        return count;
    }

    public static void AddToBuilder(CDBuilder builder, string rootDir, BuildLogger logger, ref int processedFiles)
    {
        if (string.IsNullOrEmpty(rootDir) || !Directory.Exists(rootDir)) return;
        string basePath = Path.GetFullPath(rootDir);
        int processed = 0;

        Traverse(rootDir, logger, null, (dir, f) =>
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
        if (sanitized.Length > 207) sanitized = sanitized[..207];
        return sanitized;
    }

    static void Traverse(string rootDir, BuildLogger logger, Func<string, bool>? onFile, Func<string, string, bool>? onFileWithDir)
    {
        var queue = new Queue<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        queue.Enqueue(Path.GetFullPath(rootDir));
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

            foreach (string f in files)
            {
                if (IgnoredFiles.Contains(Path.GetFileName(f))) continue;
                if (onFile != null && !onFile(f)) return;
                onFileWithDir?.Invoke(current, f);
            }

            IEnumerable<string> dirs;
            try { dirs = Directory.EnumerateDirectories(current); }
            catch (IOException ex) { logger.Warn($"IO error enumerating dirs in {current}: {ex.Message}"); continue; }
            catch (UnauthorizedAccessException ex) { logger.Warn($"Access denied: {current}: {ex.Message}"); continue; }
            catch (Exception ex) { logger.Warn($"Failed to enumerate dirs in {current}: {ex.Message}"); continue; }

            foreach (string d in dirs)
            {
                if (IgnoredDirs.Contains(Path.GetFileName(d))) continue;
                string resolved = Path.GetFullPath(d);
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
    public static void Write(string outputDir, string projectName, string appVersion, bool bios, bool uefi, bool secureBoot, BuildLogger logger)
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

static class Program
{
    static readonly object ConsoleLock = new();

    static int Main(string[] args)
    {
        try
        {
            var opts = IsoBuilderOptions.Parse(args ?? Array.Empty<string>());

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
            plugins.Configure(opts);

            string exeDir = AppContext.BaseDirectory;
            plugins.LoadFromDirectory(Path.Combine(exeDir, BuildPaths.PluginsDirName), logger, ui);
            plugins.LoadFromDirectory(Path.Combine(projectDir, BuildPaths.PluginsDirName), logger, ui);

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); logger.Log("Build cancelled by user (Ctrl+C)."); };

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
                ui.WriteLine($"Output     : {config.IsoOutputPath}");
                ui.WriteLine($"Volume ID  : {config.VolumeId}");
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

            if (ctx.State.Failed)
            {
                logger.Save();
                return ExitError(ctx.State.FailureMessage, ui, opts);
            }

            if (!opts.Silent)
            {
                ui.WriteLine(string.Empty);
                ui.WriteLine("ISO created successfully:");
                ui.WriteLine($"Path : {config.IsoOutputPath}");
                ui.WriteLine($"Size : {(ctx.State.ActualSizeBytes / (1024.0 * 1024.0)).ToString("F2", CultureInfo.InvariantCulture)} MB");
                ui.WriteLine($"Time : {ctx.State.BuildSeconds.ToString("F2", CultureInfo.InvariantCulture)} s");
                ui.WriteLine($"Speed: {ctx.State.BuildSpeedMbPerSec.ToString("F2", CultureInfo.InvariantCulture)} MB/s");
            }

            logger.Log($"ISO created: {config.IsoOutputPath}");
            logger.Log($"Size bytes: {ctx.State.ActualSizeBytes}");
            logger.Log($"Time: {ctx.State.BuildSeconds.ToString("F2", CultureInfo.InvariantCulture)} s");
            logger.Log($"Speed: {ctx.State.BuildSpeedMbPerSec.ToString("F2", CultureInfo.InvariantCulture)} MB/s");
            logger.Save();

            plugins.OnAfterBuild(ctx);
            Pause(ui, opts);
            return 0;
        }
        catch (Exception ex)
        {
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
        Console.WriteLine("  --uefi          Add UEFI boot (in addition to BIOS)");
        Console.WriteLine("  --secureboot    Secure Boot note (requires --uefi)");
        Console.WriteLine("  --bios-only     BIOS-only boot");
        Console.WriteLine("  --uefi-only     UEFI-only boot");
        Console.WriteLine();
        Console.WriteLine("Plugins:");
        Console.WriteLine("  --plugins-off          Disable all plugins");
        Console.WriteLine("  -Pu, --plugin <name>   Allow specific plugin by name");
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
                return BuildAndValidate(projectDir, bootDir, isoDir, bootBinPath, efiImgPath,
                    isoOutputPath, volumeId, includeUefi, biosEnabled, sb, ui, logger);
            }
        }

        bool secureBoot = opts.SecureBoot && includeUefi;
        return BuildAndValidate(projectDir, bootDir, isoDir, bootBinPath, efiImgPath,
            isoOutputPath, volumeId, includeUefi, biosEnabled, secureBoot, ui, logger);
    }

    static BuildConfig? BuildAndValidate(
        string projectDir, string bootDir, string isoDir,
        string bootBinPath, string efiImgPath, string isoOutputPath,
        string volumeId, bool includeUefi, bool biosEnabled, bool secureBoot,
        ConsoleUI ui, BuildLogger logger)
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
            SecureBoot = secureBoot
        };
    }
}
