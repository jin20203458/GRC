using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using Xunit;

namespace GRC.Tests;

public class IconResourceTests
{
    private static void EnsureApplication()
    {
        if (Application.Current == null)
        {
            try
            {
                _ = new Application();
            }
            catch (InvalidOperationException)
            {
                // 이미 AppDomain에 인스턴스가 존재하는 경우 무시
            }
        }
    }

    [Fact]
    public void AppIcon_ResourceStream_ExistsAndDecodesSuccessfully()
    {
        EnsureApplication();

        var packUri = new Uri("pack://application:,,,/GRC;component/Resources/GRC.ico");
        var sri = Application.GetResourceStream(packUri);
        Assert.NotNull(sri);
        Assert.True(sri.Stream.Length > 0);

        var decoder = new IconBitmapDecoder(sri.Stream, BitmapCreateOptions.None, BitmapCacheOption.Default);
        Assert.NotEmpty(decoder.Frames);
        Assert.Equal(256, decoder.Frames[0].PixelWidth);
        Assert.Equal(256, decoder.Frames[0].PixelHeight);
    }

    [Fact]
    public void WindowIcon_BitmapFrame_LoadsSuccessfullyOnStaThread()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                EnsureApplication();

                var iconFrame = BitmapFrame.Create(new Uri("pack://application:,,,/GRC;component/Resources/GRC.ico"));
                Assert.NotNull(iconFrame);
                Assert.Equal(256, iconFrame.PixelWidth);
                Assert.Equal(256, iconFrame.PixelHeight);
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(threadEx);
    }
}
