using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NekoPlayer.Core.Models;

namespace NekoPlayer.OnlineVerifier;

/// <summary>Decodes into a bounded pipe only. Does not construct a player or open an audio device.</summary>
internal static class SilentMediaVerifier
{
    private const int OutputSampleRate = 16000;
    private const int SegmentSeconds = 2;
    private const int MaximumPcmBytes = OutputSampleRate * 2 * SegmentSeconds;

    public static async Task<SilentMediaResult> VerifyAsync(AudioSource source, string ffmpegDirectory,
        int timeoutSeconds, CancellationToken cancellationToken)
    {
        var segmentSeconds = Math.Min(SegmentSeconds, source.Duration?.TotalSeconds ?? SegmentSeconds);
        var result = new SilentMediaResult
        {
            CanSeekDeclared = source.CanSeek, ProviderDeclaredDurationSeconds = source.Duration?.TotalSeconds,
            // PreviewStart is a lyric timeline offset. The resolved URL already contains the permitted clip.
            IsPreview = source.IsPreview, PreviewStartSeconds = source.IsPreview ? source.PreviewStart?.TotalSeconds : null,
            DecodedSegmentSeconds = segmentSeconds
        };
        var timer = Stopwatch.StartNew();
        var ffmpegPath = Path.Combine(ffmpegDirectory, "ffmpeg.exe");
        var ffprobePath = Path.Combine(ffmpegDirectory, "ffprobe.exe");
        try
        {
            if (!source.IsRemote || !Uri.TryCreate(source.Input, UriKind.Absolute, out var input) ||
                input.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(input.UserInfo))
            {
                result.Status = "skipped";
                result.ErrorCategory = "unsupported-session-source";
                return result;
            }
            if (!File.Exists(ffmpegPath) || !File.Exists(ffprobePath))
            {
                result.Status = "skipped";
                result.ErrorCategory = "ffmpeg-unavailable";
                return result;
            }

            var probeArguments = InputArguments(source, probe: true, offsetSeconds: null);
            probeArguments.AddRange(["-select_streams", "a:0", "-show_entries", "stream=codec_name,sample_rate,channels:format=duration", "-of", "json", source.Input]);
            var probe = await RunAsync(ffprobePath, probeArguments, pcm: false, timeoutSeconds, cancellationToken);
            result.ProbeExitCode = probe.ExitCode;
            result.ProbeHttpStatusCode = probe.HttpStatusCode;
            result.ProbeErrorCategory = probe.ErrorCategory;
            if (probe.ExitCode != 0)
            {
                result.Status = "failed";
                result.ErrorCategory = probe.ErrorCategory ?? "media-probe-rejected";
                return result;
            }
            using (var json = JsonDocument.Parse(probe.Text))
            {
                var data = json.RootElement;
                if (data.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var duration) &&
                    double.TryParse(duration.GetString(), CultureInfo.InvariantCulture, out var seconds) && double.IsFinite(seconds) && seconds > 0)
                    result.ProbedDurationSeconds = seconds;
                if (data.TryGetProperty("streams", out var streams) && streams.GetArrayLength() > 0)
                {
                    var audio = streams[0];
                    if (audio.TryGetProperty("codec_name", out var codec)) result.Codec = SafeReportValues.Codec(codec.GetString());
                    if (audio.TryGetProperty("sample_rate", out var rate) && int.TryParse(rate.GetString(), out var sampleRate)) result.SampleRate = sampleRate;
                    if (audio.TryGetProperty("channels", out var channels) && channels.TryGetInt32(out var count)) result.Channels = count;
                }
            }
            if (result.ProbedDurationSeconds is null || result.Channels is null or <= 0)
            {
                result.Status = "failed";
                result.ErrorCategory = "no-duration-or-audio-stream";
                return result;
            }

            if (!double.IsFinite(segmentSeconds) || segmentSeconds <= 0)
                throw new ArgumentException("Invalid session timing.");
            var decoded = await RunAsync(ffmpegPath, DecodeArguments(source, null, segmentSeconds),
                pcm: true, timeoutSeconds, cancellationToken);
            result.PcmBytes = decoded.PcmBytes;
            result.NonZeroPcmBytes = decoded.NonZeroPcmBytes;
            result.DecodeExitCode = decoded.ExitCode;
            result.DecodeHttpStatusCode = decoded.HttpStatusCode;
            result.DecodeErrorCategory = decoded.ErrorCategory;
            if (decoded.ExitCode != 0 || decoded.PcmBytes == 0)
            {
                result.Status = "failed";
                result.ErrorCategory = decoded.ErrorCategory ?? "media-decode-rejected";
                return result;
            }

            var permittedDuration = source.IsPreview
                ? Math.Min(source.Duration?.TotalSeconds ?? result.ProbedDurationSeconds.Value, result.ProbedDurationSeconds.Value)
                : result.ProbedDurationSeconds;
            if (source.CanSeek && permittedDuration > segmentSeconds + 1)
            {
                // Decode near the permitted segment's end. A small early seek cannot validate late access.
                var offset = Math.Max(1, permittedDuration.Value - segmentSeconds - 1);
                result.RequestedSeekSeconds = offset;
                var seek = await RunAsync(ffmpegPath, DecodeArguments(source, offset, segmentSeconds), pcm: true, timeoutSeconds, cancellationToken);
                result.SeekPcmBytes = seek.PcmBytes;
                result.SeekExitCode = seek.ExitCode;
                result.SeekHttpStatusCode = seek.HttpStatusCode;
                result.SeekErrorCategory = seek.ErrorCategory;
                result.SeekCommandSucceeded = seek.ExitCode == 0 && seek.PcmBytes > 0;
                result.Status = result.SeekCommandSucceeded ? "passed" : "partial";
                if (!result.SeekCommandSucceeded) result.ErrorCategory = seek.ErrorCategory ?? "seek-decode-rejected";
            }
            else
            {
                result.Status = "passed-without-seek";
            }
            return result;
        }
        catch (Exception error)
        {
            result.Status = "failed";
            result.ErrorCategory = SafeReportValues.Category(error);
            return result;
        }
        finally { result.ElapsedMilliseconds = timer.ElapsedMilliseconds; }
    }

    private static List<string> DecodeArguments(AudioSource source, double? offsetSeconds, double segmentSeconds)
    {
        var arguments = InputArguments(source, probe: false, offsetSeconds);
        arguments.AddRange(["-i", source.Input, "-map", "0:a:0", "-vn", "-sn", "-dn", "-t",
            segmentSeconds.ToString("0.###", CultureInfo.InvariantCulture), "-ac", "1", "-ar",
            OutputSampleRate.ToString(CultureInfo.InvariantCulture), "-f", "s16le", "pipe:1"]);
        return arguments;
    }

    private static List<string> InputArguments(AudioSource source, bool probe, double? offsetSeconds)
    {
        var arguments = new List<string> { "-hide_banner", "-loglevel", "error" };
        if (!probe) arguments.Add("-nostdin");
        arguments.AddRange(["-rw_timeout", "15000000"]);
        if (Uri.TryCreate(source.Input, UriKind.Absolute, out var input) && input.Scheme == "https")
            arguments.AddRange(["-tls_verify", "1"]);
        if (source.Headers is { Count: > 0 })
        {
            var headers = new StringBuilder();
            foreach (var pair in source.Headers)
            {
                if (pair.Key.Length is 0 or > 100 || pair.Key.Any(x => !char.IsAsciiLetterOrDigit(x) && x != '-') ||
                    pair.Value.Length > 8192 || pair.Value.Contains('\r') || pair.Value.Contains('\n'))
                    throw new ArgumentException("Invalid source header.");
                headers.Append(pair.Key).Append(": ").Append(pair.Value).Append("\r\n");
            }
            arguments.AddRange(["-headers", headers.ToString()]);
        }
        if (offsetSeconds.HasValue) arguments.AddRange(["-ss", offsetSeconds.Value.ToString("0.###", CultureInfo.InvariantCulture)]);
        return arguments;
    }

    private static async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        bool pcm, int timeoutSeconds, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        // Media hosts are fetched directly. Do not inherit the desktop's implicit proxy routing;
        // only this owned child receives this environment, while gateway API policy stays unchanged.
        var proxyVariables = new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY" };
        foreach (var name in process.StartInfo.Environment.Keys
                     .Where(name => proxyVariables.Contains(name, StringComparer.OrdinalIgnoreCase)).ToArray())
            process.StartInfo.Environment.Remove(name);
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) throw new IOException("Media process could not start.");
        process.StandardInput.Close();
        var errors = ReadDiagnosticsAsync(process.StandardError.BaseStream, timeout.Token);
        var output = pcm ? ReadPcmAsync(process.StandardOutput.BaseStream, timeout.Token)
            : ReadProbeAsync(process.StandardOutput.BaseStream, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var result = await output;
            var diagnostic = await errors;
            return result with
            {
                ExitCode = process.ExitCode,
                ErrorCategory = process.ExitCode == 0 ? null : diagnostic.Category,
                HttpStatusCode = diagnostic.HttpStatusCode
            };
        }
        finally
        {
            // Own only this child process and its descendants, never enumerate or kill user processes.
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(cleanup.Token); }
            catch (OperationCanceledException) { }
            catch (InvalidOperationException) { }
            // Observe pipe tasks on cancellation as well; none retains a background child or fault.
            try { await Task.WhenAll(output, errors); }
            catch (OperationCanceledException) { }
        }
    }

    private static async Task<ProcessResult> ReadProbeAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var length = await stream.ReadAsync(buffer, cancellationToken);
            if (length == 0) break;
            if (memory.Length + length > 65536) throw new IOException("Probe output exceeded its limit.");
            memory.Write(buffer, 0, length);
        }
        return new(0, Encoding.UTF8.GetString(memory.ToArray()), 0, 0);
    }

    private static async Task<ProcessResult> ReadPcmAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        long total = 0, nonzero = 0;
        while (true)
        {
            var length = await stream.ReadAsync(buffer, cancellationToken);
            if (length == 0) break;
            total += length;
            if (total > MaximumPcmBytes + 4096) throw new IOException("PCM output exceeded its limit.");
            for (var index = 0; index < length; index++) if (buffer[index] != 0) nonzero++;
        }
        return new(0, string.Empty, total, nonzero);
    }

    private static async Task<MediaDiagnostic> ReadDiagnosticsAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var bounded = new MemoryStream();
        int length;
        while ((length = await stream.ReadAsync(buffer, cancellationToken)) != 0)
            if (bounded.Length < 16384) bounded.Write(buffer, 0, (int)Math.Min(length, 16384 - bounded.Length));
        // Raw stderr exists only in bounded memory while this process runs. Return fixed categories and numeric HTTP status only.
        return ClassifyDiagnostic(Encoding.UTF8.GetString(bounded.ToArray()));
    }

    private static MediaDiagnostic ClassifyDiagnostic(string text)
    {
        var http = Regex.Match(text, @"(?:HTTP error |Server returned |HTTP/[12](?:\.\d)?\s+)([1-5]\d{2})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (http.Success && int.TryParse(http.Groups[1].Value, out var code)) return new("media-http-rejected", code);
        if (Has("Unrecognized option") || Has("Option") && Has("not found") || Has("Failed to set value") || Has("Error parsing options"))
            return new("ffmpeg-option-rejected", null);
        if (Has("Failed to resolve hostname") || Has("Name or service not known") || Has("getaddrinfo")) return new("media-dns-failure", null);
        if (Has("Connection timed out") || Has("Connection refused") || Has("Network is unreachable")) return new("media-connect-failure", null);
        if (Regex.IsMatch(text, @"\b(?:TLS|SSL)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            return new("media-tls-failure", null);
        if (Has("Invalid data found when processing input")) return new("invalid-media-data", null);
        if (Has("Protocol not found")) return new("media-protocol-unsupported", null);
        if (Has("Permission denied")) return new("media-resource-denied", null);
        if (Has("could not seek") || Has("seek failed")) return new("media-seek-rejected", null);
        if (Has("Invalid argument")) return new("invalid-media-argument", null);
        return new("media-process-rejected", null);
        bool Has(string pattern) => text.Contains(pattern, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record MediaDiagnostic(string Category, int? HttpStatusCode);
    private sealed record ProcessResult(int ExitCode, string Text, long PcmBytes, long NonZeroPcmBytes,
        string? ErrorCategory = null, int? HttpStatusCode = null);
}
