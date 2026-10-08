using System.Globalization;
using System.Text;

namespace NekoPlayer.OnlineVerifier;

internal static class ReportMarkdown
{
    public static string Render(OnlineVerificationReport report)
    {
        var text = new StringBuilder();
        text.AppendLine("# 在线能力与无声解码验证");
        text.AppendLine();
        text.AppendLine($"时间：{report.StartedAtUtc:O} — {report.FinishedAtUtc:O}");
        text.AppendLine();
        text.AppendLine("模式：显式触发的真实游客联网请求，与默认离线单元测试分开。使用独立临时数据目录，不读取账号凭据，不注册或登录。");
        text.AppendLine();
        text.AppendLine("HTTP 成功表示运行时收到可解析的成功响应；成功响应的精确状态码未由接口暴露。异常附带状态码时才记录数字。业务空结果、源错误、试听和权限限制分别记录。");
        text.AppendLine();
        text.AppendLine("媒体验证仅使用 ffprobe 和 FFmpeg 有界 PCM 管道；未打开音响，未调用播放器。平台声明 Full 或获得 URL 均不证明完整播放成功。Seek 证据仅限执行输入偏移后的解码，未验证应用播放状态或实际听感。");
        text.AppendLine();
        text.AppendLine($"网关启动：{report.Runtime.Status} / {report.Runtime.ErrorCategory ?? report.Runtime.Business} / {report.Runtime.ElapsedMilliseconds} ms");
        text.AppendLine($"网关关闭：{report.Shutdown.Status} / {report.Shutdown.ErrorCategory ?? report.Shutdown.Business} / {report.Shutdown.ElapsedMilliseconds} ms");
        text.AppendLine();
        text.AppendLine("| 平台 | 歌曲标识 | 能力 | 状态 | HTTP | 业务 / 错误分类 | 条数 | 耗时 ms |");
        text.AppendLine("| --- | --- | --- | --- | --- | --- | ---: | ---: |");
        foreach (var provider in report.Providers)
            foreach (var capability in provider.Capabilities)
                text.AppendLine($"| {provider.ProviderId} | {provider.ProviderTrackId ?? "—"} | {capability.Capability} | {capability.Status} | {capability.HttpStatusCode?.ToString(CultureInfo.InvariantCulture) ?? capability.Http} | {capability.ErrorCategory ?? capability.Business} | {capability.ResultCount} | {capability.ElapsedMilliseconds} |");
        text.AppendLine();
        text.AppendLine("首屏曲目权限逐项记录；首曲限制仍保留在上表。指定稳定标识若未出现在本次搜索中，明确记为 explicit-id。无声媒体优先选择声明 Full 的会话；这仍不证明整首播放。");
        text.AppendLine();
        text.AppendLine("| 平台 | 候选歌曲标识 | 来源 | 解析状态 | HTTP | 声明权限 | 业务 / 分类 | 耗时 ms |");
        text.AppendLine("| --- | --- | --- | --- | --- | --- | --- | ---: |");
        foreach (var provider in report.Providers)
            foreach (var candidate in provider.CandidateResolutions)
            {
                var resolution = candidate.Resolution;
                text.AppendLine($"| {provider.ProviderId} | {candidate.ProviderTrackId ?? "—"} | {candidate.Origin} | {resolution.Status} | {resolution.HttpStatusCode?.ToString(CultureInfo.InvariantCulture) ?? resolution.Http} | {resolution.ProviderDeclaredAvailability ?? "—"} | {resolution.ErrorCategory ?? resolution.Business} | {resolution.ElapsedMilliseconds} |");
            }
        text.AppendLine();
        text.AppendLine("| 平台 | 媒体歌曲标识 | 来源 | 无声媒体状态 | 探测 / 声明时长 s | 试听 | 片段 s | PCM 字节 | 末段 Seek 请求 s | Seek PCM 字节 | 偏移命令成功 | 分类 | 耗时 ms |");
        text.AppendLine("| --- | --- | --- | --- | ---: | --- | ---: | ---: | ---: | ---: | --- | --- | ---: |");
        foreach (var provider in report.Providers)
        {
            var media = provider.Media;
            text.AppendLine($"| {provider.ProviderId} | {provider.SelectedMediaTrackId ?? "—"} | {provider.MediaSelectionOrigin ?? "—"} | {media.Status} | {Number(media.ProbedDurationSeconds)} / {Number(media.ProviderDeclaredDurationSeconds)} | {media.IsPreview} | {Number(media.DecodedSegmentSeconds)} | {media.PcmBytes} | {Number(media.RequestedSeekSeconds)} | {media.SeekPcmBytes} | {media.SeekCommandSucceeded} | {media.ErrorCategory ?? "—"} | {media.ElapsedMilliseconds} |");
        }
        text.AppendLine();
        text.AppendLine("| 平台 | Probe 退出 / HTTP / 分类 | Decode 退出 / HTTP / 分类 | Seek 退出 / HTTP / 分类 |");
        text.AppendLine("| --- | --- | --- | --- |");
        foreach (var provider in report.Providers)
        {
            var media = provider.Media;
            text.AppendLine($"| {provider.ProviderId} | {Diagnostic(media.ProbeExitCode, media.ProbeHttpStatusCode, media.ProbeErrorCategory)} | {Diagnostic(media.DecodeExitCode, media.DecodeHttpStatusCode, media.DecodeErrorCategory)} | {Diagnostic(media.SeekExitCode, media.SeekHttpStatusCode, media.SeekErrorCategory)} |");
        }
        text.AppendLine();
        text.AppendLine("报告仅记录有限状态、平台与稳定标识、字段是否非空、计数和时长。原始 URL、签名、token、cookie、请求头、歌词文本及异常消息均不写入报告。");
        return text.ToString();
    }

    private static string Number(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "—";
    private static string Diagnostic(int? exitCode, int? http, string? category) =>
        $"{exitCode?.ToString(CultureInfo.InvariantCulture) ?? "—"} / {http?.ToString(CultureInfo.InvariantCulture) ?? "—"} / {category ?? "—"}";
}
