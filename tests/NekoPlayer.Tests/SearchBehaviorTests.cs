using NekoPlayer.App.ViewModels;
using NekoPlayer.Core.Interfaces;
using NekoPlayer.Core.Models;
using NekoPlayer.Core.Services;
using NekoPlayer.Infrastructure.Online;

namespace NekoPlayer.Tests;

public sealed class SearchBehaviorTests
{
    [Theory]
    [InlineData("authentication_required", GatewayFailureKind.Provider, ProviderSearchFailure.Authentication, true)]
    [InlineData("rate_limited", GatewayFailureKind.RateLimited, ProviderSearchFailure.RateLimited, false)]
    [InlineData("network_error", GatewayFailureKind.Provider, ProviderSearchFailure.Network, false)]
    [InlineData("gateway_timeout", GatewayFailureKind.Timeout, ProviderSearchFailure.Network, false)]
    [InlineData("search_shape", GatewayFailureKind.Protocol, ProviderSearchFailure.Protocol, false)]
    public async Task SearchFailuresExposeTheirRecoveryActionsWithoutSwitchingSources(string code, GatewayFailureKind kind, ProviderSearchFailure expected, bool canLogin)
    {
        var online = new FakeOnline((_, _) => Task.FromException<MusicSearchPage>(new GatewayException(kind, code, "fixture failure")));
        using var search = Create(online);
        search.SelectedProviderId = "qq";
        search.Query = "song";
        await search.SearchNowAsync();
        var group = Assert.Single(search.Groups);
        Assert.Equal(expected, group.FailureKind);
        Assert.Equal(canLogin, group.CanGoLogin);
        Assert.True(group.CanRetry);
        Assert.True(group.CanFindOther);
        Assert.False(group.IsEmpty);
        Assert.False(group.IsCancelled);
        Assert.NotEmpty(group.ActionHint);
        Assert.Equal("qq", search.SelectedProviderId);
        Assert.Single(online.Requests);
    }

    [Fact]
    public async Task GoLoginOnlyRequestsNavigationAndFindOtherRequiresAnExplicitAction()
    {
        var online = new FakeOnline((request, _) => request.ProviderId == "qq"
            ? Task.FromException<MusicSearchPage>(new GatewayException(GatewayFailureKind.Provider, "authentication_required", "需要登录"))
            : Task.FromResult(Page(request, [])));
        using var search = Create(online);
        search.SelectedProviderId = "qq";
        search.Query = "song";
        await search.SearchNowAsync();
        var group = Assert.Single(search.Groups);
        var requested = new List<string>();
        search.LoginRequested += (_, provider) => requested.Add(provider);
        search.GoLoginCommand.Execute(group);
        Assert.Equal(new[] { "qq" }, requested);
        Assert.Same(group, search.SelectedGroup);
        Assert.Equal("qq", search.SelectedProviderId);
        Assert.Single(online.Requests);
        await search.FindOtherSourcesAsync(group);
        Assert.Equal(new[] { "netease", "kuwo", "kugou", "qishui" }, search.Groups.Select(item => item.ProviderId));
        Assert.All(search.Groups, item => { Assert.True(item.IsEmpty); Assert.True(item.CanFindOther); Assert.False(item.CanGoLogin); });
        Assert.Equal("song", search.Query);
    }

    [Fact]
    public async Task SlowOldQueryCannotReplaceNewResultsEvenWhenProviderIgnoresCancellation()
    {
        var slow = new TaskCompletionSource<MusicSearchPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var online = new FakeOnline((request, _) => request.Query == "A" ? slow.Task : Task.FromResult(Page(request, [OnlineTrack("B")])));
        using var search = Create(online);
        search.SelectedProviderId = "netease";
        search.Query = "A";
        var old = search.SearchNowAsync();
        search.Query = "B";
        await search.SearchNowAsync();
        slow.SetResult(new MusicSearchPage("netease", 1, [OnlineTrack("A")], false));
        await old;
        Assert.Equal("B", Assert.Single(Assert.Single(search.Groups).Tracks).Title);
        Assert.Equal(2, online.Requests.Count);
    }

    [Fact]
    public async Task EnterCancelsPendingDebounceAndDoesNotSubmitTwice()
    {
        var online = new FakeOnline();
        using var search = Create(online);
        search.SelectedProviderId = "qq";
        search.Query = "Hello";
        await search.SearchNowAsync();
        await search.SearchNowAsync();
        await Task.Delay(420);
        Assert.Single(online.Requests);
        Assert.Single(search.History);
    }

    [Fact]
    public async Task LongImeCompositionMakesNoRequestsAndEnterSubmitsOnlyCommittedText()
    {
        var online = new FakeOnline();
        using var search = Create(online);
        search.SelectedProviderId = "qq";
        search.SetCompositionActive(true);
        foreach (var text in new[] { "z", "zhou", "周", "周杰", "周杰伦" })
        {
            search.Query = text;
            await Task.Delay(80);
        }
        await Task.Delay(340);
        await search.SearchNowAsync();
        Assert.Empty(online.Requests);
        Assert.Empty(search.History);
        search.SetCompositionActive(false);
        await search.SearchNowAsync();
        await Task.Delay(360);
        Assert.Equal("周杰伦", Assert.Single(online.Requests).Query);
    }

    [Fact]
    public async Task ProviderSwitchAndClearInvalidateOutstandingResults()
    {
        var pending = new TaskCompletionSource<MusicSearchPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var online = new FakeOnline((request, _) => request.ProviderId == "netease" ? pending.Task : Task.FromResult(Page(request, [OnlineTrack("qq-result", "qq")])));
        using var search = Create(online);
        search.SelectedProviderId = "netease";
        search.Query = "song";
        var old = search.SearchNowAsync();
        search.SelectedProviderId = "qq";
        await search.SearchNowAsync();
        Assert.Equal("qq", Assert.Single(search.Groups).ProviderId);
        Assert.Equal("qq-result", Assert.Single(search.Groups[0].Tracks).Title);
        search.Query = " ";
        pending.SetResult(new MusicSearchPage("netease", 1, [OnlineTrack("late")], false));
        await old;
        Assert.Empty(search.Groups);
        Assert.False(search.HasQuery);
    }

    [Fact]
    public async Task AllSourcesAppearInFixedOrderAndCompleteIndependently()
    {
        var netease = new TaskCompletionSource<MusicSearchPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var qq = new TaskCompletionSource<MusicSearchPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var online = new FakeOnline((request, _) => request.ProviderId switch
        {
            "netease" => netease.Task,
            "qq" => qq.Task,
            "kuwo" => Task.FromException<MusicSearchPage>(new InvalidOperationException("来源故障")),
            _ => Task.FromResult(Page(request, []))
        });
        using var search = Create(online);
        search.Query = "song";
        var all = search.SearchNowAsync();
        Assert.Equal(new[] { "local", "netease", "qq", "kuwo", "kugou", "qishui" }, search.Groups.Select(group => group.ProviderId));
        qq.SetResult(new MusicSearchPage("qq", 1, [OnlineTrack("qq-fast", "qq")], false));
        await EventuallyAsync(() => search.Groups[2].Tracks.Count == 1);
        Assert.True(search.Groups[1].IsLoading);
        Assert.True(search.Groups[3].IsError);
        Assert.False(search.Groups[3].IsEmpty);
        netease.SetResult(new MusicSearchPage("netease", 1, [OnlineTrack("netease-late")], false));
        await all;
        Assert.Equal("qq-fast", search.Groups[2].Tracks[0].Title);
        Assert.False(search.IsSearching);
        Assert.All(online.Requests, request => Assert.Equal(30, request.PageSize));
    }

    [Fact]
    public async Task FirstAvailableSourceBecomesVisibleAndLaterResultsDoNotMoveItsSelectionOrScroll()
    {
        var netease = new TaskCompletionSource<MusicSearchPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var qq = new TaskCompletionSource<MusicSearchPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var online = new FakeOnline((request, _) => request.ProviderId switch
        {
            "netease" => netease.Task,
            "qq" => qq.Task,
            _ => Task.FromResult(Page(request, []))
        });
        using var search = Create(online);
        search.Query = "song";
        var pending = search.SearchNowAsync();
        Assert.Equal("local", search.SelectedGroup?.ProviderId);
        var fastTrack = OnlineTrack("fast", "qq");
        qq.SetResult(new MusicSearchPage("qq", 1, [fastTrack], false));
        await EventuallyAsync(() => search.SelectedGroup?.ProviderId == "qq");
        var selected = search.SelectedGroup!;
        selected.SelectedTrack = fastTrack;
        selected.ScrollOffset = 180;
        netease.SetResult(new MusicSearchPage("netease", 1, [OnlineTrack("late")], false));
        await pending;
        Assert.Same(selected, search.SelectedGroup);
        Assert.Same(fastTrack, search.SelectedTrack);
        Assert.Equal(180, selected.ScrollOffset);
        Assert.Equal(new[] { "local", "netease", "qq", "kuwo", "kugou", "qishui" }, search.Groups.Select(group => group.ProviderId));
    }

    [Fact]
    public async Task UserSelectedEmptySourceIsProtectedFromAutomaticResultSelection()
    {
        var netease = new TaskCompletionSource<MusicSearchPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var online = new FakeOnline((request, _) => request.ProviderId == "netease"
            ? netease.Task : Task.FromResult(Page(request, [])));
        using var search = Create(online);
        search.Query = "song";
        var pending = search.SearchNowAsync();
        var chosen = search.Groups.Single(group => group.ProviderId == "kuwo");
        search.SelectedGroup = chosen;
        chosen.ScrollOffset = 40;
        netease.SetResult(new MusicSearchPage("netease", 1, [OnlineTrack("available")], false));
        await pending;
        Assert.Same(chosen, search.SelectedGroup);
        Assert.Equal(40, chosen.ScrollOffset);
        Assert.Single(search.Groups.Single(group => group.ProviderId == "netease").Tracks);
    }

    [Fact]
    public async Task FailedPageRetriesSamePageAndPreservesSelectionScrollOrderAndFavorites()
    {
        var attempts = 0;
        var first = OnlineTrack("same title", "netease", "1");
        var second = OnlineTrack("same title", "netease", "2");
        var third = OnlineTrack("third", "netease", "3");
        var online = new FakeOnline((request, _) =>
        {
            if (request.Page == 1) return Task.FromResult(Page(request, [first, second], true));
            if (++attempts == 1) return Task.FromException<MusicSearchPage>(new InvalidOperationException("page failed"));
            return Task.FromResult(Page(request, [OnlineTrack("duplicate", "netease", "1"), third]));
        });
        var catalog = new ReadOnlyCatalog([new Track { Id = second.Id, IsFavorite = true }]);
        using var search = Create(online, catalog: catalog);
        search.SelectedProviderId = "netease";
        search.Query = "song";
        await search.SearchNowAsync();
        var group = Assert.Single(search.Groups);
        search.SelectedTrack = second;
        group.ScrollOffset = 275;
        await search.LoadMoreAsync(group);
        Assert.True(group.IsError);
        Assert.Equal(1, group.Page);
        Assert.Equal(2, group.Tracks.Count);
        Assert.Same(second, search.SelectedTrack);
        Assert.Equal(275, group.ScrollOffset);
        await search.RetryGroupAsync(group);
        Assert.False(group.IsError);
        Assert.Equal(2, group.Page);
        Assert.Equal(new[] { first, second, third }, group.Tracks);
        Assert.True(second.IsFavorite);
        Assert.Same(second, search.SelectedTrack);
        Assert.Equal(new[] { 1, 2, 2 }, online.Requests.Select(request => request.Page));
        Assert.Equal(0, catalog.Writes);
    }

    [Fact]
    public async Task CancelledSourceCanRetryWithoutAffectingOtherGroups()
    {
        var waiting = new TaskCompletionSource<MusicSearchPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempt = 0;
        var online = new FakeOnline((request, _) => request.ProviderId == "qq" && ++attempt == 1
            ? waiting.Task : Task.FromResult(Page(request, [OnlineTrack(request.ProviderId, request.ProviderId)])));
        using var search = Create(online);
        search.Query = "song";
        var firstSearch = search.SearchNowAsync();
        var group = search.Groups.Single(group => group.ProviderId == "qq");
        search.CancelGroup(group);
        Assert.True(group.IsCancelled);
        Assert.False(group.IsEmpty);
        await search.RetryGroupAsync(group);
        waiting.SetResult(new MusicSearchPage("qq", 1, [OnlineTrack("stale", "qq")], false));
        await firstSearch;
        Assert.Equal("qq", Assert.Single(group.Tracks).Title);
        Assert.False(group.IsCancelled);
        Assert.Single(search.Groups.Single(group => group.ProviderId == "netease").Tracks);
    }

    [Fact]
    public async Task LocalSearchUsesNormalizedCrossFieldAndWithRelevanceAndDoesNotWrite()
    {
        var exact = new Track { Title = "Hello Artist", Artist = "Someone", IsFavorite = true };
        var crossField = new Track { Title = "Hello", Artist = "Artist", Album = "Collection", IsFavorite = true };
        var missingArtist = new Track { Title = "Hello", Artist = "Someone" };
        var onlineTrack = OnlineTrack("Hello Artist");
        var library = new ReadOnlyLibrary([missingArtist, crossField, exact, onlineTrack]);
        var online = new FakeOnline();
        var catalog = new ReadOnlyCatalog();
        using var search = Create(online, library, catalog);
        search.SelectedProviderId = "local";
        search.Query = "　Ｈｅｌｌｏ　 ＡＲＴＩＳＴ\t ";
        await search.SearchNowAsync();
        Assert.Equal(new[] { exact, crossField }, Assert.Single(search.Groups).Tracks);
        Assert.True(exact.IsFavorite);
        Assert.True(crossField.IsFavorite);
        Assert.Empty(online.Requests);
        Assert.Equal("Hello ARTIST", Assert.Single(search.History).Query);
        Assert.Equal(0, library.Writes);
        Assert.Equal(0, catalog.Writes);
    }

    [Fact]
    public async Task AutomaticTypingReplacesEpisodeHistoryButExplicitQueriesRemainAndHistoryCapsAtTwenty()
    {
        var online = new FakeOnline();
        using var search = Create(online);
        search.SelectedProviderId = "qq";
        search.Query = "H";
        await EventuallyAsync(() => online.Requests.Count == 1);
        search.Query = "Hello";
        await EventuallyAsync(() => online.Requests.Count == 2);
        Assert.Equal("Hello", Assert.Single(search.History).Query);
        await search.SearchNowAsync();
        search.Query = "Next";
        await search.SearchNowAsync();
        Assert.Equal(new[] { "Next", "Hello" }, search.History.Select(entry => entry.Query));
        for (var index = 0; index < 24; index++)
        {
            search.Query = "query " + index;
            await search.SearchNowAsync();
        }
        Assert.Equal(20, search.History.Count);
        search.Query = " ｑｕｅｒｙ　２３ ";
        await search.SearchNowAsync();
        Assert.Equal(20, search.History.Count);
        Assert.Equal("query 23", search.History[0].Query);
        using var restored = Create(new FakeOnline());
        restored.LoadHistory(search.ExportHistory());
        Assert.Equal(search.ExportHistory(), restored.ExportHistory());
    }

    [Fact]
    public void ImportedHistoryNormalizesDeduplicatesAndRejectsUnknownProvidersAndBlanks()
    {
        using var search = Create(new FakeOnline());
        var now = DateTime.UtcNow;
        search.LoadHistory([
            new(" Hello ", "qq", now), new("Ｈｅｌｌｏ", "qq", now.AddMinutes(-1)),
            new("Hello", "netease", now.AddMinutes(-2)), new(" ", "qq", now), new("test", "bad", now)
        ]);
        Assert.Equal(2, search.History.Count);
        Assert.All(search.History, entry => Assert.Equal("Hello", entry.Query));
    }

    [Fact]
    public async Task SingleHistoryRemovalIsIdempotentPreservesOthersCurrentQueryAndProviderAndTwentyLimit()
    {
        using var search = Create(new FakeOnline());
        search.SelectedProviderId = "qq";
        search.Query = "current";
        await search.SearchNowAsync();
        var now = DateTime.UtcNow;
        search.LoadHistory(Enumerable.Range(0, 24).Select(index => new SearchHistoryEntry("saved " + index, "netease", now.AddMinutes(-index))));
        Assert.Equal(20, search.History.Count);
        var deleted = search.History[6];
        var expected = search.History.Where(entry => entry != deleted).ToArray();
        var historyChanges = 0;
        search.HistoryChanged += (_, _) => historyChanges++;
        search.RemoveHistoryCommand.Execute(deleted);
        search.RemoveHistoryCommand.Execute(deleted);
        Assert.Equal(1, historyChanges);
        Assert.Equal(expected, search.ExportHistory());
        Assert.Equal("current", search.Query);
        Assert.Equal("qq", search.SelectedProviderId);
        Assert.Equal(19, search.History.Count);
        search.Query = "another";
        await search.SearchNowAsync();
        Assert.Equal(20, search.History.Count);
        Assert.DoesNotContain(deleted, search.History);
    }

    [Fact]
    public async Task RemovingCurrentHistoryBeforeDebouncePreventsItsAutomaticReinsertion()
    {
        var online = new FakeOnline();
        using var search = Create(online);
        var saved = new SearchHistoryEntry("current", "qq", DateTime.UtcNow);
        search.LoadHistory([saved]);
        search.SelectedProviderId = "qq";
        search.Query = "current";
        var historyChanges = 0;
        search.HistoryChanged += (_, _) => historyChanges++;
        search.RemoveHistory(saved);
        await EventuallyAsync(() => online.Requests.Count == 1);
        Assert.Empty(search.History);
        Assert.Equal(1, historyChanges);
        await search.SearchNowAsync();
        Assert.Equal("current", Assert.Single(search.History).Query);
    }

    [Fact]
    public async Task RemovingAutomaticHistoryClearsItsEpisodeAndLeavesOtherHistoryIntact()
    {
        var online = new FakeOnline();
        using var search = Create(online);
        var preserved = new SearchHistoryEntry("previous", "qq", DateTime.UtcNow.AddMinutes(-1));
        search.LoadHistory([preserved]);
        search.SelectedProviderId = "qq";
        search.Query = "automatic";
        await EventuallyAsync(() => online.Requests.Count == 1);
        search.RemoveHistory(search.History[0]);
        await Task.Delay(360);
        Assert.Equal(preserved, Assert.Single(search.History));
        search.Query = "automatic extended";
        await EventuallyAsync(() => online.Requests.Count == 2);
        Assert.Equal(new[] { "automatic extended", "previous" }, search.History.Select(entry => entry.Query));
    }

    [Fact]
    public async Task CanonicalPlaybackResolutionRefreshesExistingRowAndEnablesExplicitPreview()
    {
        var row = OnlineTrack("unknown", "qq");
        var online = new FakeOnline((request, _) => Task.FromResult(Page(request, [row])));
        using var search = Create(online);
        search.SelectedProviderId = "qq";
        search.Query = "unknown";
        await search.SearchNowAsync();
        search.SelectedTrack = row;
        var changes = new List<string?>();
        row.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        search.ApplyAvailability(new Track
        {
            Id = Guid.NewGuid(), SourceKind = TrackSourceKind.Online, ProviderId = "QQ", ProviderTrackId = row.ProviderTrackId,
            Availability = MusicAvailability.Preview, RestrictionReason = "仅提供试听"
        });
        Assert.Same(row, Assert.Single(search.Groups[0].Tracks));
        Assert.Same(row, search.SelectedTrack);
        Assert.True(row.CanPreview);
        Assert.False(row.CanAttemptPlayback);
        Assert.Equal("仅提供试听", row.RestrictionReason);
        Assert.Contains(nameof(Track.CanPreview), changes);
    }

    [Fact]
    public async Task FindOtherVersionsUsesExplicitVersionAndOtherPlatformsWithoutPlayingOrWriting()
    {
        var online = new FakeOnline((request, _) => Task.FromResult(Page(request,
            [OnlineTrack("Song (Live)", request.ProviderId, "live"), OnlineTrack("Unmarked Song", request.ProviderId, "unmarked")])));
        var library = new ReadOnlyLibrary();
        var catalog = new ReadOnlyCatalog();
        using var search = Create(online, library, catalog);
        var played = 0;
        search.PlayRequested += (_, _) => played++;
        await search.FindOtherVersionsAsync(new Track { Title = "Song", Artist = "Singer", VersionLabel = "Live", ProviderId = "qq", SourceKind = TrackSourceKind.Online });
        Assert.Equal("Song Singer Live", search.Query);
        Assert.Equal(new[] { "netease", "kuwo", "kugou", "qishui" }, search.Groups.Select(group => group.ProviderId));
        Assert.All(search.Groups, group =>
        {
            Assert.Equal("Live", group.Tracks[0].VersionLabel);
            Assert.Equal(string.Empty, group.Tracks[1].VersionLabel);
        });
        Assert.Equal(0, played);
        Assert.Equal(0, catalog.Writes);
        Assert.Equal(0, library.Writes);
    }

    [Fact]
    public async Task DisposedSearchDoesNotAcceptLateResults()
    {
        var delayed = new TaskCompletionSource<MusicSearchPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var online = new FakeOnline((_, _) => delayed.Task);
        var search = Create(online);
        search.SelectedProviderId = "qq";
        search.Query = "song";
        var pending = search.SearchNowAsync();
        var group = Assert.Single(search.Groups);
        search.Dispose();
        delayed.SetResult(new MusicSearchPage("qq", 1, [OnlineTrack("late", "qq")], false));
        await pending;
        Assert.Empty(group.Tracks);
    }

    [Theory]
    [InlineData("Hello artist", true)]
    [InlineData("ＡＲＴＩＳＴ　ＡＬＢＵＭ", true)]
    [InlineData("hello missing", false)]
    [InlineData("\t　 ", true)]
    public void MultiWordSearchMatchesAcrossFields(string query, bool expected) =>
        Assert.Equal(expected, TrackSearch.Matches(new Track { Title = "Hello", Artist = "Artist", Album = "Album" }, query));

    private static OnlineSearchViewModel Create(FakeOnline online, ReadOnlyLibrary? library = null, ReadOnlyCatalog? catalog = null) =>
        new(online, library ?? new ReadOnlyLibrary(), catalog ?? new ReadOnlyCatalog());
    private static Track OnlineTrack(string title, string provider = "netease", string? id = null) => new()
    {
        Id = OnlineTrackIdentity.Create(provider, id ?? title), SourceKind = TrackSourceKind.Online,
        ProviderId = provider, ProviderTrackId = id ?? title, Title = title, Artist = "Singer"
    };
    private static MusicSearchPage Page(MusicSearchRequest request, IReadOnlyList<Track> tracks, bool hasMore = false) =>
        new(request.ProviderId, request.Page, tracks, hasMore);
    private static async Task EventuallyAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!predicate() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(predicate(), "Search did not reach the expected state.");
    }

    private sealed class FakeOnline(Func<MusicSearchRequest, CancellationToken, Task<MusicSearchPage>>? handler = null) : IOnlineMusicService
    {
        public List<MusicSearchRequest> Requests { get; } = [];
        public IReadOnlyList<MusicProviderInfo> Providers { get; } = [];
        public Task<MusicSearchPage> SearchAsync(MusicSearchRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return handler?.Invoke(request, cancellationToken) ?? Task.FromResult(Page(request, []));
        }
        public Task<PlaybackResolution> ResolveAsync(Track track, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<LyricsLine>> GetLyricsAsync(Track track, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ReadOnlyCatalog(IReadOnlyList<Track>? tracks = null) : ITrackCatalog
    {
        public int Writes { get; private set; }
        public Task<IReadOnlyList<Track>> GetTracksByIdsAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken = default)
        {
            var requested = ids.ToHashSet();
            return Task.FromResult<IReadOnlyList<Track>>((tracks ?? []).Where(track => requested.Contains(track.Id)).ToArray());
        }
        public Task<Track> EnsureOnlineTrackAsync(Track track, CancellationToken cancellationToken = default) { Writes++; throw new InvalidOperationException("Search must not write."); }
        public Task<IReadOnlyList<Track>> EnsureOnlineTracksAsync(IEnumerable<Track> tracks, CancellationToken cancellationToken = default) { Writes++; throw new InvalidOperationException("Search must not write."); }
    }

    private sealed class ReadOnlyLibrary(IReadOnlyList<Track>? tracks = null) : IMusicLibraryService
    {
        public int Writes { get; private set; }
        public Task<IReadOnlyList<Track>> GetTracksAsync(CancellationToken cancellationToken = default) => Task.FromResult(tracks ?? (IReadOnlyList<Track>)[]);
        private InvalidOperationException WriteRejected() { Writes++; return new InvalidOperationException("Search must not write."); }
        public Task<IReadOnlyList<Track>> GetFavoritesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<RecentTrack>> GetRecentAsync(int count = 100, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImportResult> ImportAsync(IEnumerable<string> files, IProgress<ImportProgress>? progress = null, CancellationToken cancellationToken = default) => throw WriteRejected();
        public Task<ImportResult> ScanFolderAsync(string folder, bool recursive, IProgress<ImportProgress>? progress = null, CancellationToken cancellationToken = default) => throw WriteRejected();
        public Task SetFavoriteAsync(Guid trackId, bool favorite, CancellationToken cancellationToken = default) => throw WriteRejected();
        public Task<RecentRemoval?> RemoveRecentAsync(Guid historyId, CancellationToken cancellationToken = default) => throw WriteRejected();
        public Task RestoreRecentAsync(RecentRemoval removal, CancellationToken cancellationToken = default) => throw WriteRejected();
        public Task<int> ClearRecentAsync(CancellationToken cancellationToken = default) => throw WriteRejected();
        public Task RemoveFromLibraryAsync(Guid trackId, CancellationToken cancellationToken = default) => throw WriteRejected();
        public Task<LibraryRemovalResult> RemoveFromLibraryAsync(IEnumerable<Guid> trackIds, CancellationToken cancellationToken = default) => throw WriteRejected();
        public Task RecordPlaybackAsync(Guid trackId, TimeSpan position, CancellationToken cancellationToken = default) => throw WriteRejected();
        public Task InitializeAsync(CancellationToken cancellationToken = default) => throw WriteRejected();
    }
}
