using System;
using System.IO.Packaging;
using System.Threading;
using System.Threading.Tasks;
using GRC.Services;
using Xunit;

namespace GRC.Tests;

public class AudioServiceTests
{
    static AudioServiceTests()
    {
        _ = PackUriHelper.UriSchemePack;
    }

    [Fact]
    public void AudioService_CreatedOnStaThread_CanSetPropertiesAndToggle()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                _ = PackUriHelper.UriSchemePack;
                var audioService = new AudioService();
                audioService.SetTypingSoundState(true);
                audioService.SetTypingSoundVolume(0.5);
                audioService.SetBgmState(true);
                audioService.SetBgmVolume(0.5);
                audioService.SetBgmState(false);
                audioService.StopVoiceSound();
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        bool finished = thread.Join(5000);

        Assert.True(finished, "STA thread timed out");
        Assert.Null(threadEx);
    }

    [Fact]
    public async Task AudioService_BackgroundThreadCalls_DoNotThrowStaException()
    {
        _ = PackUriHelper.UriSchemePack;
        var tcs = new TaskCompletionSource<AudioService>();
        var thread = new Thread(() =>
        {
            try
            {
                var audioService = new AudioService();
                tcs.SetResult(audioService);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var audioService = await tcs.Task;

        // 백그라운드 스레드(MTA)에서 AudioService 메서드 호출 시
        // RunOnUIThread를 통해 안전하게 디스패칭되어 STA 스레드 위반 예외가 발생하지 않아야 함
        await Task.Run(() =>
        {
            audioService.SetTypingSoundState(true);
            audioService.SetTypingSoundVolume(0.7);
            audioService.StartTypingSound();
            audioService.StopTypingSound();

            audioService.SetBgmState(true);
            audioService.SetBgmVolume(0.5);
            audioService.SetBgmState(false);

            audioService.StopVoiceSound();
        });
    }
}
