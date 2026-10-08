using System.Text.Json;
using NekoPlayer.Core.Interfaces;
using NekoPlayer.Core.Models;

namespace NekoPlayer.Infrastructure.Online;

public sealed class ProviderAccountService(IGatewayRuntime gateway, WindowsAccountVault vault) : IProviderAccountService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly string[] LoginProviders = ["netease", "qq", "kugou"];
    private readonly object _sync = new();
    private readonly Dictionary<string, SemaphoreSlim> _providerGates = LoginProviders.ToDictionary(id => id, _ => new SemaphoreSlim(1, 1));
    private readonly Dictionary<string, long> _providerRevisions = [];
    private readonly Dictionary<string, long> _mutations = [];
    private RestoreState? _restore;
    private long _revision;
    public long Revision => Interlocked.Read(ref _revision);

    public long GetProviderRevision(string providerId)
    {
        lock (_sync) return _providerRevisions.GetValueOrDefault(Normalize(providerId));
    }

    public async Task EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        var state = await GetRestoreStateAsync(cancellationToken).ConfigureAwait(false);
        await RestoreAllAsync(state, retryFailures: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task EnsureProviderReadyAsync(string providerId, CancellationToken cancellationToken = default)
    {
        var state = await GetRestoreStateAsync(cancellationToken).ConfigureAwait(false);
        var id = Normalize(providerId);
        // Guest-only sources do not wait for credential IO or another platform's validation.
        if (!LoginProviders.Contains(id)) return;
        var credentials = await state.Credentials.WaitAsync(cancellationToken).ConfigureAwait(false);
        await GetRestoreTask(state, id, credentials, retryFailures: false).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<RestoreState> GetRestoreStateAsync(CancellationToken cancellationToken)
    {
        var address = await gateway.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            if (_restore?.Address == address) return _restore;
            var state = new RestoreState(address);
            _restore = state;
            var revision = Interlocked.Increment(ref _revision);
            foreach (var id in new[] { "netease", "qq", "kuwo", "kugou", "qishui" }) _providerRevisions[id] = revision;
            state.Credentials = ReadCredentialsAsync(state);
            return state;
        }
    }

    private async Task<Dictionary<string, string>> ReadCredentialsAsync(RestoreState state)
    {
        await Task.Yield();
        try
        {
            var credentials = await vault.ReadAsync().ConfigureAwait(false);
            lock (_sync) state.CredentialError = null;
            return credentials;
        }
        catch (Exception)
        {
            lock (_sync) state.CredentialError = "本机登录凭证无法读取，请重新登录";
            return [];
        }
    }

    private async Task RestoreAllAsync(RestoreState state, bool retryFailures, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (retryFailures && state.Credentials.IsCompleted && state.CredentialError is not null)
                state.Credentials = ReadCredentialsAsync(state);
        }
        var credentials = await state.Credentials.WaitAsync(cancellationToken).ConfigureAwait(false);
        var tasks = LoginProviders.Select(id => GetRestoreTask(state, id, credentials, retryFailures)).ToArray();
        await Task.WhenAll(tasks).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task GetRestoreTask(RestoreState state, string id, Dictionary<string, string> credentials, bool retryFailures)
    {
        lock (_sync)
        {
            if (state.Operations.TryGetValue(id, out var current) && (!retryFailures || !current.IsCompleted || !state.Errors.ContainsKey(id)))
                return current;
            if (!credentials.TryGetValue(id, out var credential)) return Task.CompletedTask;
            return state.Operations[id] = RestoreProviderAsync(state, id, credential, _mutations.GetValueOrDefault(id));
        }
    }

    private async Task RestoreProviderAsync(RestoreState state, string id, string credential, long mutation)
    {
        await Task.Yield();
        var gate = _providerGates[id];
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_sync)
                if (!ReferenceEquals(_restore, state) || _mutations.GetValueOrDefault(id) != mutation) return;
            // A caller can leave its wait without cancelling another request's credential restoration.
            await gateway.SendAsync("/v1/auth/restore", new { providerId = id, credential }).ConfigureAwait(false);
            lock (_sync)
            {
                if (!ReferenceEquals(_restore, state) || _mutations.GetValueOrDefault(id) != mutation) return;
                state.Errors.Remove(id);
                if (id == "qq") state.Unverified.Add(id);
                BumpRevision(id);
            }
        }
        catch (Exception ex)
        {
            lock (_sync)
                if (ReferenceEquals(_restore, state) && _mutations.GetValueOrDefault(id) == mutation)
                    state.Errors[id] = ex is GatewayException ? ex.Message : "登录凭证恢复失败，请刷新状态或重新登录";
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<ProviderAccount>> GetAccountsAsync(CancellationToken cancellationToken = default)
    {
        var state = await GetRestoreStateAsync(cancellationToken).ConfigureAwait(false);
        // Opening/refreshing account status explicitly retries previously failed restoration.
        await RestoreAllAsync(state, retryFailures: true, cancellationToken).ConfigureAwait(false);
        var body = await gateway.SendAsync("/v1/auth/status", new { }, cancellationToken).ConfigureAwait(false);
        var accounts = body.GetProperty("accounts").Deserialize<ProviderAccount[]>(JsonOptions) ?? [];
        lock (_sync)
            return accounts.Select(account =>
            {
                if (!account.LoggedIn && (state.Errors.TryGetValue(account.ProviderId, out var error) || (error = state.CredentialError) is not null))
                    return account with { Message = error };
                if (account.LoggedIn && state.Unverified.Contains(account.ProviderId))
                    return account with { Message = "已恢复本机保存的登录凭证，尚未重新验证；实际播放时确认账号权限" };
                return account;
            }).ToArray();
    }

    public async Task<ProviderLogin> StartLoginAsync(string providerId, CancellationToken cancellationToken = default)
    {
        var id = Normalize(providerId);
        await EnsureProviderReadyAsync(id, cancellationToken).ConfigureAwait(false);
        var body = await gateway.SendAsync("/v1/auth/start", new { providerId = id }, cancellationToken).ConfigureAwait(false);
        return body.Deserialize<ProviderLogin>(JsonOptions) ?? throw new InvalidDataException("登录二维码响应无效");
    }

    public async Task<ProviderLoginResult> CheckLoginAsync(string providerId, string loginId, CancellationToken cancellationToken = default)
    {
        var id = Normalize(providerId);
        var gate = GetProviderGate(id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var body = await gateway.SendAsync("/v1/auth/check", new { providerId = id, loginId }, cancellationToken).ConfigureAwait(false);
            var result = body.Deserialize<ProviderLoginResult>(JsonOptions) ?? throw new InvalidDataException("登录状态响应无效");
            if (result.State != "authorized") return result;
            // Upstream authorization is already effective even if a later status/save wait is cancelled.
            lock (_sync) AccountMutated(id);
            var status = await gateway.SendAsync("/v1/auth/status", new { }, cancellationToken).ConfigureAwait(false);
            var current = status.GetProperty("accounts").Deserialize<ProviderAccount[]>(JsonOptions)?.FirstOrDefault(x => x.ProviderId == id);
            if (current?.LoggedIn != true) return new("expired", "登录已取消");
            try { await vault.SetAsync(id, body.GetProperty("credential").GetString(), cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { result = result with { Message = "已授权，本次会话有效；凭证保存失败，下次需重新登录" }; }
            return result;
        }
        finally { gate.Release(); }
    }

    public Task CancelLoginAsync(string providerId, string loginId, CancellationToken cancellationToken = default) =>
        gateway.SendAsync("/v1/auth/cancel", new { providerId = Normalize(providerId), loginId }, cancellationToken);

    public async Task LogoutAsync(string providerId, CancellationToken cancellationToken = default)
    {
        var id = Normalize(providerId);
        var gate = GetProviderGate(id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Persistent removal and the version change survive an unavailable gateway.
            await vault.SetAsync(id, null, cancellationToken).ConfigureAwait(false);
            lock (_sync) AccountMutated(id);
            await gateway.SendAsync("/v1/auth/logout", new { providerId = id }, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private SemaphoreSlim GetProviderGate(string id) => _providerGates.TryGetValue(id, out var gate) ? gate
        : throw new GatewayException(GatewayFailureKind.Provider, "login_unavailable", "此来源暂不支持账号登录");
    private static string Normalize(string providerId) => providerId.Trim().ToLowerInvariant();
    private void BumpRevision(string id) => _providerRevisions[id] = Interlocked.Increment(ref _revision);
    private void AccountMutated(string id)
    {
        _mutations[id] = _mutations.GetValueOrDefault(id) + 1;
        if (_restore is { } state)
        {
            state.Errors.Remove(id);
            state.Unverified.Remove(id);
            state.Operations[id] = Task.CompletedTask;
        }
        BumpRevision(id);
    }

    private sealed class RestoreState(Uri address)
    {
        public Uri Address { get; } = address;
        public Task<Dictionary<string, string>> Credentials { get; set; } = null!;
        public string? CredentialError { get; set; }
        public Dictionary<string, Task> Operations { get; } = [];
        public Dictionary<string, string> Errors { get; } = [];
        public HashSet<string> Unverified { get; } = [];
    }
}
