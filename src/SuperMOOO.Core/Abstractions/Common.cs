namespace SuperMOOO.Core.Abstractions;

public readonly record struct ProgressInfo(double Fraction, string Message);      // Fraction < 0: không xác định

public enum ProviderErrorKind { KeyMissing, KeyInvalid, Forbidden, QuotaExceeded, RateLimited, Network, Timeout, BadResponse }

public sealed class ProviderException(string providerId, ProviderErrorKind kind, string message) : Exception(message)
{
    public string ProviderId => providerId;
    public ProviderErrorKind Kind => kind;
}
