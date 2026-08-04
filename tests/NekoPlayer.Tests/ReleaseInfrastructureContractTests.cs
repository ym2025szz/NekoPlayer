namespace NekoPlayer.Tests;

public sealed class ReleaseInfrastructureContractTests
{
    [Fact]
    public void CiQuotesLinuxNullAudioDevice()
    {
        var workflow = SourceContracts.Read(@".github\workflows\ci.yml");
        Assert.Equal(2, SourceContracts.Count(workflow, "NEKOPLAYER_LINUX_AUDIO_DEVICE: 'null'"));
        Assert.DoesNotContain("NEKOPLAYER_LINUX_AUDIO_DEVICE: null", workflow);
    }

    [Fact]
    public void CiExercisesUnicodeAndSpacePathsOnLinux()
    {
        var workflow = SourceContracts.Read(@".github\workflows\ci.yml");
        Assert.Contains("猫娘 音频", workflow);
        Assert.Contains("Linux import and duplicate-detection pipeline", workflow);
    }

    [Fact]
    public void CiVerifiesXdgDirectoriesOutsidePublishTree()
    {
        var workflow = SourceContracts.Read(@".github\workflows\ci.yml");
        Assert.Contains("XDG_DATA_HOME", workflow);
        Assert.Contains("XDG_CONFIG_HOME", workflow);
        Assert.Contains("XDG_CACHE_HOME", workflow);
        Assert.Contains("NekoPlayer/nekoplayer.db", workflow);
    }

    [Fact]
    public void LinuxDesktopEntryQuotesExecutablePath()
    {
        var installer = SourceContracts.Read(@"packaging\linux\install-linux.sh");
        Assert.Contains("Exec=\"$install_root/NekoPlayer\"", installer);
    }

    [Fact]
    public void LinuxUninstallerPreservesXdgUserData()
    {
        var uninstaller = SourceContracts.Read(@"packaging\linux\uninstall-linux.sh");
        Assert.DoesNotContain(".local/share/NekoPlayer", uninstaller);
        Assert.DoesNotContain(".config/NekoPlayer", uninstaller);
        Assert.DoesNotContain(".cache/NekoPlayer", uninstaller);
        Assert.Contains("User data under XDG data/config/cache directories was preserved.", uninstaller);
    }

    [Fact]
    public void ReleaseScriptsDisableTrimmingAndDebugSymbols()
    {
        var windows = SourceContracts.Read("publish-win-x64.ps1");
        var linux = SourceContracts.Read("publish-linux-x64.sh");
        Assert.Contains("PublishTrimmed=false", windows);
        Assert.Contains("DebugSymbols=false", windows);
        Assert.Contains("PublishTrimmed=false", linux);
        Assert.Contains("DebugSymbols=false", linux);
    }

    [Fact]
    public void ReleaseScriptsExtractAndValidateTheirArchives()
    {
        var windows = SourceContracts.Read("publish-win-x64.ps1");
        var linux = SourceContracts.Read("publish-linux-x64.sh");
        Assert.Contains("Expand-Archive", windows);
        Assert.Contains("verification\\win-x64-archive", windows);
        Assert.Contains("tar -xzf", linux);
        Assert.Contains("verification/linux-x64-archive", linux);
    }

    [Fact]
    public void LinuxPackageRejectsWindowsExecutablesAndFfmpegDlls()
    {
        var script = SourceContracts.Read("publish-linux-x64.sh");
        Assert.Contains("NekoPlayer.exe", script);
        Assert.Contains("ffmpeg.exe", script);
        Assert.Contains("avcodec-*.dll", script);
    }

    [Fact]
    public void LinuxPackageScanAllowsSqliteRuntimeAssembliesButRejectsDatabaseFiles()
    {
        var script = SourceContracts.Read("publish-linux-x64.sh");
        Assert.DoesNotContain("*.sqlite*", script);
        Assert.Contains("*.sqlite3", script);
        Assert.Contains("*.db-wal", script);
        Assert.Contains("*.db-shm", script);
    }

    [Fact]
    public void WindowsPackageRejectsUserDataAndLocalPaths()
    {
        var script = SourceContracts.Read("publish-win-x64.ps1");
        Assert.Contains("'.db'", script);
        Assert.Contains("'.mp3'", script);
        Assert.Contains("Users${backslash}", script);
        Assert.Contains("codex\" + '_tmp", script);
        Assert.Contains("Avalonia.Diagnostics", script);
        Assert.Contains("$privateMatches.Count -gt 0", script);
        Assert.DoesNotContain("Select-String -Pattern $privatePattern -Quiet", script);
    }

    [Fact]
    public void PlaybackVerifierRequiresAnExplicitAudioDirectory()
    {
        var script = SourceContracts.Read("test-real-playback.ps1");
        Assert.Contains("[Parameter(Mandatory)]", script);
        Assert.DoesNotContain("$env:USERPROFILE", script);
    }

    [Fact]
    public void CiUsesExplicitHeadlessAudioAndDisplayBackends()
    {
        var workflow = SourceContracts.Read(@".github\workflows\ci.yml");
        Assert.Contains("-HeadlessNullOutput", workflow);
        Assert.Contains("xvfb-run -a ./publish-linux-x64.sh", workflow);
        Assert.Contains("openbox --sm-disable", workflow);
        Assert.Contains("xdotool search --onlyvisible --name \".*\"", workflow);
        Assert.DoesNotContain("xdotool search --name \"猫娘播放器\"", workflow);
        var verifier = SourceContracts.Read(@"tools\NekoPlayer.PlaybackVerifier\Program.cs");
        Assert.Contains("--headless-null-output", verifier);
        Assert.Contains("new LinuxFfmpegAudioPlayerService(locator)", verifier);
    }

    [Fact]
    public void FfmpegSetupPrefersAuthenticatedGithubCli()
    {
        var script = SourceContracts.Read("setup-ffmpeg.ps1");
        Assert.Contains("gh -ErrorAction SilentlyContinue", script);
        Assert.Contains("repos/BtbN/FFmpeg-Builds/releases/latest", script);
        Assert.Contains("falling back to the public GitHub API", script);
    }

    [Fact]
    public void WindowsLinuxPublisherEscapesBackslashesForWslpath()
    {
        var script = SourceContracts.Read("publish-linux-x64.ps1");
        Assert.Contains("$root.Replace('\\', '\\\\')", script);
        Assert.Contains("wsl.exe wslpath -a -- $escapedWindowsRoot", script);
        Assert.Contains("wsl.exe --cd $linuxRoot bash -lc $linuxCommand", script);
    }
}
