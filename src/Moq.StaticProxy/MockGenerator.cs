﻿using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Avatars;
using Avatars.CodeAnalysis;
using Avatars.Processors;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Moq.Processors;
using Moq.Sdk;

namespace Moq
{
    /// <summary>
    /// Generates mocks by inspecting the current compilation for 
    /// invocations to methods annotated with [MockGenerator].
    /// </summary>
    [Generator]
    public class MockGenerator : ISourceGenerator
    {
        readonly AvatarGenerator generator;

        public MockGenerator()
        {
            generator = new AvatarGenerator()
                .WithNamingConvention(new MockNamingConvention())
                .WithGeneratorAttribute(typeof(MockGeneratorAttribute))
                .WithProcessor(new DefaultImports(typeof(IMocked).Namespace, typeof(LazyInitializer).Namespace))
                .WithProcessor(new CSharpMocked())
                .WithSyntaxReceiver(() => new RecursiveMockCandidatesReceiver());
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Execute(GeneratorExecutionContext context)
        {
            context.AnalyzerConfigOptions.CheckDebugger(nameof(MockGenerator));

            if (context.AnalyzerConfigOptions.GlobalOptions.TryGetValue("build_property.MoqAnalyzerDir", out var analyerDir))
                DependencyResolver.AddSearchPath(analyerDir);

            generator.Execute(context);
        }

        public void Initialize(GeneratorInitializationContext context) => generator.Initialize(context);

        class RecursiveMockCandidatesReceiver : IAvatarCandidatesReceiver
        {
            readonly List<SyntaxNode> nodes = new();

            public IEnumerable<INamedTypeSymbol[]> GetCandidates(ProcessorContext context)
            {
                var generatorAttr = context.GeneratorAttribute;
                var moqmodule = context.Compilation.GetTypeByMetadataName("Moq.IMoq")!.ContainingModule;
                var sdkmodule = context.Compilation.GetTypeByMetadataName(typeof(IMock).FullName!)!.ContainingModule;

                foreach (var node in nodes)
                {
                    var semantic = context.Compilation.GetSemanticModel(node.SyntaxTree);
                    if (semantic == null)
                        break;

                    var flow = semantic.AnalyzeDataFlow(node);

                    // There are two possible flows:
                    // mock.Prop.Method().Returns(...): this is recursive "normal" flow In
                    // mock.Setup(x => x.Prop.Method()).Returns(...): 
                    //      this is a recursive "read outside" flow: the flow into the recursive expression
                    //      is actually the lambda parameter, not useful. But the "read outside" is the actual 
                    //      mock variable where the Setup is being performed, which is what we need.
                    bool IsMockFlow(ImmutableArray<ISymbol> data) =>
                        data.Length == 1 &&
                        data[0].DeclaringSyntaxReferences.Length == 1 &&
                        data[0].DeclaringSyntaxReferences[0].GetSyntax(context.CancellationToken) is VariableDeclaratorSyntax variable &&
                        variable.Initializer?.Value is InvocationExpressionSyntax create &&
                        semantic!.GetSymbolInfo(create, context.CancellationToken).Symbol is IMethodSymbol method &&
                        method.GetAttributes().Any(attr => SymbolEqualityComparer.Default.Equals(attr.AttributeClass, generatorAttr));

                    // Detect if the variable being accessed was initialized from a generator method call
                    if (!IsMockFlow(flow.DataFlowsIn) && !IsMockFlow(flow.ReadOutside))
                        continue;

                    var symbol = semantic.GetSymbolInfo(node);
                    // TODO: see if we need to consider symbol.CandidateSymbols too
                    if (symbol.Symbol == null ||
                        SymbolEqualityComparer.Default.Equals(symbol.Symbol.ContainingModule, moqmodule) ||
                        SymbolEqualityComparer.Default.Equals(symbol.Symbol.ContainingModule, sdkmodule))
                        continue;

                    // We only process recursive property and method accesses
                    if (symbol.Symbol.Kind != SymbolKind.Property &&
                        symbol.Symbol.Kind != SymbolKind.Method)
                        continue;

                    var methodSymbol = symbol.Symbol as IMethodSymbol;
                    var propertySymbol = symbol.Symbol as IPropertySymbol;

                    // Extension methods are not considered for mocking
                    if (methodSymbol?.IsExtensionMethod == true ||
                        // void methods can't result in a recursive mock either
                        methodSymbol?.ReturnsVoid == true)
                        continue;

                    var type = (methodSymbol?.ReturnType ?? propertySymbol?.Type) as INamedTypeSymbol;
                    if (type != null && type.CanBeIntercepted() == false)
                        continue;

                    yield return new[] { type! };
                }
            }

            public void OnVisitSyntaxNode(SyntaxNode syntaxNode)
            {
                if (syntaxNode.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.SimpleMemberAccessExpression) ||
                    syntaxNode.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.InvocationExpression))
                    nodes.Add(syntaxNode);
            }
        }
    }
}