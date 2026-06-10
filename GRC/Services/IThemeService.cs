using GRC.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GRC.Services;

public interface IThemeService
{
    string GetRandomBackground(BackgroundTheme theme, ref List<string> unusedBackgrounds, string currentImage);
}

public class ThemeService : IThemeService
{
    private readonly Random _random = new();

    // 뷰모델에 하드코딩되어 있던 이미지 경로들을 테마 서비스로 이관
    private readonly string[] _fantasyImages = [
        "pack://application:,,,/Resources/Fantasy/bg1.jpg", "pack://application:,,,/Resources/Fantasy/bg2.jpg",
        "pack://application:,,,/Resources/Fantasy/bg3.jpg", "pack://application:,,,/Resources/Fantasy/bg4.jpg",
        "pack://application:,,,/Resources/Fantasy/bg5.jpg", "pack://application:,,,/Resources/Fantasy/bg6.jpg",
        "pack://application:,,,/Resources/Fantasy/bg7.png", "pack://application:,,,/Resources/Fantasy/bg8.png",
        "pack://application:,,,/Resources/Fantasy/bg9.PNG", "pack://application:,,,/Resources/Fantasy/bg10.PNG",
        "pack://application:,,,/Resources/Fantasy/bg11.PNG"
    ];
    private readonly string[] _modernImages = [
        "pack://application:,,,/Resources/Modern/bg1.jpg", "pack://application:,,,/Resources/Modern/bg2.jpg",
        "pack://application:,,,/Resources/Modern/bg3.jpg", "pack://application:,,,/Resources/Modern/bg4.jpg",
        "pack://application:,,,/Resources/Modern/bg5.jpg"
    ];
    private readonly string[] _cyberpunkImages = [
        "pack://application:,,,/Resources/Cyberpunk/bg1.jpg", "pack://application:,,,/Resources/Cyberpunk/bg2.jpg",
        "pack://application:,,,/Resources/Cyberpunk/bg3.jpg", "pack://application:,,,/Resources/Cyberpunk/bg4.jpg",
        "pack://application:,,,/Resources/Cyberpunk/bg5.jpg"
    ];

    public string GetRandomBackground(BackgroundTheme theme, ref List<string> unusedBackgrounds, string currentImage)
    {
        string[] targetImages = theme switch
        {
            BackgroundTheme.Modern => _modernImages,
            BackgroundTheme.Cyberpunk => _cyberpunkImages,
            _ => _fantasyImages
        };

        if (targetImages.Length <= 1) return currentImage;

        if (unusedBackgrounds.Count == 0 || !unusedBackgrounds.Any(img => targetImages.Contains(img)))
        {
            unusedBackgrounds = new List<string>(targetImages);
            unusedBackgrounds.Remove(currentImage);
        }

        if (unusedBackgrounds.Count == 0) return currentImage;

        int index = _random.Next(unusedBackgrounds.Count);
        string newImage = unusedBackgrounds[index];
        unusedBackgrounds.RemoveAt(index);

        return newImage;
    }
}