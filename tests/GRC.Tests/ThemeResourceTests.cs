using System;
using System.IO.Packaging;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using Xunit;

namespace GRC.Tests;

public class ThemeResourceTests
{
    static ThemeResourceTests()
    {
        _ = PackUriHelper.UriSchemePack;
    }

    [Fact]
    public void PrimitiveTokens_LoadsSuccessfully_ContainsMandatoryColorsAndMetrics()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                _ = PackUriHelper.UriSchemePack;
                if (Application.Current == null)
                {
                    try { _ = new Application(); } catch (InvalidOperationException) { }
                }

                var uri = new Uri("/GRC;component/Themes/PrimitiveTokens.xaml", UriKind.Relative);
                var rd = (ResourceDictionary)Application.LoadComponent(uri);

                Assert.NotNull(rd);
                Assert.True(rd.Contains("ColorZinc950"), "ColorZinc950 is missing");
                Assert.True(rd.Contains("ColorZinc900"), "ColorZinc900 is missing");
                Assert.True(rd.Contains("ColorAccentPrimary"), "ColorAccentPrimary is missing");
                Assert.True(rd.Contains("ColorTextPrimary"), "ColorTextPrimary is missing");
                Assert.True(rd.Contains("CornerRadiusCard"), "CornerRadiusCard is missing");
                Assert.True(rd.Contains("CornerRadiusBubble"), "CornerRadiusBubble is missing");
                Assert.True(rd.Contains("SansFontFamily"), "SansFontFamily is missing");
                Assert.True(rd.Contains("CodeFontFamily"), "CodeFontFamily is missing");
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        bool finished = thread.Join(5000);

        Assert.True(finished, "Theme test thread timed out");
        Assert.Null(threadEx);
    }

    [Fact]
    public void DarkTheme_WithPrimitiveTokensMerged_LoadsAndAllBrushesAreFrozen()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                _ = PackUriHelper.UriSchemePack;
                if (Application.Current == null)
                {
                    try { _ = new Application(); } catch (InvalidOperationException) { }
                }

                var primitiveUri = new Uri("/GRC;component/Themes/PrimitiveTokens.xaml", UriKind.Relative);
                var primitiveRd = (ResourceDictionary)Application.LoadComponent(primitiveUri);
                if (Application.Current?.Resources.MergedDictionaries != null &&
                    !Application.Current.Resources.MergedDictionaries.Contains(primitiveRd))
                {
                    Application.Current.Resources.MergedDictionaries.Add(primitiveRd);
                }

                var darkThemeUri = new Uri("/GRC;component/Themes/DarkTheme.xaml", UriKind.Relative);
                var darkThemeRd = (ResourceDictionary)Application.LoadComponent(darkThemeUri);

                string[] requiredBrushes =
                [
                    "AppBackgroundBrush",
                    "SurfaceBackgroundBrush",
                    "CardBackgroundBrush",
                    "CardBackgroundElevatedBrush",
                    "BorderSubtleBrush",
                    "TextPrimaryBrush",
                    "TextSecondaryBrush",
                    "TextThoughtBrush",
                    "AccentPrimaryBrush",
                    "StatusSuccessBgBrush",
                    "StatusErrorBgBrush",
                    "StatusWarningBgBrush"
                ];

                foreach (var brushKey in requiredBrushes)
                {
                    Assert.True(darkThemeRd.Contains(brushKey), $"{brushKey} is missing in DarkTheme.xaml");
                    var brush = darkThemeRd[brushKey] as SolidColorBrush;
                    Assert.NotNull(brush);
                    Assert.True(brush.IsFrozen, $"{brushKey} should be frozen (po:Freeze=True)");
                }
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        bool finished = thread.Join(5000);

        Assert.True(finished, "Theme test thread timed out");
        Assert.Null(threadEx);
    }
}
