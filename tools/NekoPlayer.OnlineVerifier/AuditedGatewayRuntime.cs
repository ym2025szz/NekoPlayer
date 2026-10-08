using System.Text.Json;
using NekoPlayer.Core.Interfaces;
using NekoPlayer.Core.Models;
using NekoPlayer.Infrastructure.Online;

namespace NekoPlayer.OnlineVerifier;

internal sealed class AuditedGatewayRuntime(IGatewayRuntime inner) : IGatewayRuntime
{
    private readonly AsyncLocal<CapabilityResult?> _current = new();
    public GatewayState State => inner.State;
    public string? LastError => inner.LastError;

    public IDisposable Observe(CapabilityResult result)
    {
        var previous = _current.Value;
        _current.Value = result;
        return new Scope(() => _current.Value = previous);
    }

    public Task<Uri> EnsureReadyAsync(CancellationToken cancellationToken = default) => inner.EnsureReadyAsync(cancellationToken);
    public Task StopAsync() => inner.StopAsync();
    public ValueTask DisposeAsync() => inner.DisposeAsync();

    public async Task<JsonElement> SendAsync(string route, object? payload = null, CancellationToken cancellationToken = default)
    {
        var observation = _current.Value;
        try
        {
            var response = await inner.SendAsync(route, payload, cancellationToken);
            if (observation?.Status == "running")
            {
                // IGatewayRuntime exposes successful JSON, not the exact successful HTTP status.
                observation.Http = "success-status-not-exposed";
            }
            return response;
        }
        catch (GatewayException error)
        {
            if (observation?.Status == "running")
            {
                observation.HttpStatusCode = error.HttpStatusCode;
                observation.Http = error.HttpStatusCode.HasValue ? "response" : "transport-or-protocol-failed";
            }
            throw;
        }
        catch
        {
            if (observation?.Status == "running") observation.Http = "failed";
            throw;
        }
    }

    private sealed class Scope(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
