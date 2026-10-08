namespace NekoPlayer.Core.Models;

public sealed record ProviderAccount(string ProviderId, string Name, bool CanLogin, bool LoggedIn, string DisplayName, string Message);
public sealed record ProviderLogin(string ProviderId, string LoginId, string QrImage, DateTimeOffset ExpiresAt, string Message);
public sealed record ProviderLoginResult(string State, string Message, ProviderAccount? Account = null);
