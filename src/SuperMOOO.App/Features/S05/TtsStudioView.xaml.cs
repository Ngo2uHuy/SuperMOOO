using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using SuperMOOO.App.Shell;
using SuperMOOO.App.Controls;
using SuperMOOO.Core.Jobs;

namespace SuperMOOO.App.Features.S05;

[Screen("S05", "Phòng thu giọng", ScreenRegistry.Creative, 4)]
public partial class TtsStudioView : UserControl, IAsyncScreen
{
    private readonly IServiceProvider _services;
    private Task _load = Task.CompletedTask;

    public TtsStudioView(TtsStudioViewModel viewModel, IServiceProvider services, ScreenJobsViewModel jobs)
    {
        InitializeComponent();
        ScreenJobs.Attach(jobs, JobOwner.Screen("S05"));
        jobs.ResultSelected += (_, path) => viewModel.ShowResult(path);
        DataContext = viewModel;
        _services = services;
        CloneButton.Click += (_, _) => ShowDialog<VoiceCloneDialog>();
        ServerButton.Click += (_, _) => ShowDialog<NativeTtsServerDialog>();
        viewModel.PropertyChanged += OnChanged;
        Loaded += OnLoaded;
    }

    public Task WhenLoadedAsync() => _load;

    private TtsStudioViewModel ViewModel => (TtsStudioViewModel)DataContext;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        _load = ViewModel.LoadAsync();
        await _load;
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        // RVC là plugin tuỳ chọn: chỉ hiện khi worker báo có.
        if (e.PropertyName == nameof(TtsStudioViewModel.RvcAvailable))
            RvcPanel.Visibility = ViewModel.RvcAvailable ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowDialog<T>() where T : Window
    {
        var dialog = (T)_services.GetService(typeof(T))!;
        dialog.Owner = Window.GetWindow(this);
        dialog.ShowDialog();
        // Giọng clone mới hoặc model vừa nạp xuất hiện trong danh sách.
        ViewModel.ReloadVoicesCommand.Execute(null);
    }
}
