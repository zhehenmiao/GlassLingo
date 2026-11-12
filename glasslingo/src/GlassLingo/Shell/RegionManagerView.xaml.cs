using System.Windows;
using System.Windows.Controls;
using GlassLingo.Sessions;

namespace GlassLingo.Shell
{
    public partial class RegionManagerView : UserControl
    {
        private readonly App _app;
        internal RegionManagerView(App app)
        { InitializeComponent(); _app = app; DataContext = app.WatchRegions; }
        private void Add_Click(object sender, RoutedEventArgs e) => _app.SelectRegion(true);
        private void Pause_Click(object sender, RoutedEventArgs e)
        { if (sender is FrameworkElement { DataContext: WatchRegion region }) _app.ToggleRegionPause(region); }
        private void Visibility_Click(object sender, RoutedEventArgs e)
        { if (sender is FrameworkElement { DataContext: WatchRegion region }) _app.ToggleRegionVisibility(region); }
        private async void Remove_Click(object sender, RoutedEventArgs e)
        { if (sender is FrameworkElement { DataContext: WatchRegion region }) await _app.RemoveRegionAsync(region); }
    }
}
