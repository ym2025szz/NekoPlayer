using NekoPlayer.Core.Models;

namespace NekoPlayer.Core.Interfaces;

public interface IProviderAccountService
{
    long Revision { get; }
    Task EnsureReadyAsync(CancellationToken cancellationToken = default);
    Task EnsureProviderReadyAsync(string providerId, CancellationToken cancellationToken = default) => EnsureReadyAsync(cancellationToken);
    long GetProviderRevision(string providerId) => Revision;
    Task<IReadOnlyList<ProviderAccount>> GetAccountsAsync(CancellationToken cancellationToken = default);
    Task<ProviderLogin> StartLoginAsync(string providerId, CancellationToken cancellationToken = default);
    Task<ProviderLoginResult> CheckLoginAsync(string providerId, string loginId, CancellationToken cancellationToken = default);
    Task CancelLoginAsync(string providerId, string loginId, CancellationToken cancellationToken = default);
    Task LogoutAsync(string providerId, CancellationToken cancellationToken = default);
}
