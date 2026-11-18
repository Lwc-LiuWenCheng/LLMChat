using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Chat.App.Views;

/// <summary>欢迎页：居中 logo、问候语与居中的 ComposerView。</summary>
public partial class WelcomeView : UserControl
{
    public WelcomeView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
