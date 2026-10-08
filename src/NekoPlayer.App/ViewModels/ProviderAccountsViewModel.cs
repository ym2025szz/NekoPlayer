using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NekoPlayer.Core.Interfaces;
using NekoPlayer.Core.Models;
using NekoPlayer.Infrastructure.Online;

namespace NekoPlayer.App.ViewModels;

public partial class ProviderAccountRow(ProviderAccount account) : ObservableObject
{
    [ObservableProperty] private ProviderAccount account = account;
    [ObservableProperty] private bool isFocused;
    public string StatusText => Account.LoggedIn ? Account.DisplayName : "未登录 · 游客模式";
    public bool CanLogin => Account.CanLogin;
    public bool CanLogout => Account.LoggedIn;
    public string LoginText => Account.LoggedIn ? "重新授权" : "扫码登录";
    partial void OnAccountChanged(ProviderAccount value)
    {
        OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(CanLogin));
        OnPropertyChanged(nameof(CanLogout)); OnPropertyChanged(nameof(LoginText));
    }
}

public partial class ProviderAccountsViewModel(IProviderAccountService service) : ViewModelBase, IDisposable
{
    private CancellationTokenSource? _loginCancellation;
    private readonly CancellationTokenSource _lifetime = new();
    private string? _loginId;
    private string? _providerId;
    private long _generation;
    private bool _disposed;
    public ObservableCollection<ProviderAccountRow> Rows { get; } = [];
    public event EventHandler<string>? AccountChanged;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isLoginVisible;
    [ObservableProperty] private string message = "可继续游客搜索；扫码登录后使用账号的实际播放权限。";
    [ObservableProperty] private string loginTitle = "扫码授权";
    [ObservableProperty] private string loginMessage = "正在获取二维码……";
    [ObservableProperty] private Bitmap? qrImage;
    [ObservableProperty] private bool canRefreshQr;
    [ObservableProperty] private string? selectedProviderId;
    [ObservableProperty] private ProviderAccountRow? selectedRow;
    public string AccountSummary => $"账号与授权 · 已登录 {Rows.Count(x => x.Account.LoggedIn)} 个平台";

    /// <summary>Selects an account row for navigation without starting or submitting login.</summary>
    public void FocusProvider(string providerId)
    {
        if (_disposed) return;
        SelectedProviderId = providerId.Trim().ToLowerInvariant();
        SelectedRow = Rows.FirstOrDefault(row => row.Account.ProviderId == SelectedProviderId);
        foreach (var row in Rows) row.IsFocused = row.Account.ProviderId == SelectedProviderId;
        var name = SelectedRow?.Account.Name ?? (SelectedProviderId switch { "netease" => "网易云", "qq" => "QQ 音乐", "kugou" => "酷狗", _ => SelectedProviderId });
        Message = $"已定位{name}账号；点击扫码登录后开始授权。";
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_disposed || IsBusy) return;
        IsBusy = true;
        try
        {
            var accounts = await service.GetAccountsAsync(_lifetime.Token);
            if (_disposed) return;
            foreach (var account in accounts)
            {
                var row = Rows.FirstOrDefault(x => x.Account.ProviderId == account.ProviderId);
                if (row is null) Rows.Add(new(account)); else row.Account = account;
            }
            Message = "登录凭证使用 Windows 加密并保存在本机；扫码后请在手机上确认授权。";
            if (SelectedProviderId is { } provider) FocusProvider(provider);
            OnPropertyChanged(nameof(AccountSummary));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_disposed) Message = FriendlyError(ex); }
        finally { IsBusy = false; }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task LoginAsync(ProviderAccountRow row) => StartAsync(row.Account.ProviderId, row.Account.Name);

    private async Task StartAsync(string providerId, string name)
    {
        if (_disposed) return;
        CloseLogin();
        var generation = ++_generation;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _loginCancellation = cancellation;
        _providerId = providerId;
        IsLoginVisible = true; CanRefreshQr = false;
        LoginTitle = $"{name} · 扫码授权"; LoginMessage = "正在获取二维码……";
        try
        {
            var login = await BeginWithRetryAsync(providerId, cancellation.Token);
            if (_disposed || generation != _generation)
            {
                await TryCancelAsync(providerId, login.LoginId);
                return;
            }
            _loginId = login.LoginId;
            const string prefix = "data:image/png;base64,";
            if (!login.QrImage.StartsWith(prefix, StringComparison.Ordinal) || login.QrImage.Length > 512000) throw new InvalidDataException("平台返回了无效二维码");
            using var stream = new MemoryStream(Convert.FromBase64String(login.QrImage[prefix.Length..]));
            QrImage = new Bitmap(stream);
            CanRefreshQr = true;
            LoginMessage = login.Message;
            var transientErrors = 0;
            while (DateTimeOffset.UtcNow < login.ExpiresAt)
            {
                await Task.Delay(TimeSpan.FromSeconds(2.5), cancellation.Token);
                ProviderLoginResult result;
                try { result = await service.CheckLoginAsync(providerId, login.LoginId, cancellation.Token); transientErrors = 0; }
                catch (GatewayException ex) when (IsTransient(ex))
                {
                    if (++transientErrors >= 3) throw;
                    LoginMessage = "网络暂不可用，正在重试登录状态……";
                    continue;
                }
                if (_disposed || generation != _generation) return;
                LoginMessage = result.Message;
                if (result.State == "authorized")
                {
                    var row = Rows.FirstOrDefault(x => x.Account.ProviderId == providerId);
                    if (row is not null && result.Account is not null) row.Account = result.Account;
                    _loginId = null;
                    QrImage?.Dispose(); QrImage = null;
                    Message = result.Message;
                    CanRefreshQr = false;
                    OnPropertyChanged(nameof(AccountSummary));
                    AccountChanged?.Invoke(this, providerId);
                    return;
                }
                if (result.State == "expired") break;
            }
            if (generation == _generation) { LoginMessage = "二维码已过期，请刷新"; CanRefreshQr = true; QrImage?.Dispose(); QrImage = null; }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_disposed && generation == _generation)
            {
                LoginMessage = FriendlyError(ex); CanRefreshQr = true;
                QrImage?.Dispose(); QrImage = null;
                if (_loginId is { } failedLogin) await TryCancelAsync(providerId, failedLogin);
                _loginId = null;
            }
        }
        finally
        {
            if (ReferenceEquals(_loginCancellation, cancellation)) _loginCancellation = null;
            cancellation.Dispose();
        }
    }

    private async Task<ProviderLogin> BeginWithRetryAsync(string providerId, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await service.StartLoginAsync(providerId, token); }
            catch (GatewayException ex) when (attempt < 2 && IsTransient(ex))
            {
                LoginMessage = "网络暂不可用，正在重新获取二维码……";
                await Task.Delay(TimeSpan.FromSeconds(attempt + 1), token);
            }
        }
    }
    private static bool IsTransient(GatewayException error) => error.Kind is GatewayFailureKind.Timeout or GatewayFailureKind.RateLimited or GatewayFailureKind.Unavailable
        || error.Code is "network_error" or "tls_error" or "upstream_http_error";

    [RelayCommand]
    private Task RefreshQrAsync()
    {
        var row = Rows.FirstOrDefault(x => x.Account.ProviderId == _providerId);
        return row is null ? Task.CompletedTask : StartAsync(row.Account.ProviderId, row.Account.Name);
    }

    [RelayCommand]
    public void CloseLogin()
    {
        _generation++;
        _loginCancellation?.Cancel();
        if (_providerId is { } provider && _loginId is { } login) _ = TryCancelAsync(provider, login);
        _loginId = null;
        IsLoginVisible = false;
        QrImage?.Dispose(); QrImage = null;
    }
    private async Task TryCancelAsync(string providerId, string loginId)
    {
        try { await service.CancelLoginAsync(providerId, loginId, _lifetime.Token); } catch { }
    }

    [RelayCommand]
    private async Task LogoutAsync(ProviderAccountRow row)
    {
        if (_disposed || IsBusy) return;
        CloseLogin(); IsBusy = true;
        try
        {
            await service.LogoutAsync(row.Account.ProviderId, _lifetime.Token);
            row.Account = row.Account with { LoggedIn = false, DisplayName = "", Message = "已退出登录，可继续游客搜索" };
            Message = $"{row.Account.Name}已退出，已移除本机保存的登录凭证";
            OnPropertyChanged(nameof(AccountSummary));
            AccountChanged?.Invoke(this, row.Account.ProviderId);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Message = FriendlyError(ex); }
        finally { IsBusy = false; }
    }

    private static string FriendlyError(Exception error) => error is GatewayException ? error.Message : "账号操作失败，请刷新或稍后重试";
    public void Dispose()
    {
        if (_disposed) return;
        CloseLogin(); _disposed = true; _lifetime.Cancel(); _lifetime.Dispose();
    }
}
