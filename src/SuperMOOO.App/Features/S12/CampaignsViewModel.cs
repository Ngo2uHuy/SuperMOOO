using System.Collections.ObjectModel;
using SuperMOOO.App.Controls;
using SuperMOOO.Social.Comments;
using SuperMOOO.Social.Reports;

namespace SuperMOOO.App.Features.S12;

public sealed record CampaignRow(CommentCampaign Campaign)
{
    public long Id => Campaign.Id;
    public string Title => Campaign.Title ?? Campaign.Url;
    public string Platform => Campaign.Platform ?? "";
    public int Comments => Campaign.CommentCount;
    public string Sentiment => $"+{Campaign.PositiveCount} / -{Campaign.NegativeCount} / ={Campaign.NeutralCount}";
    public int Leads => Campaign.LeadCount;
    public string Created => Campaign.CreatedAt.ToLocalTime().ToString("dd/MM HH:mm");
}

public sealed record CampaignCommentRow(CommentRecord Comment)
{
    public string Author => Comment.Author;
    public string Text => Comment.Text;
    public int Likes => Comment.Likes;
    public string Sentiment => ReportExporter.SentimentLabel(Comment.Sentiment);
    public string Lead => Comment.LeadType ?? (Comment.IsLead ? "Lead" : "");
    public string Profile => Comment.ProfileUrl ?? "";
}

/// <summary>Tab "Chiến dịch" của S12: quét bình luận, xem chiến dịch đã lưu và lead, xuất Excel/CSV.</summary>
[Service(ServiceLifetime.Transient)]
public sealed partial class CampaignsViewModel(CommentCampaignService campaigns, ReportExporter exporter, IDialogService dialogs) : ObservableObject
{
    public ObservableCollection<CampaignRow> Campaigns { get; } = [];
    public ObservableCollection<CampaignCommentRow> Comments { get; } = [];

    [ObservableProperty]
    public partial string NewUrl { get; set; } = "";

    [ObservableProperty]
    public partial int MaxComments { get; set; } = 1000;

    [ObservableProperty]
    public partial CampaignRow? Selected { get; set; }

    [ObservableProperty]
    public partial bool LeadsOnly { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? Status { get; set; }

    partial void OnSelectedChanged(CampaignRow? value) => _ = LoadCommentsAsync();

    partial void OnLeadsOnlyChanged(bool value) => _ = LoadCommentsAsync();

    public async Task LoadAsync()
    {
        Campaigns.Clear();
        foreach (var c in await campaigns.ListAsync()) Campaigns.Add(new CampaignRow(c));
    }

    [RelayCommand]
    private async Task CrawlAsync()
    {
        IsBusy = true;
        Status = "Đang quét bình luận…";
        dialogs.Info("Đã bắt đầu", "Đang quét bình luận của chiến dịch.");
        try
        {
            var result = await campaigns.CrawlAsync(NewUrl, MaxComments);
            Status = $"Đã lưu chiến dịch: {result.Comments.Count} bình luận, {result.LeadCount} lead.";
            await LoadAsync();
            Selected = Campaigns.FirstOrDefault(c => c.Id == result.CampaignId);
        }
        catch (Exception ex) when (ex is ProviderException or ArgumentException or InvalidOperationException)
        {
            Status = $"Lỗi: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task LoadCommentsAsync()
    {
        Comments.Clear();
        if (Selected is not { } row) return;
        foreach (var c in await campaigns.GetCommentsAsync(row.Id, LeadsOnly)) Comments.Add(new CampaignCommentRow(c));
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (Selected is not { } row) return;
        var path = dialogs.SaveFile("Xuất bình luận", "Excel (*.xlsx)|*.xlsx|CSV (*.csv)|*.csv", $"binh-luan-{row.Id}.xlsx");
        if (path is null) return;
        try
        {
            var comments = await campaigns.GetCommentsAsync(row.Id, LeadsOnly);
            await exporter.ExportCommentsAsync(comments, row.Platform, path);
            Status = $"Đã xuất {comments.Count} dòng.";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            dialogs.Error(ex.Message);
        }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (Selected is not { } row || !await dialogs.ConfirmAsync("Xoá chiến dịch", $"Xoá chiến dịch \"{row.Title}\" và toàn bộ bình luận?")) return;
        await campaigns.DeleteAsync(row.Id);
        Selected = null;
        await LoadAsync();
    }
}
