using System.Net;
using System.Text.RegularExpressions;
using NekoPlayer.Infrastructure.Online;

namespace NekoPlayer.OnlineVerifier;

/// <summary>Deliberately excludes raw responses, messages, input URLs, headers and credential fields.</summary>
internal sealed class OnlineVerificationReport
{
    public DateTimeOffset StartedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset FinishedAtUtc { get; set; }
    public string Mode { get; set; } = "read-only-online";
    public bool AudioDeviceUsed { get; set; }
    public bool FullPlaybackVerified { get; set; }
    public int QueryLength { get; set; }
    public CapabilityResult Runtime { get; set; } = new("startup");
    public CapabilityResult Shutdown { get; set; } = new("shutdown");
    public List<ProviderResult> Providers { get; } = [];
}

internal sealed class ProviderResult(string providerId)
{
    public string ProviderId { get; } = providerId;
    public bool Experimental { get; set; }
    public string? ProviderTrackId { get; set; }
    public string? SelectedMediaTrackId { get; set; }
    public string? MediaSelectionOrigin { get; set; }
    public List<CapabilityResult> Capabilities { get; } = [];
    public List<CandidateResolutionResult> CandidateResolutions { get; } = [];
    public SilentMediaResult Media { get; set; } = new();
}

internal sealed class CandidateResolutionResult(string? providerTrackId, string origin)
{
    public string? ProviderTrackId { get; } = providerTrackId;
    public string Origin { get; } = origin;
    public CapabilityResult Resolution { get; } = new("resolve");
}

internal sealed class CapabilityResult(string capability)
{
    public string Capability { get; } = capability;
    public string Status { get; set; } = "not-run";
    public string Http { get; set; } = "not-attempted";
    public int? HttpStatusCode { get; set; }
    public string Business { get; set; } = "not-evaluated";
    public string? ErrorCategory { get; set; }
    public long ElapsedMilliseconds { get; set; }
    public int ResultCount { get; set; }
    public Dictionary<string, bool> NonEmptyFields { get; } = new();
    public string? ProviderDeclaredAvailability { get; set; }
    public bool? HasSessionSource { get; set; }
    public bool? CanSeekDeclared { get; set; }
}

internal sealed class SilentMediaResult
{
    public string Mode { get; } = "ffprobe-and-bounded-pcm-pipe";
    public bool AudioDeviceUsed { get; } = false;
    public bool FullPlaybackVerified { get; } = false;
    public string Status { get; set; } = "not-run";
    public string? ErrorCategory { get; set; }
    public long ElapsedMilliseconds { get; set; }
    public double? ProbedDurationSeconds { get; set; }
    public double? ProviderDeclaredDurationSeconds { get; set; }
    public bool IsPreview { get; set; }
    public double? PreviewStartSeconds { get; set; }
    public double DecodedSegmentSeconds { get; set; }
    public int? SampleRate { get; set; }
    public int? Channels { get; set; }
    public string? Codec { get; set; }
    public long PcmBytes { get; set; }
    public long NonZeroPcmBytes { get; set; }
    public int? ProbeExitCode { get; set; }
    public int? ProbeHttpStatusCode { get; set; }
    public string? ProbeErrorCategory { get; set; }
    public int? DecodeExitCode { get; set; }
    public int? DecodeHttpStatusCode { get; set; }
    public string? DecodeErrorCategory { get; set; }
    public double? RequestedSeekSeconds { get; set; }
    public long SeekPcmBytes { get; set; }
    public bool SeekCommandSucceeded { get; set; }
    public int? SeekExitCode { get; set; }
    public int? SeekHttpStatusCode { get; set; }
    public string? SeekErrorCategory { get; set; }
    public string SeekEvidence { get; } = "ffmpeg input-offset decode; application transport and audible playback are not exercised";
    public bool? CanSeekDeclared { get; set; }
}

internal static partial class SafeReportValues
{
    // Stable identifiers are useful evidence. Do not copy arbitrary provider strings or messages.
    public static string? TrackId(string? value) => value is not null && StableId().IsMatch(value) ? value : null;
    public static string? Codec(string? value) => value is not null && CodecName().IsMatch(value) ? value : null;

    public static string Category(Exception error) => error switch
    {
        GatewayException { Kind: GatewayFailureKind.Unavailable } => "runtime-unavailable",
        GatewayException { Kind: GatewayFailureKind.Timeout } => "timeout",
        GatewayException { Kind: GatewayFailureKind.RateLimited } => "rate-limited",
        GatewayException { Kind: GatewayFailureKind.Protocol } => "gateway-protocol",
        GatewayException { Kind: GatewayFailureKind.Provider, Code: "authentication_required" } => "guest-auth-required",
        GatewayException { Kind: GatewayFailureKind.Provider, Code: "unsupported_guest_operation" } => "guest-operation-unsupported",
        GatewayException { Kind: GatewayFailureKind.Provider } => "provider-error",
        OperationCanceledException => "cancelled-or-timeout",
        TimeoutException => "timeout",
        HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } => "rate-limited",
        HttpRequestException => "http-transport",
        System.Text.Json.JsonException => "invalid-json",
        System.ComponentModel.Win32Exception => "executable-unavailable",
        IOException => "io-error",
        ArgumentException => "invalid-input",
        _ => "unexpected-error"
    };

    [GeneratedRegex("^[A-Za-z0-9_-]{1,160}$", RegexOptions.CultureInvariant)]
    private static partial Regex StableId();

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,40}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodecName();
}
