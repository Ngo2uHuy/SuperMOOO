using System.ComponentModel;
using System.Windows.Controls;
using SuperMOOO.App.Controls;
using SuperMOOO.App.Shell;
using SuperMOOO.Core.Jobs;

namespace SuperMOOO.App.Features.S13;

[Screen("S13", "Công cụ media", ScreenRegistry.Editing, 10)]
public partial class MediaToolsView : UserControl, IAsyncScreen
{
    private readonly DynamicOperationForm _form = new();

    public MediaToolsView(MediaToolsViewModel viewModel, ScreenJobsViewModel jobs)
    {
        InitializeComponent();
        ScreenJobs.Attach(jobs, JobOwner.Screen("S13"));
        DataContext = viewModel;
        FormHost.Content = _form;
        viewModel.PropertyChanged += OnViewModelChanged;
        RunButton.Click += async (_, _) =>
        {
            if (_form.TryGetRequest(out var request)) await ViewModel.RunAsync(request);
        };
        BindForm();
    }

    private MediaToolsViewModel ViewModel => (MediaToolsViewModel)DataContext;

    public Task WhenLoadedAsync() => Task.CompletedTask;

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MediaToolsViewModel.Selected)) BindForm();
        else if (e.PropertyName == nameof(MediaToolsViewModel.IsRunning)) RunButton.IsEnabled = !ViewModel.IsRunning;
    }

    private void BindForm()
    {
        if (ViewModel.Selected is { } d) _form.Bind(d.RequestType);
    }
}
