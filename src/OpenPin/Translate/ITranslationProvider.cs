namespace OpenPin.Translate;

/// <summary>A machine translation service. Implementations are thread-safe and never log secrets.</summary>
public interface ITranslationProvider
{
    /// <summary>Provider id ("openai", "baidu", "youdao", "deepl").</summary>
    string Id { get; }

    /// <summary>
    /// Translate <paramref name="segments"/> (order and count are preserved; blank segments are returned unchanged
    /// without a request). <paramref name="sourceLang"/> null = auto-detect. Tags are those of <see cref="TranslateLanguages"/>.
    /// Throws <see cref="TranslationException"/> with a user-facing message on failure and
    /// <see cref="OperationCanceledException"/> when <paramref name="ct"/> is cancelled.
    /// </summary>
    Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> segments, string? sourceLang, string targetLang, CancellationToken ct);
}

/// <summary>A failure with a message that can be shown to the user (never contains a key).</summary>
public sealed class TranslationException : Exception
{
    public TranslationException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Common batching: skips blank segments, splits the rest into ordered batches, re-assembles the results.</summary>
public abstract class TranslationProviderBase : ITranslationProvider
{
    public abstract string Id { get; }

    /// <summary>Maximum segments per request.</summary>
    protected virtual int MaxBatchItems => 40;

    /// <summary>Maximum characters per request (approximate).</summary>
    protected virtual int MaxBatchChars => 3000;

    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> segments, string? sourceLang, string targetLang, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var result = new string[segments.Count];
        var todo = new List<int>();
        for (int i = 0; i < segments.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(segments[i])) result[i] = segments[i] ?? "";
            else todo.Add(i);
        }
        if (todo.Count == 0) return result;

        var texts = todo.Select(i => segments[i]).ToList();
        foreach (var batch in TranslationBatcher.Split(texts, MaxBatchItems, MaxBatchChars))
        {
            ct.ThrowIfCancellationRequested();
            var input = batch.Select(k => texts[k]).ToList();
            var output = await TranslateBatchAsync(input, sourceLang, targetLang, ct).ConfigureAwait(false);
            if (output.Count != input.Count)
                throw new TranslationException(Core.Loc.Format("Translate.Error.Count", input.Count, output.Count));
            for (int j = 0; j < batch.Count; j++) result[todo[batch[j]]] = output[j] ?? "";
        }
        return result;
    }

    /// <summary>Translate one batch (non-blank segments). Must return exactly one string per input, in order.</summary>
    protected abstract Task<IReadOnlyList<string>> TranslateBatchAsync(IReadOnlyList<string> segments, string? sourceLang, string targetLang, CancellationToken ct);
}

/// <summary>Splits segments into consecutive batches by item count and character budget (pure, unit tested).</summary>
public static class TranslationBatcher
{
    /// <summary>
    /// Consecutive index ranges of <paramref name="segments"/>: each batch has at most <paramref name="maxItems"/> items and
    /// at most <paramref name="maxChars"/> characters, except that a single oversized segment forms its own batch.
    /// Concatenating the batches gives 0..n-1 in order.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<int>> Split(IReadOnlyList<string> segments, int maxItems, int maxChars)
    {
        maxItems = Math.Max(1, maxItems);
        maxChars = Math.Max(1, maxChars);
        var batches = new List<IReadOnlyList<int>>();
        var current = new List<int>();
        int chars = 0;
        for (int i = 0; i < segments.Count; i++)
        {
            int len = segments[i]?.Length ?? 0;
            if (current.Count > 0 && (current.Count >= maxItems || chars + len > maxChars))
            {
                batches.Add(current);
                current = new List<int>();
                chars = 0;
            }
            current.Add(i);
            chars += len;
        }
        if (current.Count > 0) batches.Add(current);
        return batches;
    }
}
