namespace SuperMOOO.Core.Operations;

public enum OperationGroup { Download, Crawl, Monitor, Reports, Publishing, Media, System }

[AttributeUsage(AttributeTargets.Class)]
public sealed class OperationAttribute(string key, string label, OperationGroup group) : Attribute
{
    public string Key => key;
    public string Label => label;
    public OperationGroup Group => group;
    public string[] LegacyKeys { get; init; } = [];                      // key cũ GỘP vào operation này
}

public interface IOperationHandler<TRequest, TResult> where TRequest : class
{
    Task<TResult> HandleAsync(TRequest request, IProgress<ProgressInfo>? progress, CancellationToken ct);
}

public enum FieldKind { Text, MultiLine, Number, Bool, Choice, File, Folder, Url, UrlList, Secret, DateTime, Category }

[AttributeUsage(AttributeTargets.Property)]
public sealed class FieldAttribute(string label) : Attribute
{
    public string Label => label;
    public string? Help { get; init; }
    public string? Example { get; init; }
    public FieldKind Kind { get; init; }
    public string[] Choices { get; init; } = [];
    public string[] ChoiceLabels { get; init; } = [];
}
