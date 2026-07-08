using System.Windows;
using Wpf.Ui;

namespace AIVTuber.App;

/// <summary>Simple IPageService that returns pre-built FrameworkElement instances by type key.</summary>
internal sealed class PageService : IPageService
{
    private readonly Dictionary<Type, FrameworkElement> _pages = [];

    public void Register(Type key, FrameworkElement page) => _pages[key] = page;

    public T? GetPage<T>() where T : class => _pages.GetValueOrDefault(typeof(T)) as T;
    public FrameworkElement? GetPage(Type pageType) => _pages.GetValueOrDefault(pageType);
}
