using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace ExpenseAssistant.Core;

/// <summary>
/// Tries each model in order and answers from the first that works: for example a self-hosted open model first (no
/// quota, data stays on your GPU), a cloud model when that is down, and the caller's rules after that.
/// </summary>
public sealed class FallbackChatClient(IReadOnlyList<(string Name, IChatClient Client)> providers, Action<string, Exception>? onError = null) : IChatClient
{
    public IReadOnlyList<string> Names { get; } = providers.Select(p => p.Name).ToList();

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
    {
        var list = messages.ToList();
        Exception? last = null;
        foreach (var (name, client) in providers)
        {
            try
            {
                var r = await client.GetResponseAsync(list, options, ct);
                r.ModelId = $"{r.ModelId ?? "model"} ({name})";
                return r;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                onError?.Invoke(name, e);
                last = e;
            }
        }
        throw last ?? new InvalidOperationException("No AI provider is configured.");
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken ct = default) => providers[0].Client.GetStreamingResponseAsync(messages, options, ct);

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceType == typeof(FallbackChatClient) ? this : null;

    public void Dispose() { foreach (var p in providers) p.Client.Dispose(); }
}
