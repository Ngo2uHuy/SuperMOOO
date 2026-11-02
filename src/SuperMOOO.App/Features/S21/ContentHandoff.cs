namespace SuperMOOO.App.Features.S21;

/// <summary>Chuyển video đã chọn ở S21 sang Soạn bài (S14): S14 lấy đúng một lần khi nạp.</summary>
[Service]
public sealed class ContentHandoff
{
    private string? _contentId;

    public void Offer(string contentId) => Interlocked.Exchange(ref _contentId, contentId);

    public string? Take() => Interlocked.Exchange(ref _contentId, null);
}
