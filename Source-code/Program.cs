using System;
using System.IO;
using System.Net.Http;
using DiscUtils.Iso9660;
using System.Text;
using System.Threading; // ← AJOUTE ÇA


class Program
{
    public static int totalFiles;
    public static int processedFiles;

    private static bool spinnerRunning;          // ← AJOUT
    private static Thread? spinnerThread;        // ← AJOUT


    static int Main(string[] args)
    {
        const string VERSION = "2026.7";

        // --- VERSION MODE ---
        if (args.Length > 0 && args[0].Equals("--version", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("BuildIso version " + VERSION);

            try
            {
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("BuildIso-Updater");
                    client.Timeout = TimeSpan.FromSeconds(5);

                    var json = client.GetStringAsync("https://api.github.com/repos/BuildIso/BuildIso/releases/latest").Result;

                    // extraction simple du tag_name
                    string marker = "\"tag_name\":";
                    int i = json.IndexOf(marker);
                    if (i != -1)
                    {
                        int start = json.IndexOf('"', i + marker.Length) + 1;
                        int end = json.IndexOf('"', start);
                        string latestTag = json.Substring(start, end - start);

                        if (latestTag != "v" + VERSION)
                            Console.WriteLine("A new update is available: " + latestTag);
                        else
                            Console.WriteLine("You already have the latest version.");
                    }
                    else
                    {
                        Console.WriteLine("Unable to parse update information.");
                    }
                }
            }
            catch
            {
                Console.WriteLine("Unable to check for updates (offline?).");
            }

            return 0;
        }


        Console.WriteLine("========================================");
        Console.WriteLine("      Welcome to BuildIso v2026.7");
        Console.WriteLine("========================================\n");

        string projectDir;

        // --- INTERACTIVE MODE ---
        if (args.Length == 0)
        {
            Console.WriteLine("No project path provided.");
            Console.WriteLine("Please enter the path of your OS project:");
            Console.Write("> ");

            string? raw = Console.ReadLine();
            projectDir = (raw ?? "").Trim();

            if (projectDir == "")
                return ExitError("No path entered.");
        }
        else
        {
            projectDir = args[0].Trim().Trim('"');
        }

        if (!Directory.Exists(projectDir))
            return ExitError("Project directory does not exist.");

        Console.Write("Do you want to build the ISO? (Y/n): ");
        string? confirmRaw = Console.ReadLine();
        string confirm = (confirmRaw ?? "").Trim().ToLowerInvariant();

        if (confirm == "n" || confirm == "no")
        {
            Console.WriteLine("Build cancelled.");
            Pause();
            return 0;
        }

        // ===============================
        //   UEFI + Secure Boot questions
        // ===============================

        Console.Write("Do you want to include UEFI support? (Y/n): ");
        string? uefiRaw = Console.ReadLine();
        string uefi = (uefiRaw ?? "").Trim().ToLowerInvariant();

        bool includeUefi = !(uefi == "n" || uefi == "no");
        bool enableSecureBoot = false;

        if (includeUefi)
        {
            Console.Write("Do you want to enable Secure Boot? (Y/n): ");
            string? sbRaw = Console.ReadLine();
            string sb = (sbRaw ?? "").Trim().ToLowerInvariant();

            enableSecureBoot = !(sb == "n" || sb == "no");
        }

        Console.WriteLine("\n=== Build Configuration ===");
        Console.WriteLine("BIOS support: YES");
        Console.WriteLine("UEFI support: " + (includeUefi ? "YES" : "NO"));
        Console.WriteLine("Secure Boot: " + (enableSecureBoot ? "YES (handled by your OS)" : "NO"));
        Console.WriteLine("===========================\n");

        // ===============================
        //   BUILD PROCESS
        // ===============================

        projectDir = projectDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        string[] csprojs = Directory.GetFiles(projectDir, "*.csproj");
        if (csprojs.Length == 0)
            return ExitError("No .csproj file found.");

        string csproj = csprojs[0];
        string projectName = Path.GetFileNameWithoutExtension(csproj);

        string bootDir = Path.Combine(projectDir, "boot");
        string bootBin = Path.Combine(bootDir, "boot.bin");
        string efiImg = Path.Combine(bootDir, "efiboot.img");
        string isoRoot = Path.Combine(projectDir, "iso_root");
        var logger = new BuildLogger(projectDir);
        string isoOutput = Path.Combine(projectDir, projectName + ".iso");

        if (!File.Exists(bootBin))
            return ExitError("boot.bin not found in boot/ directory.");

        if (!Directory.Exists(isoRoot))
            return ExitError("iso_root directory not found.");
        // === DISK SPACE CHECK ===
        long predictedMb = PredictIsoSizeMb(isoRoot, bootDir);
        Console.WriteLine($"Predicted ISO size: {predictedMb} MB");


        if (!CheckDiskSpace(projectDir, predictedMb, logger))
            return ExitError("Build cancelled due to insufficient disk space.");

        // BIOS bootloader validation
        BootImageType bootType;
        if (!IsValidBootImage(bootBin, out bootType))
            return ExitError("boot.bin is not a valid boot image (512 or 2048 bytes).");

        Console.WriteLine("Detected BIOS boot image type: " + bootType);

        // UEFI presence check
        string efiBootPathInIso = Path.Combine(isoRoot, "EFI", "BOOT", "BOOTX64.EFI");
        if (includeUefi)
        {
            if (!File.Exists(efiImg))
                return ExitError("UEFI selected but efiboot.img not found in boot/ directory.");

            if (!File.Exists(efiBootPathInIso))
                Console.WriteLine("Warning: EFI/BOOT/BOOTX64.EFI not found in iso_root (but efiboot.img exists).");
        }

        if (enableSecureBoot)
        {
            Console.WriteLine("NOTE: Secure Boot enabled.");
            Console.WriteLine("Make sure efiboot.img is already signed by your OSDev toolchain.");
        }

        try
        {
            // INITIALISATION BARRE DE PROGRESSION
            Program.totalFiles = CountFiles(isoRoot);
            Program.processedFiles = 0;
            Console.WriteLine("Adding files:");

            var builder = new CDBuilder
            {
                UseJoliet = true,
                VolumeIdentifier = projectName.ToUpperInvariant()
            };

            AddDirectoryRecursive(builder, isoRoot, "");

            // Keep streams open until after Build()
            var biosStream = File.OpenRead(bootBin);
            MemoryStream? efiStream = null;

            builder.SetBootImage(biosStream, BootDeviceEmulation.NoEmulation, 0);

            if (includeUefi)
            {
                efiStream = new MemoryStream(File.ReadAllBytes(efiImg));
                builder.SetBootImage(efiStream, BootDeviceEmulation.NoEmulation, 1);
            }

            spinnerRunning = true;
            spinnerThread = new Thread(SpinnerLoop);
            spinnerThread.Start();

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            using (var isoStream = builder.Build())
            using (var file = File.Create(isoOutput))
            {
                isoStream.CopyTo(file);
            }

            spinnerRunning = false;
            spinnerThread.Join();
            Console.WriteLine(); // pour passer à la ligne proprement



            biosStream.Dispose();
            efiStream?.Dispose();

            Console.WriteLine("\n✔ ISO created successfully:");
            stopwatch.Stop();

            long sizeBytes = new FileInfo(isoOutput).Length;
            double sizeMb = sizeBytes / (1024.0 * 1024.0);
            double speed = sizeMb / stopwatch.Elapsed.TotalSeconds;

            Console.WriteLine($"Build completed in {stopwatch.Elapsed.TotalSeconds:F2} seconds.");
            Console.WriteLine($"ISO size: {sizeMb:F2} MB");
            Console.WriteLine($"Write speed: {speed:F2} MB/s");
            Console.WriteLine(isoOutput);

            logger.Log("Predicted ISO size: " + predictedMb + " MB");
            logger.Log("ISO created: " + isoOutput);
            logger.Log("UEFI: " + includeUefi);
            logger.Log("Secure Boot: " + enableSecureBoot);
            logger.Save();

            var readme = new ReadmeGenerator(projectDir);
            readme.Write(projectName, VERSION, includeUefi, enableSecureBoot);

            Pause();
            return 0;
        }
        catch (Exception ex)
        {
            return ExitError("Exception: " + ex.Message);
        }
    }

    // ===============================
    //   FUNCTIONS
    // ===============================
    static long GetDirectorySize(string dir)
    {
        long size = 0;

        foreach (var file in Directory.GetFiles(dir))
            size += new FileInfo(file).Length;

        foreach (var sub in Directory.GetDirectories(dir))
            size += GetDirectorySize(sub);

        return size;
    }
    static long PredictIsoSizeMb(string isoRoot, string bootDir)
    {
        long isoRootBytes = GetDirectorySize(isoRoot);
        long bootBytes = GetDirectorySize(bootDir);

        long totalBytes = isoRootBytes + bootBytes;

        // marge de sécurité 10%
        totalBytes = (long)(totalBytes * 1.10);

        return totalBytes / (1024 * 1024);
    }
    static bool CheckDiskSpace(string projectDir, long predictedMb, BuildLogger logger)
    {
        var drive = new DriveInfo(Path.GetPathRoot(projectDir)!);
        long freeMb = drive.AvailableFreeSpace / (1024 * 1024);

        if (freeMb < predictedMb)
        {
            string msg = $"Not enough disk space. Required: {predictedMb} MB, available: {freeMb} MB";
            Console.WriteLine(msg);
            logger.Warn(msg);

            Console.Write("Do you want to continue anyway? (Y/n): ");
            string? raw = Console.ReadLine();
            string ans = (raw ?? "").Trim().ToLowerInvariant();

            return !(ans == "n" || ans == "no");
        }

        return true;
    }

    static int CountFiles(string dir)
    {
        int count = Directory.GetFiles(dir).Length;
        foreach (var sub in Directory.GetDirectories(dir))
            count += CountFiles(sub);
        return count;
    }

    public static void UpdateProgress()
    {
        processedFiles++;

        double ratio = (double)processedFiles / totalFiles;
        int barSize = 20;
        int filled = (int)(ratio * barSize);

        string bar = "[" + new string('#', filled) + new string('-', barSize - filled) + "]";
        Console.Write($"\rAdding files: {bar} {(ratio * 100):F0}% ({processedFiles}/{totalFiles})");
    }
    public static void SpinnerLoop()
    {
        char[] seq = new[] { '/', '-', '\\', '|' };
        int i = 0;

        while (spinnerRunning)
        {
            Console.Write($"\rBuilding ISO... {seq[i++ % seq.Length]}");
            Thread.Sleep(100);
        }

        Console.Write("\rBuilding ISO... done      \n");
    }

    static bool IsValidBootImage(string path, out BootImageType type)
    {
        byte[] data = File.ReadAllBytes(path);

        if (data.Length == 512)
        {
            if (data[510] == 0x55 && data[511] == 0xAA)
            {
                type = BootImageType.BiosBootSector512;
                return true;
            }

            type = default;
            return false;
        }

        if (data.Length == 2048)
        {
            type = BootImageType.ElTorito2048;
            return true;
        }

        type = default;
        return false;
    }

    static void AddDirectoryRecursive(CDBuilder builder, string sourceDir, string isoPath)
    {
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            string name = Path.GetFileName(file);
            string isoFilePath = string.IsNullOrEmpty(isoPath) ? name : isoPath + "/" + name;
            builder.AddFile(isoFilePath, file);
            Program.UpdateProgress();
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            string name = Path.GetFileName(dir);
            string newIsoPath = string.IsNullOrEmpty(isoPath) ? name : isoPath + "/" + name;
            AddDirectoryRecursive(builder, dir, newIsoPath);
        }
    }

    static int ExitError(string msg)
    {
        Console.WriteLine("Error: " + msg);
        Pause();
        return 1;
    }

    static void Pause()
    {
        Console.WriteLine("\nPress ENTER to exit...");
        Console.ReadLine();
    }

    enum BootImageType
    {
        BiosBootSector512,
        ElTorito2048
    }
}

public class BuildLogger
{
    private readonly string _path;
    private readonly System.Text.StringBuilder _buffer = new();

    public BuildLogger(string projectDir)
    {
        _path = System.IO.Path.Combine(projectDir, "build.log");
        _buffer.AppendLine("=== BuildIso Log ===");
        _buffer.AppendLine("Timestamp: " + System.DateTime.Now);
        _buffer.AppendLine();
    }

    public void Log(string msg)
    {
        string line = "[INFO] " + msg;
        _buffer.AppendLine(line);
        System.Console.WriteLine(line);
    }

    public void Warn(string msg)
    {
        string line = "[WARN] " + msg;
        _buffer.AppendLine(line);
        System.Console.WriteLine(line);
    }

    public void Error(string msg)
    {
        string line = "[ERROR] " + msg;
        _buffer.AppendLine(line);
        System.Console.WriteLine(line);
    }

    public void Save()
    {
        System.IO.File.AppendAllText(_path, _buffer.ToString());
    }
}

public class ReadmeGenerator
{
    private readonly string _path;

    public ReadmeGenerator(string projectDir)
    {
        _path = System.IO.Path.Combine(projectDir, "README.md");
    }

    public void Write(string projectName, string version, bool uefi, bool secureBoot)
    {
        var md = new System.Text.StringBuilder();

        md.AppendLine("# " + projectName);
        md.AppendLine();
        md.AppendLine("Generated with **BuildIso " + version + "**");
        md.AppendLine("MIT LICENSE");
        md.AppendLine("## Build Configuration");
        md.AppendLine("- BIOS: **YES**");
        md.AppendLine("- UEFI: **" + (uefi ? "YES" : "NO") + "**");
        md.AppendLine("- Secure Boot: **" + (secureBoot ? "YES" : "NO") + "**");
        md.AppendLine();
        md.AppendLine("## Output");
        md.AppendLine("This folder contains the generated ISO and build logs.");
        md.AppendLine();

        System.IO.File.AppendAllText(_path, md.ToString());
    }
}
