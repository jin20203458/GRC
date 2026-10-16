using GRC.Models;
using System.Threading.Tasks;

namespace GRC.Services;

public interface ILorebookService
{
    Task<LorebookEntry?> ExtractMemoryToLorebookAsync(string messageText);
}
