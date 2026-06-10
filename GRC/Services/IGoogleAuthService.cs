using System.Threading;
using System.Threading.Tasks;

namespace GRC.Services;

public interface IGoogleAuthService
{
    Task<string> GetGoogleAccessTokenAsync(CancellationToken cancellationToken);
}
