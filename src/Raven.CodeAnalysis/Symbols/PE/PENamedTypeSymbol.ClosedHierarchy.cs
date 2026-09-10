using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Raven.CodeAnalysis.Symbols;

internal partial class PENamedTypeSymbol
{
    private const string ClosedHierarchyAttributeNamespace =
        "System.Runtime.CompilerServices";
    private const string ClosedHierarchyAttributeName =
        "ClosedHierarchyAttribute";

    private void EnsureClosedHierarchyMetadata()
    {
        if (_isSealedHierarchy is not null)
            return;

        var permittedTypes = ImmutableArray.CreateBuilder<INamedTypeSymbol>();
        var isSealedHierarchy = TryReadClosedHierarchyTypeNames(out var permittedTypeNames);

        if (isSealedHierarchy)
        {
            foreach (var permittedTypeName in permittedTypeNames)
            {
                var metadataName = GetUnqualifiedMetadataName(permittedTypeName);
                if (_typeInfo.Assembly.GetType(
                        metadataName,
                        throwOnError: false,
                        ignoreCase: false) is { } type &&
                    _reflectionTypeLoader.ResolveType(type) is INamedTypeSymbol permittedType)
                {
                    permittedTypes.Add(permittedType);
                }
            }
        }

        _permittedDirectSubtypes = permittedTypes
            .Distinct(SymbolEqualityComparer.Default)
            .OfType<INamedTypeSymbol>()
            .ToImmutableArray();
        _isSealedHierarchy = isSealedHierarchy;
    }

    private bool TryReadClosedHierarchyTypeNames(
        out ImmutableArray<string> permittedTypeNames)
    {
        permittedTypeNames = ImmutableArray<string>.Empty;
        if (ContainingAssembly is not PEAssemblySymbol peAssembly)
        {
            return false;
        }

        try
        {
            var assemblyPath = peAssembly.AssemblyPath;
            if (string.IsNullOrWhiteSpace(assemblyPath))
                assemblyPath = peAssembly.GetAssemblyInfo().Location;
            if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
                return false;

            using var stream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(stream);
            if (!peReader.HasMetadata)
                return false;

            var reader = peReader.GetMetadataReader();
            var typeHandle = MetadataTokens.EntityHandle(_typeInfo.MetadataToken);
            if (typeHandle.Kind != HandleKind.TypeDefinition)
                return false;

            var typeDefinition = reader.GetTypeDefinition(
                (TypeDefinitionHandle)typeHandle);
            foreach (var attributeHandle in typeDefinition.GetCustomAttributes())
            {
                var attribute = reader.GetCustomAttribute(attributeHandle);
                if (IsClosedHierarchyAttributeConstructor(reader, attribute.Constructor, "IsClosedTypeAttribute"))
                {
                    // As in C#, discover the authoritative family from direct bases
                    // in the declaring module. DerivedTypes is the runtime JSON hint.
                    var root = _typeInfo.IsGenericType
                        ? _typeInfo.GetGenericTypeDefinition()
                        : _typeInfo.AsType();
                    permittedTypeNames = _typeInfo.Module.GetTypes()
                        .Where(type => type.BaseType is { } baseType &&
                            (baseType.IsGenericType ? baseType.GetGenericTypeDefinition() : baseType) == root)
                        .Select(type => type.FullName!)
                        .ToImmutableArray();
                    return true;
                }

                if (!IsClosedHierarchyAttributeConstructor(reader, attribute.Constructor, ClosedHierarchyAttributeName))
                    continue;

                var valueReader = reader.GetBlobReader(attribute.Value);
                if (valueReader.ReadUInt16() != 1)
                    return true;

                var count = valueReader.ReadUInt32();
                if (count == uint.MaxValue)
                    return true;
                if (count > (uint)valueReader.RemainingBytes)
                    return true;

                var builder = ImmutableArray.CreateBuilder<string>(
                    checked((int)count));
                for (var index = 0; index < count; index++)
                {
                    var typeName = valueReader.ReadSerializedString();
                    if (!string.IsNullOrWhiteSpace(typeName))
                        builder.Add(typeName);
                }

                permittedTypeNames = builder.ToImmutable();
                return true;
            }
        }
        catch (Exception exception) when (
            exception is BadImageFormatException or
                IOException or
                InvalidOperationException or
                NotSupportedException or
                UnauthorizedAccessException)
        {
        }

        return false;
    }

    private static bool IsClosedHierarchyAttributeConstructor(
        MetadataReader reader,
        EntityHandle constructor,
        string attributeName)
    {
        var attributeType = constructor.Kind switch
        {
            HandleKind.MemberReference =>
                reader.GetMemberReference((MemberReferenceHandle)constructor).Parent,
            HandleKind.MethodDefinition =>
                reader.GetMethodDefinition((MethodDefinitionHandle)constructor)
                    .GetDeclaringType(),
            _ => default
        };

        return attributeType.Kind switch
        {
            HandleKind.TypeReference => IsClosedHierarchyAttribute(
                reader,
                reader.GetTypeReference((TypeReferenceHandle)attributeType), attributeName),
            HandleKind.TypeDefinition => IsClosedHierarchyAttribute(
                reader,
                reader.GetTypeDefinition((TypeDefinitionHandle)attributeType), attributeName),
            _ => false
        };
    }

    private static bool IsClosedHierarchyAttribute(
        MetadataReader reader,
        TypeReference type,
        string attributeName)
        => reader.StringComparer.Equals(
               type.Namespace,
               ClosedHierarchyAttributeNamespace) &&
           reader.StringComparer.Equals(type.Name, attributeName);

    private static bool IsClosedHierarchyAttribute(
        MetadataReader reader,
        TypeDefinition type,
        string attributeName)
        => reader.StringComparer.Equals(
               type.Namespace,
               ClosedHierarchyAttributeNamespace) &&
           reader.StringComparer.Equals(type.Name, attributeName);

    private static string GetUnqualifiedMetadataName(string serializedTypeName)
    {
        var comma = serializedTypeName.IndexOf(',');
        return comma < 0
            ? serializedTypeName
            : serializedTypeName[..comma].Trim();
    }
}
