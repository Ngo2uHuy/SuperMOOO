using System.ComponentModel.DataAnnotations;
using System.Reflection;
using SuperMOOO.Infrastructure.Conventions;

namespace SuperMOOO.Infrastructure.Operations;

public sealed record OperationField(string Name, string Label, FieldKind Kind, string? Help, string? Example, bool Required,
    Type ClrType, IReadOnlyList<string> Choices, IReadOnlyList<string>? ChoiceLabels = null);

public sealed record OperationDescriptor(string Key, string Label, OperationGroup Group, IReadOnlyList<string> LegacyKeys,
    Type HandlerType, Type RequestType, Type ResultType, IReadOnlyList<OperationField> Fields)
{
    // Danh sách UI (UI Automation, đọc màn hình) lấy tên mục từ ToString().
    public override string ToString() => Label;
}

/// <summary>Danh sách operation lấy từ code ([Operation]) — nguồn duy nhất cho form động và bảng parity.</summary>
[Service]
public sealed class OperationRegistry
{
    private readonly Dictionary<string, OperationDescriptor> _byKey = new(StringComparer.OrdinalIgnoreCase);

    public OperationRegistry(IReadOnlyList<Assembly>? assemblies = null)
        : this((assemblies ?? AssemblyCatalog.LoadAppAssemblies()).SelectMany(a => a.GetTypes()))
    {
    }

    private OperationRegistry(IEnumerable<Type> types)
    {
        foreach (var type in types.Where(t => t is { IsClass: true, IsAbstract: false } && t.IsDefined(typeof(OperationAttribute))))
        {
            var attr = type.GetCustomAttribute<OperationAttribute>()!;
            var contract = type.GetInterfaces()
                .SingleOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IOperationHandler<,>))
                ?? throw new InvalidOperationException($"{type.Name} có [Operation] nhưng không cài IOperationHandler<,>.");
            var request = contract.GetGenericArguments()[0];
            var descriptor = new OperationDescriptor(attr.Key, attr.Label, attr.Group, attr.LegacyKeys, type, request,
                contract.GetGenericArguments()[1], FieldReflector.Describe(request));

            foreach (var key in attr.LegacyKeys.Prepend(attr.Key))
                if (!_byKey.TryAdd(key, descriptor))
                    throw new InvalidOperationException($"Trùng operation key '{key}' ({_byKey[key].HandlerType.Name} và {type.Name}).");
        }

        All = _byKey.Values.Distinct().OrderBy(d => d.Group).ThenBy(d => d.Key).ToList();
    }

    public IReadOnlyList<OperationDescriptor> All { get; }

    /// <summary>Tìm theo key mới hoặc key cũ đã gộp vào operation.</summary>
    public OperationDescriptor? Find(string key) => _byKey.GetValueOrDefault(key);
}

public static class FieldReflector
{
    /// <summary>Các property có [Field] của request, kèm metadata cho form động; Kind mặc định (Text) được suy ra từ kiểu CLR.</summary>
    public static IReadOnlyList<OperationField> Describe(Type request) =>
        request.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => (Property: p, Attr: p.GetCustomAttribute<FieldAttribute>()))
            .Where(x => x.Attr is not null)
            .Select(x =>
            {
                var type = Nullable.GetUnderlyingType(x.Property.PropertyType) ?? x.Property.PropertyType;
                return new OperationField(x.Property.Name, x.Attr!.Label, x.Attr.Kind != FieldKind.Text ? x.Attr.Kind : Infer(type),
                    x.Attr.Help, x.Attr.Example, x.Property.IsDefined(typeof(RequiredAttribute)), type,
                    x.Attr.Choices.Length > 0 ? x.Attr.Choices : type.IsEnum ? Enum.GetNames(type) : [], x.Attr.ChoiceLabels);
            })
            .ToList();

    private static FieldKind Infer(Type type) =>
        type == typeof(bool) ? FieldKind.Bool
        : type.IsEnum ? FieldKind.Choice
        : type == typeof(DateTime) || type == typeof(DateTimeOffset) ? FieldKind.DateTime
        : type != typeof(string) && type.IsAssignableTo(typeof(System.Collections.IEnumerable)) ? FieldKind.MultiLine
        : Type.GetTypeCode(type) is >= TypeCode.SByte and <= TypeCode.Decimal ? FieldKind.Number
        : FieldKind.Text;
}
