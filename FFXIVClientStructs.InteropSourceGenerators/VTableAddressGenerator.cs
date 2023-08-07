﻿using System.Collections.Immutable;
using FFXIVClientStructs.InteropGenerator;
using FFXIVClientStructs.InteropSourceGenerators.Extensions;
using FFXIVClientStructs.InteropSourceGenerators.Models;
using LanguageExt;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FFXIVClientStructs.InteropSourceGenerators;

[Generator]
internal sealed class VTableAddressGenerator : IIncrementalGenerator
{
    private const string AttributeName = "FFXIVClientStructs.Interop.Attributes.VTableAddressAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<(Validation<DiagnosticInfo, StructInfo> StructInfo,
            Validation<DiagnosticInfo, StaticAddressInfo> StaticAddressInfo)> structAndStaticAddressInfos =
            context.SyntaxProvider
                .ForAttributeWithMetadataName(
                    AttributeName,
                    static (node, _) => node is StructDeclarationSyntax
                    {
                        AttributeLists.Count: > 0
                    },
                    static (context, _) =>
                    {
                        StructDeclarationSyntax structSyntax = (StructDeclarationSyntax)context.TargetNode;
                        INamedTypeSymbol symbol = (INamedTypeSymbol)context.TargetSymbol;
                        return (Struct: StructInfo.GetFromSyntax(structSyntax),
                            Info: StaticAddressInfo.GetFromRoslyn(structSyntax, symbol));
                    });

        // group by struct
        IncrementalValuesProvider<(Validation<DiagnosticInfo, StructInfo> StructInfo,
            Validation<DiagnosticInfo, Seq<StaticAddressInfo>> StaticAddressInfos)> groupedStructInfoWithStaticAddressInfos =
            structAndStaticAddressInfos.TupleGroupByValidation();

        // make sure caching is working
        IncrementalValuesProvider<Validation<DiagnosticInfo, StructWithStaticAddressInfos>> structWithStaticAddressInfos =
            groupedStructInfoWithStaticAddressInfos.Select(static (item, _) =>
                (item.StructInfo, item.StaticAddressInfos).Apply(static (si, sai) =>
                    new StructWithStaticAddressInfos(si, sai))
            );

        context.RegisterSourceOutput(structWithStaticAddressInfos, (sourceContext, item) =>
        {
            item.Match(
                Fail: diagnosticInfos =>
                {
                    diagnosticInfos.Iter(dInfo => sourceContext.ReportDiagnostic(dInfo.ToDiagnostic()));
                },
                Succ: structWithStaticAddressInfo =>
                {
                    sourceContext.AddSource(structWithStaticAddressInfo.GetFileName(), structWithStaticAddressInfo.RenderSource());
                });
        });
        
        IncrementalValueProvider<ImmutableArray<Validation<DiagnosticInfo, StructWithStaticAddressInfos>>>
            collectedStructs = structWithStaticAddressInfos.Collect();

        context.RegisterSourceOutput(collectedStructs,
            (sourceContext, structs) =>
            {
                sourceContext.AddSource("VTableAddressGenerator.Resolver.g.cs", BuildResolverSource(structs));
            });
    }
    
    private static string BuildResolverSource(
        ImmutableArray<Validation<DiagnosticInfo, StructWithStaticAddressInfos>> structInfos)
    {
        IndentedStringBuilder builder = new();

        builder.AppendLine("// <auto-generated/>");
        builder.AppendLine("using System.Runtime.CompilerServices;");
        builder.AppendLine();;

        builder.AppendLine("namespace FFXIVClientStructs.Interop;");
        builder.AppendLine();

        builder.AppendLine("public unsafe sealed partial class Resolver");
        builder.AppendLine("{");
        builder.Indent();
        builder.AppendLine("[ModuleInitializer]");
        builder.AppendLine("internal static void AddVTableAddresses()");
        builder.AppendLine("{");
        builder.Indent();

        structInfos.Iter(siv => 
            siv.IfSuccess(structInfo => structInfo.RenderResolverSource(builder)));

        builder.DecrementIndent();
        builder.AppendLine("}");
        builder.DecrementIndent();
        builder.AppendLine("}");

        return builder.ToString();
    }

    internal sealed record StaticAddressInfo(StructInfo StructInfo, SignatureInfo SignatureInfo, int Offset,
        bool IsPointer)
    {
        public static Validation<DiagnosticInfo, StaticAddressInfo> GetFromRoslyn(
            StructDeclarationSyntax structSyntax, INamedTypeSymbol namedTypeSymbol)
        {
            Validation<DiagnosticInfo, StructInfo> validStructInfo =
                StructInfo.GetFromSyntax(structSyntax);

            Option<AttributeData> staticAddressAttribute = namedTypeSymbol.GetFirstAttributeDataByTypeName(AttributeName);

            Validation<DiagnosticInfo, SignatureInfo> validSignature =
                staticAddressAttribute
                    .GetValidAttributeArgument<string>("Signature", 0, AttributeName, namedTypeSymbol)
                    .Bind(signatureString => SignatureInfo.GetValidatedSignature(signatureString, namedTypeSymbol));
            Validation<DiagnosticInfo, int> validOffset =
                staticAddressAttribute.GetValidAttributeArgument<int>("Offset", 1, AttributeName, namedTypeSymbol);
            Validation<DiagnosticInfo, bool> validIsPointer =
                staticAddressAttribute.GetValidAttributeArgument<bool>("IsPointer", 2, AttributeName, namedTypeSymbol);

            return (validStructInfo, validSignature, validOffset, validIsPointer).Apply((structInfo, signature, offset, isPointer) =>
                new StaticAddressInfo(structInfo, signature, offset, isPointer));
        }

        public void RenderAddress(IndentedStringBuilder builder, StructInfo structInfo)
        {
            builder.AppendLine(
                $"public static readonly Address VTable = new StaticAddress(\"{structInfo.Name}.VTable\", \"{SignatureInfo.Signature}\", {SignatureInfo.GetByteArrayString()}, {SignatureInfo.GetMaskArrayString()}, 0, {Offset});");
        }
        
        public void RenderPointer(IndentedStringBuilder builder, StructInfo structInfo)
        {
            builder.AppendLine($"public static nuint VTable => {structInfo.Name}.Addresses.VTable.Value;");
        }

        public void RenderAddToResolver(IndentedStringBuilder builder, StructInfo structInfo)
        {
            string hierarchy = structInfo.Hierarchy.Any() ? "." + string.Join(".", structInfo.Hierarchy) : "";
            string fullTypeName = "global::" + structInfo.Namespace + hierarchy + "." + structInfo.Name;
            builder.AppendLine($"Resolver.GetInstance.RegisterAddress({fullTypeName}.Addresses.VTable);");
        }
    }

    private sealed record StructWithStaticAddressInfos
        (StructInfo StructInfo, Seq<StaticAddressInfo> StaticAddressInfos)
    {
        public string RenderSource()
        {
            IndentedStringBuilder builder = new();

            StructInfo.RenderStart(builder);
            
            builder.AppendLine("public static partial class Addresses");
            builder.AppendLine("{");
            builder.Indent();
            StaticAddressInfos.Iter(sai => sai.RenderAddress(builder, StructInfo));
            builder.DecrementIndent();
            builder.AppendLine("}");
            builder.AppendLine();
            
            builder.AppendLine($"public partial struct {StructInfo.Name}VTable");
            builder.AppendLine("{");
            builder.AppendLine("}");
            builder.AppendLine();
            builder.AppendLine("public unsafe static partial class StaticAddressPointers");
            builder.AppendLine("{");
            builder.Indent();
            StaticAddressInfos.Iter(sai => sai.RenderPointer(builder, StructInfo));
            builder.DecrementIndent();
            builder.AppendLine("}");
            builder.AppendLine();
            builder.AppendLine($"public static {StructInfo.Name}VTable StaticVTable => *({StructInfo.Name}VTable*)StaticAddressPointers.VTable;");
            builder.AppendLine();

            StructInfo.RenderEnd(builder);

            return builder.ToString();
        }

        public string GetFileName()
        {
            return $"{StructInfo.Namespace}.{StructInfo.Name}.VTableAddresses.g.cs";
        }
        
        public void RenderResolverSource(IndentedStringBuilder builder)
        {
            StaticAddressInfos.Iter(sai => sai.RenderAddToResolver(builder, StructInfo));
        }
    }
}