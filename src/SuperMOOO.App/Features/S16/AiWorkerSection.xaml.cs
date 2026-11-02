using System.Windows.Controls;
using SuperMOOO.App.Controls;
using SuperMOOO.Core.Jobs;

namespace SuperMOOO.App.Features.S16;

[SettingsSection("worker", "AI worker", 10)]
public partial class AiWorkerSection : UserControl
{
    public AiWorkerSection(AiWorkerViewModel viewModel, ScreenJobsViewModel jobs)
    {
        InitializeComponent();
        DataContext = viewModel;
        ScreenJobs.Attach(jobs, JobOwner.Screen("S16"), ComponentSetup.JobType);
        jobs.JobSucceeded += _ => viewModel.RefreshStatusCommand.Execute(null);
    }
}
