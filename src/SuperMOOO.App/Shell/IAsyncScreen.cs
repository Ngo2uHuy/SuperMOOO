namespace SuperMOOO.App.Shell;

/// <summary>Màn có khởi tạo bất đồng bộ; smoke test chờ xong rồi mới coi là "mở được".</summary>
public interface IAsyncScreen
{
    Task WhenLoadedAsync();
}
