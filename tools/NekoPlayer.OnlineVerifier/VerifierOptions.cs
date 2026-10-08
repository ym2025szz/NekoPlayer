namespace NekoPlayer.OnlineVerifier;

internal sealed record VerifierOptions(bool Online, bool Media, string Query, string[] Providers,
    int CapabilityTimeoutSeconds, int SourceTimeoutSeconds, string? FfmpegDirectory,
    IReadOnlyDictionary<string, string> ExplicitTrackIds)
{
    public static readonly string[] AllProviders = ["netease", "qq", "kuwo", "kugou", "qishui"];

    public static VerifierOptions Parse(string[] args)
    {
        var online = false;
        var media = true;
        var query = "周杰伦";
        var providers = AllProviders;
        var capabilitySeconds = 25;
        var sourceSeconds = 110;
        string? ffmpegDirectory = null;
        var explicitTrackIds = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            string Value()
            {
                if (++index >= args.Length) throw new ArgumentException("An option value is missing.");
                return args[index];
            }

            switch (args[index])
            {
                case "--online": online = true; break;
                case "--no-media": media = false; break;
                case "--query": query = Value(); break;
                case "--providers":
                    providers = Value().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                        .Distinct(StringComparer.Ordinal).ToArray();
                    if (providers.Length == 0 || providers.Any(x => !AllProviders.Contains(x, StringComparer.Ordinal)))
                        throw new ArgumentException("Providers must be netease, qq, kuwo, kugou or qishui.");
                    break;
                case "--timeout-seconds": capabilitySeconds = ParseSeconds(Value(), 2, 60); break;
                case "--source-timeout-seconds": sourceSeconds = ParseSeconds(Value(), 5, 300); break;
                case "--ffmpeg-directory": ffmpegDirectory = Path.GetFullPath(Value()); break;
                case "--track-ids":
                    foreach (var selection in Value().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    {
                        var pair = selection.Split('=', 2, StringSplitOptions.TrimEntries);
                        if (pair.Length != 2 || !AllProviders.Contains(pair[0], StringComparer.Ordinal) || SafeReportValues.TrackId(pair[1]) is null)
                            throw new ArgumentException("Track selections require supported-provider=stable-id pairs.");
                        explicitTrackIds[pair[0]] = pair[1];
                    }
                    break;
                case "--help": break;
                default: throw new ArgumentException("Unknown option. Run with --help.");
            }
        }
        if (string.IsNullOrWhiteSpace(query) || query.Length > 100) throw new ArgumentException("Query must contain 1 to 100 characters.");
        return new(online, media, query, providers, capabilitySeconds, sourceSeconds, ffmpegDirectory, explicitTrackIds);
    }

    private static int ParseSeconds(string value, int minimum, int maximum) =>
        int.TryParse(value, out var seconds) && seconds >= minimum && seconds <= maximum
            ? seconds : throw new ArgumentException($"Timeout must be between {minimum} and {maximum} seconds.");
}
