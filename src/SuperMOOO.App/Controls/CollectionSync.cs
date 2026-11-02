using System.Collections.ObjectModel;

namespace SuperMOOO.App.Controls;

public static class CollectionSync
{
    /// <summary>Cập nhật danh sách tại chỗ theo khoá: chỉ thêm/xoá/di chuyển/thay dòng đổi (so theo <paramref name="signature"/>), nên lựa chọn và vị trí cuộn của ListView không bị reset mỗi lần làm mới.</summary>
    public static void Sync<T>(this ObservableCollection<T> target, IReadOnlyList<T> fresh, Func<T, string> key, Func<T, object?> signature)
    {
        var keys = fresh.Select(key).ToHashSet();
        for (var i = target.Count - 1; i >= 0; i--)
            if (!keys.Contains(key(target[i]))) target.RemoveAt(i);

        for (var i = 0; i < fresh.Count; i++)
        {
            var k = key(fresh[i]);
            var at = -1;
            for (var j = i; j < target.Count; j++)
                if (key(target[j]) == k) { at = j; break; }

            if (at < 0) target.Insert(i, fresh[i]);
            else
            {
                if (at != i) target.Move(at, i);
                if (!Equals(signature(target[i]), signature(fresh[i]))) target[i] = fresh[i];
            }
        }
    }
}
