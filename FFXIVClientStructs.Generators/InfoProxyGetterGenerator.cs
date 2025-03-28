using System.Collections.Immutable;
using FFXIVClientStructs.Generators.Extensions;
using FFXIVClientStructs.Generators.Helpers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FFXIVClientStructs.Generators;

[Generator]
internal sealed class InfoProxyGetterGenerator : IIncrementalGenerator {
    private const string InfoProxyAttributeName = "FFXIVClientStructs.Attributes.InfoProxyAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context) {
        IncrementalValuesProvider<InfoProxyGetterInfo> infoProxyGetterInfos =
            context.SyntaxProvider.ForAttributeWithMetadataName(
                InfoProxyAttributeName,
                static (node, _) => node is StructDeclarationSyntax { AttributeLists.Count: > 0 },
                static (context, _) => {
                    if (context.TargetSymbol is not INamedTypeSymbol structSymbol)
                        return null;
                    StructInfo structInfo = StructInfo.FromRoslyn(structSymbol);
                    if (!context.Attributes[0].TryGetConstructorArgument(0, out uint infoProxyId))
                        return default;
                    return new InfoProxyGetterInfo(structInfo, infoProxyId);
                }).Where(static info => info is not null)!;

        context.RegisterSourceOutput(infoProxyGetterInfos,
            static (sourceContext, item) => { sourceContext.AddSource($"{item.StructInfo.FullyQualifiedMetadataName}.InstanceGetter.g.cs", RenderInstanceGetter(item)); });

        IncrementalValueProvider<ImmutableArray<InfoProxyGetterInfo>> collectedTargets = infoProxyGetterInfos.Collect();

        context.RegisterSourceOutput(collectedTargets,
            static (sourceContext, item) => { sourceContext.AddSource("InfoModule.InfoProxyGetters.g.cs", RenderInfoModuleGetters(item)); });
    }

    private static string RenderInstanceGetter(InfoProxyGetterInfo infoProxyGetterInfo) {
        using IndentedTextWriter writer = new();

        infoProxyGetterInfo.StructInfo.RenderStart(writer);

        writer.WriteLine($"public static {infoProxyGetterInfo.StructInfo.Name}* Instance()");
        using (writer.WriteBlock()) {
            writer.WriteLine("var infoModule = InfoModule.Instance();");
            writer.WriteLine($"return infoModule == null ? null : ({infoProxyGetterInfo.StructInfo.Name}*)infoModule->GetInfoProxyById((InfoProxyId){infoProxyGetterInfo.InfoProxyId});");
        }

        infoProxyGetterInfo.StructInfo.RenderEnd(writer);

        return writer.ToString();
    }

    private static string RenderInfoModuleGetters(ImmutableArray<InfoProxyGetterInfo> infoProxyGetterInfos) {
        using IndentedTextWriter writer = new();

        writer.WriteLine("// <auto-generated>");
        writer.WriteLine();

        writer.WriteLine("namespace FFXIVClientStructs.FFXIV.Client.UI.Info;");
        writer.WriteLine();

        writer.WriteLine("public unsafe partial struct InfoModule");
        using (writer.WriteBlock()) {
            foreach (InfoProxyGetterInfo infoProxyGetterInfo in infoProxyGetterInfos) {
                writer.WriteLine($"public {infoProxyGetterInfo.StructInfo.FullyQualifiedMetadataName}* Get{infoProxyGetterInfo.StructInfo.Name}() => ({infoProxyGetterInfo.StructInfo.FullyQualifiedMetadataName}*)GetInfoProxyById((InfoProxyId){infoProxyGetterInfo.InfoProxyId});");
            }
        }

        return writer.ToString();
    }

    private sealed record InfoProxyGetterInfo(StructInfo StructInfo, uint InfoProxyId);
}
