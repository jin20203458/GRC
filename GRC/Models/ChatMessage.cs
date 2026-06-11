using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Text.Json.Serialization;
using static System.Net.Mime.MediaTypeNames;

namespace GRC.Models;

/// <summary>
/// 개별 대화 단위를 저장하는 객체
/// (UI 실시간 갱신을 위해 ObservableObject 적용 및 Text 프로퍼티 수정 가능)
/// </summary>
public partial class ChatMessage : ObservableObject
{
    public string Role { get; set; } = "";

    [ObservableProperty]
    private string _text = "";

    public DateTime Timestamp { get; set; }

    // JSON 파싱을 위한 기본 생성자
    public ChatMessage() 
    { 
        Role = "";
        _text = "";
    }

    [JsonConstructor]
    public ChatMessage(string role, string text, DateTime timestamp)
    {
        Role = role;
        Text = text;
        Timestamp = timestamp;
    }
}