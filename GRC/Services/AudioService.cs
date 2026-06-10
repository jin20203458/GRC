using System;
using System.Windows.Media;
using System.Collections.Generic;
namespace GRC.Services;

public interface IAudioService
{
    void SetBgmState(bool isEnabled);
    void SetBgmVolume(double volume);
    void SetTypingSoundState(bool isEnabled);
    void SetTypingSoundVolume(double volume);
    void StartTypingSound();
    void StopTypingSound();
    void PlayVoiceSound(string filePath);
    Task PlayVoiceSoundAsync(string filePath);
    void StopVoiceSound();
}

public class AudioService : IAudioService
{
    private readonly MediaPlayer _bgmPlayer = new();
    private readonly MediaPlayer _typingPlayer = new();

    private readonly MediaPlayer _voicePlayer = new();
    private bool _isBgmPlaying = false;
    private bool _isTypingSoundEnabled = false;

    private readonly Queue<(string FilePath, TaskCompletionSource<bool> Tcs)> _voiceQueue = new();
    private TaskCompletionSource<bool>? _currentVoiceTcs;

    private bool _isVoicePlaying = false;

    public AudioService()
    {
        // 1. BGM 플레이어 설정
        _bgmPlayer.Open(new Uri("pack://siteoforigin:,,,/Resources/bgm.m4a"));
        _bgmPlayer.MediaEnded += (s, e) =>
        {
            _bgmPlayer.Position = TimeSpan.Zero;
            _bgmPlayer.Play();
        };

        // 2. 타건음 플레이어 설정
        _typingPlayer.Open(new Uri("pack://siteoforigin:,,,/Resources/typing.mp3"));
        _typingPlayer.MediaEnded += (s, e) =>
        {
            _typingPlayer.Position = TimeSpan.Zero;
            _typingPlayer.Play();
        };

        _voicePlayer.MediaEnded += (s, e) =>
        {
            _currentVoiceTcs?.TrySetResult(true);
            PlayNextVoice();
        };

        _voicePlayer.MediaFailed += (s, e) =>
        {
            System.Diagnostics.Debug.WriteLine($"[Audio Error]: TTS 오디오 파일 재생 실패");

            // 에러가 나도 무한 대기하지 않도록 false 신호를 보내 락 해제
            _currentVoiceTcs?.TrySetResult(false);

            // 멈추지 않고 큐에 있는 다음 대사들을 계속 재생
            PlayNextVoice();
        };
    }

    public void SetBgmState(bool isEnabled)
    {
        if (isEnabled && !_isBgmPlaying)
        {
            _bgmPlayer.Play();
            _isBgmPlaying = true;
        }
        else if (!isEnabled && _isBgmPlaying)
        {
            _bgmPlayer.Pause();
            _isBgmPlaying = false;
        }
    }

    public void SetBgmVolume(double volume) => _bgmPlayer.Volume = volume;

    public void SetTypingSoundState(bool isEnabled) => _isTypingSoundEnabled = isEnabled;
    public void SetTypingSoundVolume(double volume) => _typingPlayer.Volume = volume;

    public void StartTypingSound()
    {
        if (_isTypingSoundEnabled)
        {
            _typingPlayer.Position = TimeSpan.Zero;
            _typingPlayer.Play();
        }
    }
    public void StopTypingSound()
    {
        _typingPlayer.Pause();
    }
    public Task PlayVoiceSoundAsync(string filePath)
    {
        var tcs = new TaskCompletionSource<bool>();
        _voiceQueue.Enqueue((filePath, tcs));

        if (!_isVoicePlaying)
        {
            PlayNextVoice();
        }
        return tcs.Task; // 음성 재생이 끝날 때까지 완료되지 않음
    }
    public void PlayVoiceSound(string filePath) => _ = PlayVoiceSoundAsync(filePath);

    private void PlayNextVoice()
    {
        if (_voiceQueue.Count > 0)
        {
            _isVoicePlaying = true;
            var next = _voiceQueue.Dequeue();
            _currentVoiceTcs = next.Tcs; // 완료 신호를 보낼 Tcs 추적

            _voicePlayer.Open(new Uri(next.FilePath, UriKind.Absolute));
            _voicePlayer.Play();
        }
        else
        {
            _isVoicePlaying = false;
            _currentVoiceTcs = null;
        }
    }
    public void StopVoiceSound()
    {
        _voiceQueue.Clear();
        _voicePlayer.Stop();
        _isVoicePlaying = false;
        // 스킵(정지) 시 무한 대기에 빠지지 않도록 취소 신호를 보냄
        _currentVoiceTcs?.TrySetResult(false);
        _currentVoiceTcs = null;
    }
}
