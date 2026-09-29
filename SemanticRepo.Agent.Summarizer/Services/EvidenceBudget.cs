using SemanticRepo.Agent.Summarizer.Infrastructure;
using SemanticRepo.Contracts.Summarization;

namespace SemanticRepo.Agent.Summarizer.Services;

internal sealed class EvidenceBudget(SummarizerOptions options)
{
    private readonly List<SummaryEvidenceDto> evidence = [];
    private int characters;

    public IReadOnlyList<SummaryEvidenceDto> Evidence => evidence;
    public bool WasLimited { get; private set; }

    public void Add(SummaryEvidenceDto item)
    {
        var remaining = options.MaxEvidenceCharacters - characters;
        if (evidence.Count >= options.MaxEvidenceItems || remaining <= 0)
        {
            WasLimited = true;
            return;
        }
        if (item.Content.Length > remaining)
        {
            var length = remaining;
            if (char.IsHighSurrogate(item.Content[length - 1]))
                length--;
            if (length == 0)
            {
                WasLimited = true;
                return;
            }
            item = item with { Content = item.Content[..length], IsTruncated = true };
            WasLimited = true;
        }
        characters += item.Content.Length;
        evidence.Add(item);
    }
}
