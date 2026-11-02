using System.Windows.Controls;
using SuperMOOO.App.Shell;
using SuperMOOO.App.Controls;
using SuperMOOO.Core.Jobs;

namespace SuperMOOO.App.Features.S02;

[Screen("S02", "Biên tập phụ đề", ScreenRegistry.Editing, 6)]
public partial class SubtitleEditorView : UserControl, IAsyncScreen
{
    public SubtitleEditorView(SubtitleEditorViewModel viewModel, ScreenJobsViewModel jobs)
    {
        InitializeComponent();
        ScreenJobs.Attach(jobs, JobOwner.Screen("S02"));
        viewModel.Initialize();
        DataContext = viewModel;
    }

    public Task WhenLoadedAsync() => Task.CompletedTask;
}
