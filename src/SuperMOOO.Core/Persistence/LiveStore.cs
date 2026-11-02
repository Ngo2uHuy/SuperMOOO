namespace SuperMOOO.Core.Persistence;

public interface ILiveStore
{
    Task<IReadOnlyList<LiveProduct>> ListProductsAsync(CancellationToken ct = default);
    Task UpsertProductAsync(LiveProduct product, CancellationToken ct = default);
    Task<bool> DeleteProductAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<LiveFaq>> ListFaqsAsync(string productId, CancellationToken ct = default);
    Task UpsertFaqAsync(LiveFaq faq, CancellationToken ct = default);
    Task<bool> DeleteFaqAsync(string id, CancellationToken ct = default);
}
