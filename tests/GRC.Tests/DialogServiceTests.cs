using System;
using GRC.Services;
using Xunit;

namespace GRC.Tests;

public class DialogServiceTests
{
    private class DummyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    [Fact]
    public void RunOnUIThread_WhenDispatcherNullOrAccessible_ExecutesImmediately()
    {
        var service = new DialogService(new DummyServiceProvider());
        bool executed = false;

        service.RunOnUIThread(() => executed = true);

        Assert.True(executed);
    }

    [Fact]
    public void CloseAuxiliaryWindows_WhenNoAuxiliaryWindows_CompletesWithoutError()
    {
        var service = new DialogService(new DummyServiceProvider());

        var ex = Record.Exception(() => service.CloseAuxiliaryWindows());

        Assert.Null(ex);
    }
}
