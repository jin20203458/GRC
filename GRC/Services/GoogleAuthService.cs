using Google.Apis.Auth.OAuth2;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace GRC.Services;

public class GoogleAuthService : IGoogleAuthService
{
    private GoogleCredential? _cachedCredential;
    private readonly SemaphoreSlim _credentialLock = new(1, 1);

    public async Task<string> GetGoogleAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_cachedCredential == null)
        {
            await _credentialLock.WaitAsync(cancellationToken);
            try
            {
                if (_cachedCredential == null)
                {
                    string jsonKeyPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "google-credentials.json");
                    
                    // 비동기로 안전하게 키 파일 로드
                    string jsonContent = await File.ReadAllTextAsync(jsonKeyPath, cancellationToken);
                    var specificCredential = Google.Apis.Auth.OAuth2.CredentialFactory.FromJson<Google.Apis.Auth.OAuth2.ServiceAccountCredential>(jsonContent);

                    _cachedCredential = specificCredential.ToGoogleCredential()
                        .CreateScoped("https://www.googleapis.com/auth/cloud-platform");
                }
            }
            finally
            {
                _credentialLock.Release();
            }
        }

        // GoogleCredential 내부에서 자체 토큰 만료를 점검하고 필요시 갱신/캐싱된 값을 반환합니다.
        return await ((ITokenAccess)_cachedCredential).GetAccessTokenForRequestAsync(authUri: null, cancellationToken: cancellationToken);
    }
}
