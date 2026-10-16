using GRC.Models;
using System.Threading.Tasks;

namespace GRC.Services;

public interface ILorebookService
{
    Task<LorebookEntry?> ExtractMemoryToLorebookAsync(string messageText);
    Task<List<LorebookEntry>> EnsureEmbeddingsAsync(
        System.Collections.Generic.IEnumerable<LorebookEntry>? lorebooks,
        System.Threading.CancellationToken cancellationToken = default);
}
