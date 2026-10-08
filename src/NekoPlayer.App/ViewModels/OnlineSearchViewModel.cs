using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NekoPlayer.Core.Interfaces;
using NekoPlayer.Core.Models;
using NekoPlayer.Core.Services;
using NekoPlayer.Infrastructure.Online;

namespace NekoPlayer.App.ViewModels;

public sealed record SearchProviderOption(string Id, string Name);
public sealed record SearchPlayRequest(Track Track, IReadOnlyList<Track> Context, bool Preview);
public enum ProviderSearchFailure { None, Authentication, RateLimited, Network, Provider, Protocol }

/// <summary>A source owns its collection, selection and paging state for the life of a query.</summary>
public sealed class ProviderSearchGroupViewModel : ObservableObject, IDisposable
{
    private bool _isLoading;
    private bool _isLoadingMore;
    private bool _hasMore;
    private bool _isCancelled;
    private bool _hasSearched;
    private string? _errorMessage;
    private string? _warningMessage;
    private ProviderSearchFailure _failureKind;
    private int _page;
    private Track? _selectedTrack;
    private double _scrollOffset;

    internal ProviderSearchGroupViewModel(string providerId, string name, long generation)
    {
        ProviderId = providerId;
        Name = name;
        Generation = generation;
        Tracks.CollectionChanged += (_, _) => NotifyState();
    }

    public string ProviderId { get; }
    public string Name { get; }
    public ObservableCollection<Track> Tracks { get; } = [];
    public ObservableCollection<Track> Results => Tracks;
    public bool IsLoading { get => _isLoading; internal set { if (SetProperty(ref _isLoading, value)) NotifyState(); } }
    public bool IsLoadingMore { get => _isLoadingMore; internal set => SetProperty(ref _isLoadingMore, value); }
    public bool HasMore { get => _hasMore; internal set { if (SetProperty(ref _hasMore, value)) NotifyState(); } }
    public bool IsCancelled { get => _isCancelled; internal set { if (SetProperty(ref _isCancelled, value)) NotifyState(); } }
    public bool HasSearched { get => _hasSearched; internal set { if (SetProperty(ref _hasSearched, value)) NotifyState(); } }
    public string? ErrorMessage { get => _errorMessage; internal set { if (SetProperty(ref _errorMessage, value)) NotifyState(); } }
    public string? WarningMessage { get => _warningMessage; internal set => SetProperty(ref _warningMessage, value); }
    public ProviderSearchFailure FailureKind { get => _failureKind; internal set { if (SetProperty(ref _failureKind, value)) NotifyState(); } }
    public bool IsError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool CanRetry => !IsLoading && (IsError || IsCancelled);
    public bool CanGoLogin => !IsLoading && FailureKind == ProviderSearchFailure.Authentication && ProviderId is "netease" or "qq" or "kugou";
    public bool CanFindOther => !IsLoading && (IsError || IsEmpty || IsCancelled);
    public string RetryText => FailureKind == ProviderSearchFailure.RateLimited ? "稍后重试此来源" : "重试此来源";
    public string ActionHint => FailureKind switch
    {
        ProviderSearchFailure.Authentication when CanGoLogin => "此来源要求登录或授权已失效。可以前往账号页，也可以查看其他来源。",
        ProviderSearchFailure.Authentication => "此来源要求认证，当前暂不支持扫码登录。可以稍后重试或查看其他来源。",
        ProviderSearchFailure.RateLimited => "此来源暂时限制请求频率，请稍后重试或查看其他来源。",
        ProviderSearchFailure.Network => "连接此来源失败。请检查网络后重试，或查看其他来源。",
        ProviderSearchFailure.Protocol or ProviderSearchFailure.Provider => "此来源暂时不可用。可以重试或查看其他来源。",
        _ => IsCancelled ? "加载已取消；可以继续加载此来源或查看其他来源。" : IsEmpty ? "可以换个关键词，或查看其他来源。" : string.Empty
    };
    public bool IsEmpty => HasSearched && Tracks.Count == 0 && !IsLoading && !IsError && !IsCancelled;
    public int Count => Tracks.Count;
    public int Page { get => _page; internal set => SetProperty(ref _page, value); }
    public Track? SelectedTrack { get => _selectedTrack; set => SetProperty(ref _selectedTrack, value); }
    public double ScrollOffset { get => _scrollOffset; set => SetProperty(ref _scrollOffset, Math.Max(0, value)); }
    public string StatusMessage => IsLoading ? (IsLoadingMore ? "正在加载更多…" : "正在搜索…")
        : IsError ? FailureKind switch
        {
            ProviderSearchFailure.Authentication => "需要登录或验证",
            ProviderSearchFailure.RateLimited => "请求过于频繁",
            ProviderSearchFailure.Network => "网络连接失败",
            _ => $"搜索失败：{ErrorMessage}"
        }
        : IsCancelled ? "已取消，可重试"
        : !HasSearched ? "等待搜索"
        : IsEmpty ? "未找到结果"
        : HasMore ? $"已找到 {Count} 首，可加载更多" : $"已找到 {Count} 首";
    public string TabLabel => $"{Name} · {Count}";

    internal long Generation { get; }
    internal int RequestVersion { get; set; }
    internal int FailedPage { get; set; } = 1;
    internal CancellationTokenSource? RequestCancellation { get; set; }
    internal HashSet<string> Identities { get; } = new(StringComparer.Ordinal);
    internal IReadOnlyList<Track>? LocalMatches { get; set; }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(IsError));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanGoLogin));
        OnPropertyChanged(nameof(CanFindOther));
        OnPropertyChanged(nameof(RetryText));
        OnPropertyChanged(nameof(ActionHint));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(TabLabel));
    }

    public void Dispose()
    {
        RequestCancellation?.Cancel();
        RequestCancellation?.Dispose();
        RequestCancellation = null;
    }
}

/// <summary>Search is read-only. Only explicit result actions are delegated to the application.</summary>
public sealed class OnlineSearchViewModel : ViewModelBase, IDisposable
{
    private const int PageSize = 30;
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan InputEpisodeGap = TimeSpan.FromSeconds(3);
    private static readonly SearchProviderOption[] Sources =
    [new("local", "本地"), new("netease", "网易云"), new("qq", "QQ 音乐"), new("kuwo", "酷我"), new("kugou", "酷狗"), new("qishui", "汽水")];
    private readonly IOnlineMusicService _online;
    private readonly IMusicLibraryService _library;
    private readonly ITrackCatalog _catalog;
    private readonly SynchronizationContext? _synchronizationContext;
    private CancellationTokenSource? _debounceCancellation;
    private CancellationTokenSource? _generationCancellation;
    private string _query = string.Empty;
    private string _selectedProviderId = "all";
    private string _activeQuery = string.Empty;
    private string? _excludedProviderId;
    private bool _searchOtherPlatformsOnly;
    private string? _findOtherHint;
    private ProviderSearchGroupViewModel? _selectedGroup;
    private long _generation;
    private long _requestedGeneration = -1;
    private Task _searchTask = Task.CompletedTask;
    private bool _compositionActive;
    private bool _suppressScheduling;
    private bool _disposed;
    private SearchHistoryEntry? _automaticHistoryEntry;
    private DateTime _lastQueryEditUtc;
    private bool _mutatingHistory;
    private bool _userSelectedGroup;
    private bool _settingAutomaticGroup;
    private long _historySuppressedGeneration = -1;

    public OnlineSearchViewModel(IOnlineMusicService online, IMusicLibraryService library, ITrackCatalog catalog)
    {
        _online = online;
        _library = library;
        _catalog = catalog;
        _synchronizationContext = SynchronizationContext.Current;
        ProviderOptions = [new("all", "全部来源"), .. Sources];
        SearchCommand = new AsyncRelayCommand(() => SearchNowAsync(), AsyncRelayCommandOptions.AllowConcurrentExecutions);
        ClearQueryCommand = new RelayCommand(() => Query = string.Empty);
        RetryGroupCommand = new AsyncRelayCommand<ProviderSearchGroupViewModel>(group => RetryGroupAsync(group), AsyncRelayCommandOptions.AllowConcurrentExecutions);
        LoadMoreCommand = new AsyncRelayCommand<ProviderSearchGroupViewModel>(group => LoadMoreAsync(group), AsyncRelayCommandOptions.AllowConcurrentExecutions);
        CancelGroupCommand = new RelayCommand<ProviderSearchGroupViewModel>(CancelGroup);
        GoLoginCommand = new RelayCommand<ProviderSearchGroupViewModel>(RequestLogin);
        FindOtherSourcesCommand = new AsyncRelayCommand<ProviderSearchGroupViewModel>(FindOtherSourcesAsync);
        PlayCommand = new RelayCommand<Track>(track => RequestPlay(track, false));
        PreviewCommand = new RelayCommand<Track>(track => RequestPlay(track, true));
        FavoriteCommand = new RelayCommand<Track>(track => { if (track is not null) FavoriteRequested?.Invoke(this, track); });
        QueueCommand = new RelayCommand<Track>(track => { if (track is not null) QueueRequested?.Invoke(this, track); });
        PlaylistCommand = new RelayCommand<Track>(track => { if (track is not null) PlaylistRequested?.Invoke(this, track); });
        FindOtherCommand = new AsyncRelayCommand<Track>(FindOtherVersionsAsync);
        UseHistoryCommand = new AsyncRelayCommand<SearchHistoryEntry>(UseHistoryAsync);
        RemoveHistoryCommand = new RelayCommand<SearchHistoryEntry>(RemoveHistory);
        ClearHistoryCommand = new RelayCommand(ClearHistory);
        History.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasHistory));
            if (!_mutatingHistory) HistoryChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    public string Query
    {
        get => _query;
        set
        {
            value ??= string.Empty;
            if (!SetProperty(ref _query, value)) return;
            var now = DateTime.UtcNow;
            if (now - _lastQueryEditUtc > InputEpisodeGap) _automaticHistoryEntry = null;
            _lastQueryEditUtc = now;
            _excludedProviderId = null;
            _searchOtherPlatformsOnly = false;
            FindOtherHint = null;
            if (TrackSearch.NormalizeQuery(value).Length == 0) _automaticHistoryEntry = null;
            InvalidateSearch();
            OnPropertyChanged(nameof(HasQuery));
            ScheduleSearch();
        }
    }

    public string SelectedProviderId
    {
        get => _selectedProviderId;
        set
        {
            value = ProviderOptions.Any(option => option.Id == value) ? value : "all";
            if (!SetProperty(ref _selectedProviderId, value)) return;
            _automaticHistoryEntry = null;
            _excludedProviderId = null;
            _searchOtherPlatformsOnly = false;
            FindOtherHint = null;
            InvalidateSearch();
            ScheduleSearch();
        }
    }

    public IReadOnlyList<SearchProviderOption> ProviderOptions { get; }
    public ObservableCollection<ProviderSearchGroupViewModel> Groups { get; } = [];
    public ObservableCollection<SearchHistoryEntry> History { get; } = [];
    public bool HasHistory => History.Count != 0;
    public bool HasQuery => TrackSearch.NormalizeQuery(Query).Length != 0;
    public bool IsSearching => Groups.Any(group => group.IsLoading);
    public bool IsCompositionActive => _compositionActive;
    public string? FindOtherHint { get => _findOtherHint; private set => SetProperty(ref _findOtherHint, value); }
    public string StatusMessage => !HasQuery ? "搜索歌名、歌手或专辑，结果按来源分别显示"
        : IsCompositionActive ? "完成输入后开始搜索"
        : IsSearching ? "正在搜索，各来源结果将分别显示…"
        : Groups.Count == 0 ? "准备搜索…"
        : $"共 {Groups.Sum(group => group.Count)} 首 · {Groups.Count(group => group.IsError)} 个来源失败";
    public string LiveStatusMessage => StatusMessage;
    public ProviderSearchGroupViewModel? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (value is not null && !Groups.Contains(value)) return;
            if (!SetProperty(ref _selectedGroup, value)) return;
            if (value is not null && !_settingAutomaticGroup) _userSelectedGroup = true;
            OnPropertyChanged(nameof(SelectedTrack));
        }
    }
    public Track? SelectedTrack
    {
        get => SelectedGroup?.SelectedTrack;
        set { if (SelectedGroup is not null) SelectedGroup.SelectedTrack = value; }
    }

    public IAsyncRelayCommand SearchCommand { get; }
    public IRelayCommand ClearQueryCommand { get; }
    public IAsyncRelayCommand<ProviderSearchGroupViewModel> RetryGroupCommand { get; }
    public IAsyncRelayCommand<ProviderSearchGroupViewModel> LoadMoreCommand { get; }
    public IRelayCommand<ProviderSearchGroupViewModel> CancelGroupCommand { get; }
    public IRelayCommand<ProviderSearchGroupViewModel> GoLoginCommand { get; }
    public IAsyncRelayCommand<ProviderSearchGroupViewModel> FindOtherSourcesCommand { get; }
    public IRelayCommand<Track> PlayCommand { get; }
    public IRelayCommand<Track> PreviewCommand { get; }
    public IRelayCommand<Track> FavoriteCommand { get; }
    public IRelayCommand<Track> QueueCommand { get; }
    public IRelayCommand<Track> PlaylistCommand { get; }
    public IAsyncRelayCommand<Track> FindOtherCommand { get; }
    public IAsyncRelayCommand<SearchHistoryEntry> UseHistoryCommand { get; }
    public IRelayCommand<SearchHistoryEntry> RemoveHistoryCommand { get; }
    public IRelayCommand ClearHistoryCommand { get; }

    public event EventHandler<SearchPlayRequest>? PlayRequested;
    public event EventHandler<Track>? FavoriteRequested;
    public event EventHandler<Track>? QueueRequested;
    public event EventHandler<Track>? PlaylistRequested;
    public event EventHandler? HistoryChanged;
    public event EventHandler<string>? LoginRequested;

    public void SetCompositionActive(bool active)
    {
        if (_disposed || _compositionActive == active) return;
        _compositionActive = active;
        OnPropertyChanged(nameof(IsCompositionActive));
        InvalidateSearch();
        if (!active) ScheduleSearch();
    }

    public Task SearchNowAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || _compositionActive || !HasQuery) return Task.CompletedTask;
        CancelDebounce();
        RecordHistory(TrackSearch.NormalizeQuery(Query), explicitSubmission: true);
        return StartSearchAsync(cancellationToken);
    }

    public Task RetryGroupAsync(ProviderSearchGroupViewModel? group, CancellationToken cancellationToken = default)
    {
        if (!IsCurrent(group) || group!.IsLoading) return Task.CompletedTask;
        return SearchGroupAsync(group, group.FailedPage, cancellationToken);
    }

    public Task LoadMoreAsync(ProviderSearchGroupViewModel? group, CancellationToken cancellationToken = default)
    {
        if (!IsCurrent(group) || group!.IsLoading || !group.HasMore || group.IsError || group.IsCancelled) return Task.CompletedTask;
        return SearchGroupAsync(group, group.Page + 1, cancellationToken);
    }

    public void CancelGroup(ProviderSearchGroupViewModel? group)
    {
        if (!IsCurrent(group) || !group!.IsLoading) return;
        group.RequestVersion++;
        group.RequestCancellation?.Cancel();
        group.IsLoading = false;
        group.IsLoadingMore = false;
        group.IsCancelled = true;
        group.FailureKind = ProviderSearchFailure.None;
        group.ErrorMessage = null;
    }

    private void RequestLogin(ProviderSearchGroupViewModel? group)
    {
        if (IsCurrent(group) && group!.CanGoLogin) LoginRequested?.Invoke(this, group.ProviderId);
    }

    public async Task FindOtherSourcesAsync(ProviderSearchGroupViewModel? group)
    {
        if (!IsCurrent(group) || !group!.CanFindOther) return;
        var excludedProviderId = group.ProviderId;
        _suppressScheduling = true;
        try
        {
            SelectedProviderId = "all";
            _excludedProviderId = excludedProviderId;
            _searchOtherPlatformsOnly = true;
            InvalidateSearch();
            FindOtherHint = "已按当前关键词查找其他来源；请确认歌曲、歌手与版本后手动播放。";
        }
        finally { _suppressScheduling = false; }
        await SearchNowAsync();
    }

    public void LoadHistory(IEnumerable<SearchHistoryEntry> entries)
    {
        _automaticHistoryEntry = null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var clean = entries.Where(entry => entry is not null)
            .OrderByDescending(entry => entry.SavedAt)
            .Select(entry => entry with { Query = TrackSearch.NormalizeQuery(entry.Query) })
            .Where(entry => entry.Query.Length > 0 && ProviderOptions.Any(option => option.Id == entry.ProviderId))
            .Where(entry => seen.Add(entry.ProviderId + "\n" + entry.Query)).Take(20).ToArray();
        MutateHistory(() =>
        {
            History.Clear();
            foreach (var entry in clean) History.Add(entry);
        });
    }

    public IReadOnlyList<SearchHistoryEntry> ExportHistory() => History.Take(20).ToArray();

    public void RemoveHistory(SearchHistoryEntry? entry)
    {
        if (entry is null) return;
        var existing = History.FirstOrDefault(item => item == entry);
        if (existing is null) return;
        if (_automaticHistoryEntry == existing) _automaticHistoryEntry = null;
        if (existing.ProviderId == SelectedProviderId &&
            existing.Query.Equals(TrackSearch.NormalizeQuery(Query), StringComparison.OrdinalIgnoreCase))
            _historySuppressedGeneration = _generation;
        MutateHistory(() => History.Remove(existing));
    }

    public void ApplyAvailability(Track canonical)
    {
        foreach (var track in Groups.SelectMany(group => group.Tracks).Where(track => track.Id == canonical.Id ||
                     track.IsOnline && canonical.IsOnline &&
                     !string.IsNullOrWhiteSpace(track.ProviderId) && !string.IsNullOrWhiteSpace(track.ProviderTrackId) &&
                     string.Equals(track.ProviderId, canonical.ProviderId, StringComparison.OrdinalIgnoreCase) &&
                     track.ProviderTrackId == canonical.ProviderTrackId))
        {
            track.Availability = canonical.Availability;
            track.RestrictionReason = canonical.RestrictionReason;
        }
    }

    public void ApplyFavoriteState(Guid trackId, bool favorite)
    {
        foreach (var track in Groups.SelectMany(group => group.Tracks).Where(track => track.Id == trackId)) track.IsFavorite = favorite;
    }

    public async Task RefreshFavoriteStatesAsync(CancellationToken cancellationToken = default)
    {
        var generation = _generation;
        var tracks = Groups.SelectMany(group => group.Tracks).ToArray();
        if (tracks.Length == 0) return;
        var canonical = await _catalog.GetTracksByIdsAsync(tracks.Select(track => track.Id).Distinct(), cancellationToken);
        await OnContextAsync(() =>
        {
            if (generation != _generation || _disposed) return;
            var favorites = canonical.GroupBy(track => track.Id).ToDictionary(items => items.Key, items => items.First().IsFavorite);
            foreach (var track in tracks)
                if (track.IsOnline || favorites.ContainsKey(track.Id)) track.IsFavorite = favorites.GetValueOrDefault(track.Id);
        });
    }

    public async Task FindOtherVersionsAsync(Track? track)
    {
        if (track is null || _disposed) return;
        var title = TrackSearch.NormalizeQuery(track.Title);
        var version = ExplicitVersionLabel(track);
        var terms = new List<string> { title, TrackSearch.NormalizeQuery(track.Artist) };
        if (version.Length > 0 && !title.Contains(version, StringComparison.OrdinalIgnoreCase)) terms.Add(version);
        _suppressScheduling = true;
        try
        {
            Query = TrackSearch.NormalizeQuery(string.Join(" ", terms.Where(term => term.Length > 0)));
            SelectedProviderId = "all";
            _excludedProviderId = track.ProviderId;
            _searchOtherPlatformsOnly = true;
            InvalidateSearch();
            FindOtherHint = "已在其他平台查找相近版本；请确认歌手与版本后手动播放。";
        }
        finally { _suppressScheduling = false; }
        await SearchNowAsync();
    }

    private async Task UseHistoryAsync(SearchHistoryEntry? entry)
    {
        if (entry is null || _disposed) return;
        _suppressScheduling = true;
        try { Query = entry.Query; SelectedProviderId = entry.ProviderId; }
        finally { _suppressScheduling = false; }
        await SearchNowAsync();
    }

    private void ClearHistory()
    {
        _automaticHistoryEntry = null;
        MutateHistory(History.Clear);
    }

    private void InvalidateSearch()
    {
        _generation++;
        CancelDebounce();
        _generationCancellation?.Cancel();
        _generationCancellation?.Dispose();
        _generationCancellation = new CancellationTokenSource();
        _requestedGeneration = -1;
        foreach (var group in Groups) { group.PropertyChanged -= OnGroupChanged; group.Dispose(); }
        Groups.Clear();
        SelectGroupAutomatically(null);
        _userSelectedGroup = false;
        _activeQuery = string.Empty;
        NotifySearchState();
    }

    private void ScheduleSearch()
    {
        if (_disposed || _suppressScheduling || _compositionActive || !HasQuery) return;
        var cancellation = new CancellationTokenSource();
        _debounceCancellation = cancellation;
        _ = DebounceAsync(_generation, cancellation.Token);
    }

    private async Task DebounceAsync(long generation, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(DebounceDelay, cancellationToken);
            if (generation != _generation || _compositionActive || _disposed) return;
            RecordHistory(TrackSearch.NormalizeQuery(Query), explicitSubmission: false);
            await StartSearchAsync(CancellationToken.None);
        }
        catch (OperationCanceledException) { }
    }

    private Task StartSearchAsync(CancellationToken cancellationToken)
    {
        if (_requestedGeneration == _generation) return _searchTask;
        if (cancellationToken.IsCancellationRequested) return Task.CompletedTask;
        _requestedGeneration = _generation;
        _activeQuery = TrackSearch.NormalizeQuery(Query);
        _generationCancellation ??= new CancellationTokenSource();
        foreach (var source in Sources.Where(source =>
                     (SelectedProviderId == "all" || source.Id == SelectedProviderId) &&
                     (!_searchOtherPlatformsOnly || source.Id != "local" && source.Id != _excludedProviderId)))
        {
            var group = new ProviderSearchGroupViewModel(source.Id, source.Name, _generation);
            group.PropertyChanged += OnGroupChanged;
            Groups.Add(group);
        }
        SelectGroupAutomatically(Groups.FirstOrDefault());
        _searchTask = Task.WhenAll(Groups.Select(group => SearchGroupAsync(group, 1, cancellationToken)).ToArray());
        NotifySearchState();
        return _searchTask;
    }

    private async Task SearchGroupAsync(ProviderSearchGroupViewModel group, int pageNumber, CancellationToken cancellationToken)
    {
        if (!IsCurrent(group) || group.IsLoading) return;
        var query = _activeQuery;
        var requestVersion = ++group.RequestVersion;
        group.RequestCancellation?.Dispose();
        var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(_generationCancellation!.Token, cancellationToken);
        group.RequestCancellation = requestCancellation;
        var token = requestCancellation.Token;
        group.FailedPage = pageNumber;
        group.ErrorMessage = null;
        group.FailureKind = ProviderSearchFailure.None;
        group.WarningMessage = null;
        group.IsCancelled = false;
        group.IsLoadingMore = pageNumber > 1;
        group.IsLoading = true;
        try
        {
            MusicSearchPage page;
            if (group.ProviderId == "local")
            {
                if (group.LocalMatches is null)
                {
                    var local = await _library.GetTracksAsync(token);
                    group.LocalMatches = local.Where(track => track.IsLocal && TrackSearch.Matches(track, query))
                        .OrderByDescending(track => TrackSearch.RelevanceScore(track, query)).ToArray();
                }
                var matches = group.LocalMatches;
                page = new MusicSearchPage("local", pageNumber, matches.Skip((pageNumber - 1) * PageSize).Take(PageSize).ToArray(), matches.Count > pageNumber * PageSize);
            }
            else
            {
                page = await _online.SearchAsync(new MusicSearchRequest(query, group.ProviderId, pageNumber, PageSize), token);
                foreach (var track in page.Tracks)
                {
                    track.SourceKind = TrackSourceKind.Online;
                    track.ProviderId = group.ProviderId;
                    if (!string.IsNullOrEmpty(track.ProviderTrackId)) track.Id = OnlineTrackIdentity.Create(group.ProviderId, track.ProviderTrackId);
                    if (string.IsNullOrWhiteSpace(track.VersionLabel)) track.VersionLabel = ExplicitVersionLabel(track);
                }
            }
            token.ThrowIfCancellationRequested();
            string? favoriteWarning = null;
            IReadOnlyList<Track> canonical = [];
            if (page.Tracks.Count > 0)
            {
                try { canonical = await _catalog.GetTracksByIdsAsync(page.Tracks.Select(track => track.Id).Distinct(), token); }
                catch (OperationCanceledException) { throw; }
                catch { favoriteWarning = "收藏状态暂未同步，可稍后重试。"; }
            }
            token.ThrowIfCancellationRequested();
            await OnContextAsync(() =>
            {
                if (!IsCurrentRequest(group, requestVersion)) return;
                var favorites = canonical.GroupBy(track => track.Id).ToDictionary(items => items.Key, items => items.First().IsFavorite);
                foreach (var track in page.Tracks)
                {
                    if (favoriteWarning is null && (track.IsOnline || favorites.ContainsKey(track.Id))) track.IsFavorite = favorites.GetValueOrDefault(track.Id);
                    var identity = group.ProviderId == "local" ? track.Id.ToString("N") : track.ProviderTrackId ?? track.Id.ToString("N");
                    if (group.Identities.Add(identity)) group.Tracks.Add(track);
                }
                group.Page = pageNumber;
                group.HasMore = page.HasMore;
                group.HasSearched = true;
                group.WarningMessage = string.Join(" ", new[] { page.Warning, favoriteWarning }.Where(message => !string.IsNullOrWhiteSpace(message)));
            });
        }
        catch (OperationCanceledException)
        {
            await OnContextAsync(() => { if (IsCurrentRequest(group, requestVersion)) group.IsCancelled = true; });
        }
        catch (Exception error)
        {
            await OnContextAsync(() =>
            {
                if (!IsCurrentRequest(group, requestVersion)) return;
                group.FailureKind = ClassifyFailure(error);
                group.ErrorMessage = string.IsNullOrWhiteSpace(error.Message) ? "来源暂时不可用" : error.Message;
                group.HasSearched = true;
            });
        }
        finally
        {
            await OnContextAsync(() =>
            {
                if (!IsCurrentRequest(group, requestVersion)) return;
                group.IsLoadingMore = false;
                group.IsLoading = false;
            });
        }
    }

    private bool IsCurrent(ProviderSearchGroupViewModel? group) => !_disposed && group is not null && group.Generation == _generation && Groups.Contains(group);
    private bool IsCurrentRequest(ProviderSearchGroupViewModel group, int version) => IsCurrent(group) && group.RequestVersion == version;

    private static ProviderSearchFailure ClassifyFailure(Exception error)
    {
        if (error is not GatewayException gatewayError)
            return error is HttpRequestException or IOException or TimeoutException ? ProviderSearchFailure.Network : ProviderSearchFailure.Provider;
        if (gatewayError.Code is "authentication_required" or "auth_expired" or "login_required" or "invalid_credential" || gatewayError.HttpStatusCode is 401 or 403)
            return ProviderSearchFailure.Authentication;
        if (gatewayError.Kind == GatewayFailureKind.RateLimited || gatewayError.Code is "rate_limited" or "gateway_busy")
            return ProviderSearchFailure.RateLimited;
        if (gatewayError.Kind is GatewayFailureKind.Timeout or GatewayFailureKind.Unavailable || gatewayError.Code is "network_error" or "tls_error" or "timeout" or "upstream_http_error")
            return ProviderSearchFailure.Network;
        return gatewayError.Kind == GatewayFailureKind.Protocol ? ProviderSearchFailure.Protocol : ProviderSearchFailure.Provider;
    }

    private void RequestPlay(Track? track, bool preview)
    {
        if (track is null || _disposed || (preview ? !track.CanPreview : !track.CanAttemptPlayback)) return;
        var group = Groups.FirstOrDefault(group => group.Tracks.Contains(track));
        var context = group?.Tracks.Where(item => item.CanAttemptPlayback).ToArray() ?? [track];
        if (preview) context = [track];
        PlayRequested?.Invoke(this, new SearchPlayRequest(track, context, preview));
    }

    private void RecordHistory(string query, bool explicitSubmission)
    {
        if (query.Length == 0) return;
        if (!explicitSubmission && _historySuppressedGeneration == _generation) return;
        if (explicitSubmission) _historySuppressedGeneration = -1;
        var entry = new SearchHistoryEntry(query, SelectedProviderId, DateTime.UtcNow);
        MutateHistory(() =>
        {
            if (!explicitSubmission && _automaticHistoryEntry is not null) History.Remove(_automaticHistoryEntry);
            foreach (var existing in History.Where(item => item.ProviderId == SelectedProviderId &&
                         item.Query.Equals(query, StringComparison.OrdinalIgnoreCase)).ToArray()) History.Remove(existing);
            History.Insert(0, entry);
            while (History.Count > 20) History.RemoveAt(History.Count - 1);
        });
        _automaticHistoryEntry = explicitSubmission ? null : entry;
    }

    private void MutateHistory(Action mutation)
    {
        _mutatingHistory = true;
        try { mutation(); }
        finally { _mutatingHistory = false; }
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CancelDebounce()
    {
        _debounceCancellation?.Cancel();
        _debounceCancellation?.Dispose();
        _debounceCancellation = null;
    }

    private void OnGroupChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (sender == SelectedGroup && args.PropertyName == nameof(ProviderSearchGroupViewModel.SelectedTrack)) OnPropertyChanged(nameof(SelectedTrack));
        if (args.PropertyName == nameof(ProviderSearchGroupViewModel.Count)) SelectFirstAvailableGroup();
        if (args.PropertyName is nameof(ProviderSearchGroupViewModel.IsLoading) or nameof(ProviderSearchGroupViewModel.Count) or nameof(ProviderSearchGroupViewModel.IsError)) NotifySearchState();
    }

    private void SelectFirstAvailableGroup()
    {
        if (_userSelectedGroup || SelectedGroup?.Count > 0) return;
        var available = Groups.FirstOrDefault(group => group.Count > 0);
        if (available is not null) SelectGroupAutomatically(available);
    }

    private void SelectGroupAutomatically(ProviderSearchGroupViewModel? group)
    {
        _settingAutomaticGroup = true;
        try { SelectedGroup = group; }
        finally { _settingAutomaticGroup = false; }
    }

    private void NotifySearchState()
    {
        OnPropertyChanged(nameof(IsSearching));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(LiveStatusMessage));
    }

    private Task OnContextAsync(Action action)
    {
        if (_synchronizationContext is null || SynchronizationContext.Current == _synchronizationContext)
        {
            action();
            return Task.CompletedTask;
        }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _synchronizationContext.Post(_ =>
        {
            try { action(); completion.SetResult(); }
            catch (Exception error) { completion.SetException(error); }
        }, null);
        return completion.Task;
    }

    internal static string ExplicitVersionLabel(Track track)
    {
        if (!string.IsNullOrWhiteSpace(track.VersionLabel)) return track.VersionLabel.Trim();
        const string marker = @"(?:\b(?:live|remaster(?:ed)?|cover|instrumental|remix|demo|acoustic)\b|现场|重制|翻唱|伴奏|混音|不插电)";
        foreach (Match bracket in Regex.Matches(track.Title, @"[\(（\[【]\s*([^\)）\]】]*)[\)）\]】]"))
            if (Regex.IsMatch(bracket.Groups[1].Value, marker, RegexOptions.IgnoreCase)) return bracket.Groups[1].Value.Trim();
        var suffix = Regex.Match(track.Title, @"\s[-–—]\s*(.+)$");
        return suffix.Success && Regex.IsMatch(suffix.Groups[1].Value, marker, RegexOptions.IgnoreCase)
            ? suffix.Groups[1].Value.Trim() : string.Empty;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _generation++;
        CancelDebounce();
        _generationCancellation?.Cancel();
        _generationCancellation?.Dispose();
        _generationCancellation = null;
        foreach (var group in Groups) { group.PropertyChanged -= OnGroupChanged; group.Dispose(); }
    }
}
