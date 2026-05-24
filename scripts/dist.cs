#!/usr/bin/env dotnet
// Run from the repo root: dotnet run scripts/dist.cs [--keep-iis-artifacts]
//
// Windows: builds lance-agent (win-x64 AOT) + lance client (win-x64 AOT).
//          agent is zipped to dist/lance-agent.zip; client is left as dist/client/.
// Linux:   builds lance client (linux-x64 AOT) to dist/client-linux/.
//
// Sample configs from samples/ are copied into each dist directory so the
// zipped artifact is ready to extract and configure.

using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;

if (!Directory.Exists("src"))
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine("error: run from the repository root -- dotnet run scripts/dist.cs");
    Console.ResetColor();
    return 1;
}

bool keepIis  = args.Contains("--keep-iis-artifacts");
var  root     = Directory.GetCurrentDirectory();
var  distDir  = Path.Combine(root, "dist");
var  agentDir = Path.Combine(distDir, "agent");
var  agentZip = Path.Combine(distDir, "lance-agent.zip");

Tint(ConsoleColor.Cyan,    "\nLance distribution build");
Tint(ConsoleColor.DarkGray, $"Platform: {RuntimeInformation.OSDescription}");
Console.WriteLine();

if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
    // AOT linker locates the MSVC toolchain via vswhere -- ensure the VS Installer
    // directory is on PATH if vswhere is present but not yet reachable.
    const string vsInstallerDir = @"C:\Program Files (x86)\Microsoft Visual Studio\Installer";
    if (File.Exists(Path.Combine(vsInstallerDir, "vswhere.exe")))
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (!path.Contains(vsInstallerDir, StringComparison.OrdinalIgnoreCase))
            Environment.SetEnvironmentVariable("PATH", path + Path.PathSeparator + vsInstallerDir);
    }

    Section("Agent (win-x64):");
    Publish("Lance.Agent", "win-x64-aot");
    if (!keepIis) RemoveIisArtifacts(agentDir);
    CopySampleConfig(root, agentDir, "lance-agent.json");
    CreateZip(agentDir, agentZip);

    Console.WriteLine();
    Section("Client (win-x64):");
    Publish("Lance.Client", "win-x64-aot");
    CleanPdbs(Path.Combine(distDir, "client"));
    CopySampleConfig(root, Path.Combine(distDir, "client"), "lance.json");
}

if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
{
    Section("Client (linux-x64):");
    Publish("Lance.Client", "linux-x64-aot");
    CleanPdbs(Path.Combine(distDir, "client-linux"));
    CopySampleConfig(root, Path.Combine(distDir, "client-linux"), "lance.json");
}

ShowArtifacts(distDir);
Console.WriteLine();
Tint(ConsoleColor.Cyan, "Done.");
return 0;

// -- helpers ------------------------------------------------------------------

void Section(string label) => Tint(ConsoleColor.Yellow, label);

void Tint(ConsoleColor color, string text)
{
    Console.ForegroundColor = color;
    Console.WriteLine(text);
    Console.ResetColor();
}

void Publish(string project, string profile)
{
    Tint(ConsoleColor.DarkGray, $"  dotnet publish {project} / {profile}");
    var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false };
    psi.ArgumentList.Add("publish");
    psi.ArgumentList.Add(Path.Combine(root, "src", project));
    psi.ArgumentList.Add($"-p:PublishProfile={profile}");
    psi.ArgumentList.Add("--nologo");
    using var proc = Process.Start(psi) ?? throw new Exception($"could not start dotnet publish for {project}");
    proc.WaitForExit();
    if (proc.ExitCode != 0) throw new Exception($"publish failed: {project} ({profile})");
}

void RemoveIisArtifacts(string dir)
{
    foreach (var pattern in new[] { "web.config", "*.staticwebassets.endpoints.json", "*.pdb" })
        foreach (var f in Directory.EnumerateFiles(dir, pattern))
            File.Delete(f);
}

void CleanPdbs(string dir)
{
    if (!Directory.Exists(dir)) return;
    foreach (var f in Directory.EnumerateFiles(dir, "*.pdb"))
        File.Delete(f);
}

void CopySampleConfig(string repoRoot, string destDir, string fileName)
{
    string src  = Path.Combine(repoRoot, "samples", fileName);
    string dest = Path.Combine(destDir, fileName);
    if (!File.Exists(src)) return;
    if (!Directory.Exists(destDir)) return;
    File.Copy(src, dest, overwrite: true);
    Tint(ConsoleColor.DarkGray, $"  copied {fileName}");
}

void CreateZip(string sourceDir, string destZip)
{
    if (File.Exists(destZip)) File.Delete(destZip);
    ZipFile.CreateFromDirectory(sourceDir, destZip);
    var mb = Math.Round(new FileInfo(destZip).Length / 1_048_576.0, 1);
    Tint(ConsoleColor.Green, $"  -> {Path.GetFileName(destZip)} ({mb} MB)");
}

void ShowArtifacts(string dir)
{
    Console.WriteLine();
    Tint(ConsoleColor.White, "Artifacts:");
    foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
    {
        var rel  = f[(dir.Length + 1)..];
        var info = new FileInfo(f);
        var size = info.Length >= 1_048_576
            ? $"{Math.Round(info.Length / 1_048_576.0, 1)} MB"
            : $"{Math.Round(info.Length / 1024.0, 0)} KB";
        Console.WriteLine($"  {rel,-55} {size,8}");
    }
}
