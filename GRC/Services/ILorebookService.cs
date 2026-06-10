using GRC.Models;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
namespace GRC.Services;

public interface ILorebookService
{
    string BuildLorebookInjection(List<LorebookEntry>? lorebooks, ChapterContext currentContext, IEnumerable<ChatMessage> recentMemory);
    Task<LorebookEntry?> ExtractMemoryToLorebookAsync(string messageText);
}
