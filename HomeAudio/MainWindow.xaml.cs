using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HomeAudio.ViewModels;

namespace HomeAudio;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;
    }

    // ── Seek bar drag handling ──────────────────────────────────────────────

    private void SeekSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _vm.BeginSeek();
    }

    private void SeekSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _vm.EndSeek();
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.Dispose();
        base.OnClosed(e);
    }
}
