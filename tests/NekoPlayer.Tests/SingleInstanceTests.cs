using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using NekoPlayer.App.Services;

namespace NekoPlayer.Tests;

public sealed class SingleInstanceTests
{
    private static string Profile() => Path.Combine(Path.GetTempPath(), "NekoPlayerInstances", Guid.NewGuid().ToString("N"));

    [Fact]
    public void IdentityNormalizesPathsAndSeparatesUsersAndProfiles()
    {
        var profile = Profile();
        var key = SingleInstanceCoordinator.BuildInstanceKey(profile, "user-a");
        Assert.Equal(key, SingleInstanceCoordinator.BuildInstanceKey(Path.Combine(profile, ".") + Path.DirectorySeparatorChar, "user-a"));
        if (OperatingSystem.IsWindows())
            Assert.Equal(key, SingleInstanceCoordinator.BuildInstanceKey(profile.ToUpperInvariant().Replace('\\', '/'), "user-a"));
        Assert.NotEqual(key, SingleInstanceCoordinator.BuildInstanceKey(profile, "user-b"));
        Assert.NotEqual(key, SingleInstanceCoordinator.BuildInstanceKey(Profile(), "user-a"));
    }

    [Fact]
    public void SameProfileHasOneOwnerAndDifferentProfilesRemainIndependent()
    {
        var profile = Profile();
        using var first = new SingleInstanceCoordinator(profile);
        using var second = new SingleInstanceCoordinator(profile);
        using var other = new SingleInstanceCoordinator(Profile());
        Assert.True(first.IsPrimary);
        Assert.False(second.IsPrimary);
        Assert.True(other.IsPrimary);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ACTIVATE")]
    [InlineData("activate file.mp3")]
    [InlineData("shutdown")]
    [InlineData("shutdown-for-update C:\\anything.exe")]
    public void OnlyFixedCommandsAreAccepted(string? command) => Assert.False(SingleInstanceCoordinator.TryParseCommand(command, out _));

    [Fact]
    public async Task SecondInstanceActivatesOwnerThroughCurrentUserPipe()
    {
        var profile = Profile();
        using var owner = new SingleInstanceCoordinator(profile);
        using var client = new SingleInstanceCoordinator(profile);
        InstanceCommand? received = null;
        owner.StartServer(command => { received = command; return Task.FromResult(true); });
        Assert.Equal(InstanceExitCode.Success, await client.ActivateExistingAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(InstanceCommand.Activate, received);
    }

    [Fact]
    public async Task UpdateExitWaitsForOwnerCleanupRatherThanCommandAcknowledgement()
    {
        var profile = Profile();
        using var owner = new SingleInstanceCoordinator(profile);
        using var client = new SingleInstanceCoordinator(profile);
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.StartServer(command =>
        {
            Assert.Equal(InstanceCommand.ShutdownForUpdate, command);
            requested.TrySetResult();
            return Task.FromResult(true);
        });
        // Use the production update deadline; this test verifies cleanup ordering, not scheduler speed.
        var updateExit = client.ExitExistingForUpdateAsync(TimeSpan.FromSeconds(30));
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(updateExit.IsCompleted);
        owner.Dispose();
        Assert.Equal(InstanceExitCode.Success, await updateExit);
    }

    [Fact]
    public async Task UpdateExitReturnsTimeoutWhenOwnerDoesNotFinishCleanup()
    {
        var profile = Profile();
        using var owner = new SingleInstanceCoordinator(profile);
        using var client = new SingleInstanceCoordinator(profile);
        owner.StartServer(_ => Task.FromResult(true));
        Assert.Equal(InstanceExitCode.Timeout, await client.ExitExistingForUpdateAsync(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task NoExistingInstanceExitForUpdateSucceedsWithoutStartingServer()
    {
        using var instance = new SingleInstanceCoordinator(Profile());
        Assert.True(instance.IsPrimary);
        Assert.Equal(InstanceExitCode.Success, await instance.ExitExistingForUpdateAsync(TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public async Task ExitForUpdateCommandDoesNotInitializeGuiOrCreateUserData()
    {
        var profile = Profile();
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(typeof(NekoPlayer.App.Program).Assembly.Location);
        start.ArgumentList.Add("--exit-for-update");
        start.Environment["NEKOPLAYER_DATA_ROOT"] = profile;
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((int)InstanceExitCode.Success, process.ExitCode);
        Assert.False(Directory.Exists(profile));
    }

    [Fact]
    public void ExitForUpdateRejectsExtraArgumentsBeforeOpeningProfile()
    {
        Assert.Equal((int)InstanceExitCode.InvalidArguments,
            NekoPlayer.App.Program.Main(["--exit-for-update", "anything"]));
    }

    [Fact]
    public async Task UpdateExitObservesAlreadyClosingOwnerWithoutWaitingForPipeTimeout()
    {
        var profile = Profile();
        using var owner = new SingleInstanceCoordinator(profile);
        using var client = new SingleInstanceCoordinator(profile);
        var exit = client.ExitExistingForUpdateAsync(TimeSpan.FromSeconds(3));
        owner.Dispose();
        Assert.Equal(InstanceExitCode.Success, await exit.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task UnknownAndOversizeCommandsNeverReachHandlerAndServerStaysUsable()
    {
        var profile = Profile();
        using var owner = new SingleInstanceCoordinator(profile);
        using var client = new SingleInstanceCoordinator(profile);
        var calls = 0;
        owner.StartServer(_ => { calls++; return Task.FromResult(true); });
        foreach (var command in new[] { "run C:\\evil.exe", "act\rivate", new string('a', 65) })
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var pipe = new NamedPipeClientStream(".", owner.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(timeout.Token);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 128, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 128, leaveOpen: true);
            await writer.WriteLineAsync(command.AsMemory(), timeout.Token);
            Assert.Equal("rejected", await reader.ReadLineAsync(timeout.Token));
        }
        Assert.Equal(0, calls);
        Assert.Equal(InstanceExitCode.Success, await client.ActivateExistingAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(1, calls);
    }
}
