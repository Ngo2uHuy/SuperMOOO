using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SuperMOOO.Social.Crawlers;

namespace SuperMOOO.App.Controls;

/// <summary>
/// Thumbnail cho bảng kết quả: tải qua route mạng chung (cùng proxy với crawler, khác BitmapImage tự tải bằng proxy hệ thống),
/// giải mã ngoài luồng UI; ảnh lỗi trả null để ô để trống.
/// </summary>
[Service]
public sealed class ThumbnailLoader(IHttpClientProvider clients)
{
    private const int DecodeHeight = 112;   // ô cao 56px, gấp đôi cho màn hình DPI cao
    private const int MaxCached = 500;

    private readonly ConcurrentDictionary<string, Task<ImageSource?>> _cache = new();
    private readonly SemaphoreSlim _slots = new(6);   // bảng vài trăm dòng không mở vài trăm kết nối cùng lúc

    public Task<ImageSource?> LoadAsync(string url)
    {
        // file:// UNC từ dữ liệu crawl sẽ khiến Windows tự gửi NTLM tới máy lạ, nên chỉ nhận file cục bộ.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "file") || uri.IsUnc)
            return Task.FromResult<ImageSource?>(null);
        if (_cache.Count >= MaxCached) _cache.Clear();
        return _cache.GetOrAdd(url, _ => FetchAsync(uri));
    }

    private async Task<ImageSource?> FetchAsync(Uri uri)
    {
        await _slots.WaitAsync().ConfigureAwait(false);
        try
        {
            byte[] bytes;
            if (uri.IsFile)
            {
                bytes = await File.ReadAllBytesAsync(uri.LocalPath).ConfigureAwait(false);
            }
            else
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.TryAddWithoutValidation("User-Agent", CrawlHttp.DesktopAgent);
                using var response = await clients.Get("thumbnail").SendAsync(request, timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return Forget(uri);
                bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelHeight = DecodeHeight;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or UnauthorizedAccessException
                                       or NotSupportedException or FormatException or ArgumentException or InvalidOperationException
                                       or System.Runtime.InteropServices.COMException)
        {
            return Forget(uri);
        }
        finally
        {
            _slots.Release();
        }
    }

    // Lỗi mạng thường tạm thời: bỏ khỏi cache để lần cuộn sau thử lại.
    private ImageSource? Forget(Uri uri)
    {
        _cache.TryRemove(uri.OriginalString, out _);
        return null;
    }
}
