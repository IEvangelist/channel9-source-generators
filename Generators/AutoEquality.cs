﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Reflection.Metadata;
using System.Runtime.CompilerServices;
using System.Text;

namespace Generators
{
    [Generator]
    public class AutoEqualityGenerator : ISourceGenerator
    {
        private const string attributeText = @"
using System;
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false, AllowMultiple = false)]
internal sealed class AutoEqualityAttribute : Attribute
{
    public AutoEqualityAttribute()
    {
    }
}
";

        public void Initialize(GeneratorInitializationContext context)
        {
            // Register a syntax receiver that will be created for each generation pass
            context.RegisterForSyntaxNotifications(() => new SyntaxReceiver());
        }

        public void Execute(GeneratorExecutionContext context)
        {
            // add the attribute text
            context.AddSource("AutoEqualityAttribute", SourceText.From(attributeText, Encoding.UTF8));

            if (!(context.SyntaxReceiver is SyntaxReceiver receiver))
                return;

            // TODO: should verify the name
            var list = new List<INamedTypeSymbol>();
            foreach (var group in receiver.TypeDeclarationSyntaxList.GroupBy(x => x.SyntaxTree))
            {
                var semanticModel = context.Compilation.GetSemanticModel(group.Key, ignoreAccessibility: true);
                foreach (var decl in group)
                {
                    if (semanticModel.GetDeclaredSymbol(decl) is { } namedTypeSymbol)
                    {
                        list.Add(namedTypeSymbol);
                    }
                }
            }

            var builder = new StringBuilder();
            AddTypeGeneration(builder, list);
            context.AddSource("GeneratedEquality", SourceText.From(builder.ToString(), Encoding.UTF8));
        }

        private void AddTypeGeneration(StringBuilder builder, IEnumerable<INamedTypeSymbol> typeSymbols)
        {
            if (!typeSymbols.Any())
            {
                return;
            }

            var indent = new IndentUtil();
            builder.AppendLine($@"
using System;
using System.Collections.Generic;");

            // TODO: can't assume they all have the same namespace 
            var namespaceSymbol = typeSymbols.First().ContainingNamespace;
            if (!namespaceSymbol.IsGlobalNamespace)
            {
                builder.AppendLine($@"namespace {namespaceSymbol.Name}
{{");
                indent.Increase();
            }

            foreach (var typeSymbol in typeSymbols)
            {
                AddTypeGeneration(builder, indent, typeSymbol);
            }

            if (!namespaceSymbol.IsGlobalNamespace)
            {
                indent.Decrease();
                builder.AppendLine("}");
            }
        }

        private void AddTypeGeneration(StringBuilder builder, IndentUtil indent, INamedTypeSymbol typeSymbol)
        {
            var kind = typeSymbol.TypeKind == TypeKind.Class ? "class" : "struct";
            builder.AppendLine($@"
{indent.Value}partial {kind} {typeSymbol.Name} : IEquatable<{typeSymbol.Name}>
{indent.Value}{{
{indent.Value2}public override bool Equals(object obj) => obj is {typeSymbol.Name} other && Equals(other);");


            AddOperatorEquals();

            var memberInfoList = GetMemberInfo();
            using var marker = indent.Increase();

            AddEquals();
            AddGetHashCode();

            marker.Revert();
            builder.AppendLine($@"{indent.Value}}}");

            void AddOperatorEquals()
            {
                using var _ = indent.Increase();

                var prefix = "";
                if (!typeSymbol.IsValueType)
                {
                    prefix = "left is object && ";
                }

                builder.AppendLine($"{indent.Value}public static bool operator==({typeSymbol.Name} left, {typeSymbol.Name} right) => {prefix}left.Equals(right);");
                builder.AppendLine($"{indent.Value}public static bool operator!=({typeSymbol.Name} left, {typeSymbol.Name} right) => !(left == right);");
            }

            void AddEquals()
            {
                builder.AppendLine($@"
{indent.Value}public bool Equals({typeSymbol.Name} other)
{indent.Value}{{
{indent.Value2}return");

                using var marker = indent.Increase(2);

                for (var i = 0; i < memberInfoList.Count; i++)
                {
                    var current = memberInfoList[i];
                    if (current.UseOperator)
                    {
                        builder.Append($"{indent.Value}{current.Name} == other.{current.Name}");
                    }
                    else
                    {
                        builder.Append($"{indent.Value}EqualityComparer<{current.TypeName}>.Default.Equals({current.Name}, other.{current.Name})");
                    }

                    if (i + 1 < memberInfoList.Count)
                    {
                        builder.Append(" &&");
                    }
                    else
                    {
                        builder.Append(";");
                    }
                    builder.AppendLine();
                }

                marker.Revert();
                builder.AppendLine($"{indent.Value}}}");
            }

            void AddGetHashCode()
            {
                builder.AppendLine($@"
{indent.Value}public override int GetHashCode()
{indent.Value}{{
{indent.Value2}return HashCode.Combine(");

                using var marker = indent.Increase(2);

                // TODO: handle more than eight fields
                for (var i = 0; i < memberInfoList.Count; i++)
                {
                    var current = memberInfoList[i];
                    builder.Append($"{indent.Value}{current.Name}");
                    if (i + 1 < memberInfoList.Count)
                    {
                        builder.AppendLine(",");
                    }
                    else
                    {
                        builder.AppendLine(");");
                    }
                }

                marker.Revert();
                builder.AppendLine($"{indent.Value}}}");
            }

            List<MemberInfo> GetMemberInfo()
            {
                var list = new List<MemberInfo>();
                foreach (var symbol in typeSymbol.GetMembers())
                {
                    switch (symbol)
                    {
                        case IFieldSymbol fieldSymbol when fieldSymbol.Type is object:
                            var useOperator = fieldSymbol.Type.SpecialType is
                                SpecialType.System_Int16 or
                                SpecialType.System_Int32 or
                                SpecialType.System_Int64 or
                                SpecialType.System_UInt16 or
                                SpecialType.System_UInt32 or
                                SpecialType.System_UInt64 or
                                SpecialType.System_String or
                                SpecialType.System_IntPtr or
                                SpecialType.System_UIntPtr;
                            list.Add(new MemberInfo(fieldSymbol.Name, fieldSymbol.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), useOperator));
                            break;
                        default:
                            break;
                    }
                }

                return list;
            }
        }

        private record MemberInfo(string Name, string TypeName, bool UseOperator);

        private class IndentUtil
        {
            public class Marker : IDisposable
            {
                private readonly IndentUtil _util;
                private int _count;

                public Marker(IndentUtil indentUtil, int count)
                {
                    _util = indentUtil;
                    _count = count;
                }

                public void Revert()
                {
                    Dispose();
                    _count = 0;
                }

                public void Dispose()
                {
                    _util.Decrease(_count);
                }
            }

            public int Depth { get; private set; }
            public string UnitValue { get; } = new string(' ', 4);
            public string Value { get; private set; } = "";
            public string Value2 { get; private set; } = "";

            public IndentUtil()
            {
                Update();
            }

            public Marker Increase(int count = 1)
            {
                Depth += count;
                Update();
                return new Marker(this, count);
            }

            public void Decrease(int count = 1)
            {
                Depth -= count;
                Update();
            }

            private void Update()
            {
                Value = new string(' ', Depth * 4);
                Value2 = new string(' ', (Depth + 1) * 4);
            }
        }

        /// <summary>
        /// Created on demand before each generation pass
        /// </summary>
        internal sealed class SyntaxReceiver : ISyntaxReceiver
        {
            internal List<TypeDeclarationSyntax> TypeDeclarationSyntaxList { get; } = new();

            public void OnVisitSyntaxNode(SyntaxNode syntaxNode)
            {
                // TODO: could do a quick filter on whether the attribute has the right name
                if (syntaxNode is TypeDeclarationSyntax typeDeclarationSyntax
                    && typeDeclarationSyntax.AttributeLists.Count > 0)
                {
                    TypeDeclarationSyntaxList.Add(typeDeclarationSyntax);
                }
            }
        }
    }
}
