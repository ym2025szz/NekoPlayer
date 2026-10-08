using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using NekoPlayer.App;

namespace NekoPlayer.Tests;

public sealed class RuntimeManifestTests
{
    [Fact]
    public void BundledNodeFilesMatchOfficialArchiveProvenance()
    {
        var directory = Path.Combine(SourceContracts.Root, "tools", "node");
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "metadata.json")));
        var metadata = document.RootElement;
        Assert.Equal("v24.21.0", metadata.GetProperty("NodeVersion").GetString());
        Assert.Equal("https://nodejs.org/dist/v24.21.0/node-v24.21.0-win-x64.zip", metadata.GetProperty("ArchiveUrl").GetString());
        Assert.Equal("158f7685b44de51f6c0df1d153526cbcd3e1bc739a8dfc607721cef75de9e541", metadata.GetProperty("ArchiveSha256").GetString());
        Assert.True(metadata.GetProperty("OfficialSha256Verified").GetBoolean());
        Assert.Equal(metadata.GetProperty("NodeExeSha256").GetString(), Hash(Path.Combine(directory, "node.exe")));
        Assert.Equal(metadata.GetProperty("LicenseSha256").GetString(), Hash(Path.Combine(directory, "LICENSE")));
        Assert.True(new FileInfo(Path.Combine(directory, "LICENSE")).Length > 150_000);
        Assert.Contains("Node.js is licensed", File.ReadAllText(Path.Combine(directory, "LICENSE")));
    }

    [Fact]
    public async Task BundledNodeStartsWithoutGlobalNodeInPath()
    {
        if (!OperatingSystem.IsWindows()) return;
        var executable = Path.Combine(SourceContracts.Root, "tools", "node", "node.exe");
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.Combine(SourceContracts.Root, "tools", "node")
        };
        startInfo.ArgumentList.Add("--version");
        startInfo.Environment["PATH"] = Environment.SystemDirectory;
        using var process = Process.Start(startInfo)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("v24.21.0", (await process.StandardOutput.ReadToEndAsync()).Trim());
            Assert.Empty(await process.StandardError.ReadToEndAsync());
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void ProductVersionMetadataAndWindowsManifestAgree()
    {
        var project = XDocument.Load(Path.Combine(SourceContracts.Root, "src", "NekoPlayer.App", "NekoPlayer.App.csproj"));
        var projectVersion = project.Descendants("Version").Single().Value;
        Assert.Equal(projectVersion, AppVersionInfo.Version);
        Assert.Equal("v" + projectVersion, AppVersionInfo.DisplayVersion);
        var manifest = XDocument.Load(Path.Combine(SourceContracts.Root, "src", "NekoPlayer.App", "app.manifest"));
        var identity = manifest.Descendants().Single(x => x.Name.LocalName == "assemblyIdentity");
        Assert.Equal(projectVersion + ".0", identity.Attribute("version")?.Value);
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
