namespace NekoPlayer.Infrastructure.Online;

public enum GatewayFailureKind { Unavailable, Timeout, Protocol, Provider, RateLimited }

/// <summary>A gateway failure that can be presented without exposing provider URLs or credentials.</summary>
public sealed class GatewayException : Exception
{
    public GatewayException(GatewayFailureKind kind, string code, string message, Exception? innerException = null, int? httpStatusCode = null)
        : base(message, innerException)
    {
        Kind = kind;
        Code = code;
        HttpStatusCode = httpStatusCode;
    }

    public GatewayFailureKind Kind { get; }
    public string Code { get; }
    public int? HttpStatusCode { get; }
}
