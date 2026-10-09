using System.IO;
using System.Windows;
using gcviewer.ViewModels;

namespace gcviewer
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly MainViewModel viewModel = new();

        public MainWindow()
        {
            InitializeComponent();
            DataContext = viewModel;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            var args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && File.Exists(args[1]))
            {
                await viewModel.OpenFileAsync(args[1]);
            }
        }

        private void OnWindowDragOver(object sender, DragEventArgs e)
        {
            var canDrop = viewModel.IsNotBusy && GetDroppedFile(e) is not null;
            e.Effects = canDrop ? DragDropEffects.Copy : DragDropEffects.None;
            DropOverlay.Visibility = canDrop ? Visibility.Visible : Visibility.Collapsed;
            e.Handled = true;
        }

        private void OnOverlayDragLeave(object sender, DragEventArgs e)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }

        private async void OnWindowDrop(object sender, DragEventArgs e)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            var path = GetDroppedFile(e);
            if (path is null)
            {
                return;
            }

            e.Handled = true;
            await viewModel.OpenFileAsync(path);
        }

        private static string? GetDroppedFile(DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetData(DataFormats.FileDrop) is not string[] files)
            {
                return null;
            }

            return files.Length == 1 && File.Exists(files[0]) ? files[0] : null;
        }
    }
}
