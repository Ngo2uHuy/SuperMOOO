using SuperMOOO.Core.Operations;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SuperMOOO.App.Controls;
using SuperMOOO.App.Shell;
using SuperMOOO.Core.Jobs;
using Wpf.Ui.Controls;

namespace SuperMOOO.App.Features.S10;

/// <summary>S10: mọi operation nhóm Crawl (quét kênh, tìm kiếm, khám phá, bình luận) qua form động + bảng kết quả.</summary>
[Screen("S10", "Crawler", ScreenRegistry.Workflow, 14)]
public sealed class CrawlView : UserControl, IAsyncScreen
{
    public CrawlView(OperationRunnerPanel panel, ScreenJobsViewModel jobs)
    {
        panel.Initialize(OperationGroup.Crawl, "crawl.crawl_channel");
        panel.Grid.ViewModel.JobsOwner = JobOwner.Screen("S10");
        var screenJobs = new ScreenJobsPanel { Margin = new Thickness(0, 12, 0, 0) };
        screenJobs.Attach(jobs, JobOwner.Screen("S10"));

        var run = new Wpf.Ui.Controls.Button { Content = "Chạy tác vụ", Appearance = ControlAppearance.Primary, Icon = new SymbolIcon(SymbolRegular.Play24) };
        run.SetBinding(System.Windows.Controls.Primitives.ButtonBase.CommandProperty, new Binding(nameof(OperationRunnerViewModel.RunCommand)) { Source = panel.ViewModel });
        var header = new ScreenHeader
        {
            Title = "Crawler",
            Description = "Quét kênh, tìm kiếm, lấy bình luận; kết quả xuất Excel hoặc đưa thẳng vào hàng đợi tải.",
            Content = run
        };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(screenJobs, Dock.Bottom);
        var root = new DockPanel { Children = { header, screenJobs, panel } };
        root.SetResourceReference(MarginProperty, "PagePadding");
        Content = root;
    }

    public Task WhenLoadedAsync() => Task.CompletedTask;
}
