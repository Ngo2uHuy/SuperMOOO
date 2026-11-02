namespace SuperMOOO.Workflows.Whiteboard;

/// <summary>Các bước whiteboard báo ảnh/clip cảnh vừa xong để S18 hiện xem trước ngay khi job còn chạy.</summary>
[Service]
public sealed class WhiteboardPreviewFeed
{
    public event Action<string, string?>? SceneReady;      // (ảnh cảnh, clip cảnh nếu đã dựng)

    public void Publish(string imagePath, string? clipPath = null) => SceneReady?.Invoke(imagePath, clipPath);
}
