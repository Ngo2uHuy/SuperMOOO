using SuperMOOO.App.Shell;
using SuperMOOO.Core.Operations;
using SuperMOOO.Infrastructure.Operations;

namespace SuperMOOO.App.Controls;

public sealed record PaletteItem(string Title, string Subtitle, string ScreenId, string Keywords = "")
{
    // AutoSuggestBox điền lại ô tìm bằng ToString() của mục đã chọn.
    public override string ToString() => Title;
}

/// <summary>Ctrl+K: nguồn gợi ý cho ô tìm trên thanh điều hướng (màn hình và tác vụ); chọn tác vụ thì mở màn chứa nhóm của nó.</summary>
[Service]
public sealed class CommandPalette(ScreenRegistry screens, OperationRegistry operations)
{
    private static readonly Dictionary<OperationGroup, string> GroupScreen = new()
    {
        [OperationGroup.Download] = "S09", [OperationGroup.Crawl] = "S10", [OperationGroup.Monitor] = "S11",
        [OperationGroup.Reports] = "S12", [OperationGroup.Media] = "S13", [OperationGroup.Publishing] = "S14", [OperationGroup.System] = "S16"
    };

    public IReadOnlyList<PaletteItem> Search(string query)
    {
        var items = screens.All.Select(s => new PaletteItem(s.Display, s.Category, s.Id))
            .Concat(operations.All.Select(o => new PaletteItem(o.Label, $"Tác vụ · {screens.Find(GroupScreen[o.Group])?.Title}", GroupScreen[o.Group], o.Key)));
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return items.Where(i => terms.All(t => i.Title.Contains(t, StringComparison.CurrentCultureIgnoreCase)
                                               || i.Subtitle.Contains(t, StringComparison.CurrentCultureIgnoreCase)
                                               || i.Keywords.Contains(t, StringComparison.OrdinalIgnoreCase))).Take(30).ToList();
    }
}
