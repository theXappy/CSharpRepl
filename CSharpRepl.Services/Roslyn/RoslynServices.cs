﻿// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CSharpRepl.Services.Completion;
using CSharpRepl.Services.Disassembly;
using CSharpRepl.Services.Extensions;
using CSharpRepl.Services.Logging;
using CSharpRepl.Services.Roslyn.Formatting;
using CSharpRepl.Services.Roslyn.References;
using CSharpRepl.Services.Roslyn.Scripting;
using CSharpRepl.Services.SymbolExploration;
using CSharpRepl.Services.SyntaxHighlighting;
using CSharpRepl.Services.Theming;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Caching.Memory;
using PrettyPrompt;
using PrettyPrompt.Consoles;
using PrettyPrompt.Highlighting;
using Spectre.Console;
using Spectre.Console.Rendering;
using PrettyPromptTextSpan = PrettyPrompt.Documents.TextSpan;

namespace CSharpRepl.Services.Roslyn;

/// <summary>
/// The main entry point of all services. This is a facade for other services that manages their startup and initialization.
/// It also ensures two different areas of the Roslyn API, the Scripting and Workspace APIs, remain in sync.
/// </summary>
public sealed partial class RoslynServices
{
    private readonly SyntaxHighlighter highlighter;
    private readonly IConsoleEx console;
    private readonly ITraceLogger logger;
    private readonly SemaphoreSlim semaphore = new(1);
    private readonly IPromptCallbacks defaultPromptCallbacks = new PromptCallbacks();
    private readonly ThreadLocal<OverloadItemGenerator> overloadItemGenerator;
    private readonly CSharpParseOptions parseOptions = CSharpParseOptions.Default.WithKind(SourceCodeKind.Script).WithLanguageVersion(LanguageVersion.Latest);
    private ScriptRunner? scriptRunner;
    private WorkspaceManager? workspaceManager;
    private Disassembler? disassembler;
    private PrettyPrinter prettyPrinter;
    private SymbolExplorer? symbolExplorer;
    private AutoCompleteService? autocompleteService;
    private AssemblyReferenceService? referenceService;
    private CSharpCompilationOptions? compilationOptions;

    // when this Initialization task successfully completes, all the above members will not be null.
    [MemberNotNull(
        nameof(scriptRunner), nameof(workspaceManager), nameof(disassembler),
        nameof(prettyPrinter), nameof(symbolExplorer), nameof(autocompleteService),
        nameof(referenceService), nameof(compilationOptions))]
    private Task Initialization { get; }

    internal event Action<string>? EvaluatingInput;

    public RoslynServices(IConsoleEx console, Configuration config, ITraceLogger logger)
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        this.console = console;
        this.logger = logger;
        this.highlighter = new SyntaxHighlighter(cache, config.Theme);
        this.overloadItemGenerator = new(() => new(highlighter));

        // initialization of roslyn and all dependent services is slow! do it asynchronously so we don't increase startup time.
        this.Initialization = Task.Run(() =>
        {
            logger.Log("Starting background initialization");

            this.referenceService = new AssemblyReferenceService(config, parseOptions, logger);

            this.compilationOptions = new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                usings: referenceService.Usings.Select(u => u.Name?.ToString()).WhereNotNull(),
                allowUnsafe: true,
                sourceReferenceResolver: new SourceFileResolver(new[] { Environment.CurrentDirectory }, Environment.CurrentDirectory)
            );

            // the script runner is used to actually execute the scripts, and the workspace manager
            // is updated alongside. The workspace is a datamodel used in "editor services" like
            // syntax highlighting, autocompletion, and roslyn symbol queries.
            this.workspaceManager = new WorkspaceManager(compilationOptions, referenceService, logger);
            this.scriptRunner = new ScriptRunner(workspaceManager, parseOptions, compilationOptions, referenceService, console, config);

            this.disassembler = new Disassembler(parseOptions, compilationOptions, referenceService, scriptRunner);
            this.prettyPrinter = new PrettyPrinter(console, highlighter, config);
            this.symbolExplorer = new SymbolExplorer(referenceService, scriptRunner);
            this.autocompleteService = new AutoCompleteService(highlighter, cache, config, this);
            logger.Log("Background initialization complete");
        });

        Initialization.ContinueWith(task => console.WriteErrorLine(task.Exception?.Message ?? "Unknown error"), TaskContinuationOptions.OnlyOnFaulted);
    }

    public async Task<EvaluationResult> EvaluateAsync(string input, string[]? args = null, CancellationToken cancellationToken = default)
    {
        await Initialization.ConfigureAwait(false);

        try
        {
            //each RunCompilation (modifies script state) and UpdateCurrentDocument (changes CurrentDocument) cannot be run concurrently
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

            input = await InterceptInputWhenRefStructReturnNeedsToBeHandled(input.Trim(), cancellationToken).ConfigureAwait(false);
            EvaluatingInput?.Invoke(input);

            var result = await scriptRunner
                .RunCompilation(input, args, cancellationToken)
                .ConfigureAwait(false);

            if (result is EvaluationResult.Success success)
            {
                // update our final document text, and add a new, empty project that can be
                // used for future evaluations (whether evaluation, syntax highlighting, or completion)
                workspaceManager.UpdateCurrentDocument(success);
            }

            return result;
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async Task<IRenderable> PrettyPrintAsync(object? obj, Level level)
    {
        await Initialization.ConfigureAwait(false);

        var formattedObject = prettyPrinter.FormatObject(obj, level);

        if (level == Level.FirstDetailed)
        {
            var tree = new Tree(formattedObject.Renderable);

            var formattedMembers = formattedObject.FormatMembers(
                prettyPrinter,
                level: level,
                includeNonPublic: false);

            int nodeCount = 0;
            int maxNodes = LengthLimiting.GetTreeMaxItems(level, console.Profile);
            foreach (var formattedMember in formattedMembers)
            {
                if (nodeCount >= maxNodes)
                {
                    tree.AddNode(new Paragraph("..."));
                    break;
                }
                tree.AddNode(formattedMember.Renderable);
                nodeCount++;
            }

            return tree;
        }

        return formattedObject.Renderable;
    }

    public async Task<StyledString> PrettyPrintAsync(Exception obj, Level level)
    {
        await Initialization.ConfigureAwait(false);
        return prettyPrinter.FormatException(obj, level);
    }

    public async Task<IReadOnlyList<string>> GetPreviousSubmissionsAsync()
    {
        await Initialization.ConfigureAwait(false);
        var texts = await Task.WhenAll(
            workspaceManager
                .GetPreviousDocuments()
                .Skip(1) // skip warmup
                .Select(d => d.GetTextAsync())
        );
        return texts
            .Select(t => t.ToString().Replace("\r\n", "\n"))
            .Where(text => !string.IsNullOrEmpty(text))
            .ToList();
    }

    public async Task<IReadOnlyCollection<CompletionItemWithDescription>> CompleteAsync(string text, int caret)
    {
        if (!Initialization.IsCompleted)
            return [];

        var document = workspaceManager.CurrentDocument.WithText(SourceText.From(text));
        return await autocompleteService.Complete(document, text, caret).ConfigureAwait(false);
    }

    public async Task<SymbolResult> GetSymbolAtIndexAsync(string text, int caret)
    {
        await Initialization.ConfigureAwait(false);
        return await symbolExplorer.LookupSymbolAtPosition(text, caret);
    }

    public AnsiColor ToColor(string classification) =>
        highlighter.GetAnsiColor(classification);

    public async Task<IReadOnlyCollection<HighlightedSpan>> SyntaxHighlightAsync(string text)
    {
        if (!Initialization.IsCompleted)
            return [];

        var document = workspaceManager.CurrentDocument.WithText(SourceText.From(text));
        var highlighted = await highlighter.HighlightAsync(document);

        return highlighted;
    }

    public async Task<bool> IsTextCompleteStatementAsync(string text)
    {
        if (!Initialization.IsCompleted)
            return true;

        var document = workspaceManager.CurrentDocument.WithText(SourceText.From(text));
        var root = await document.GetSyntaxRootAsync().ConfigureAwait(false);
        return root is null || SyntaxFactory.IsCompleteSubmission(root.SyntaxTree); // if something's wrong and we can't get the syntax tree, we don't want to prevent evaluation.
    }

    public async Task<PrettyPromptTextSpan> GetSpanToReplaceByCompletionAsync(string text, int caret, CancellationToken cancellationToken)
    {
        await Initialization.ConfigureAwait(false);

        var sourceText = SourceText.From(text);
        var document = workspaceManager.CurrentDocument.WithText(sourceText);
        var completionService = CompletionService.GetService(document);
        if (completionService is null)
        {
            //fallback to default PrettyPrompt implementation
            return await defaultPromptCallbacks.GetSpanToReplaceByCompletionAsync(text, caret, cancellationToken);
        }

        var span = completionService.GetDefaultCompletionListSpan(sourceText, caret);
        return new PrettyPromptTextSpan(span.Start, span.Length);
    }

    public async Task<bool> ShouldOpenCompletionWindowAsync(string text, int caret, KeyPress keyPress, CancellationToken cancellationToken)
    {
        var keyChar = keyPress.ConsoleKeyInfo.KeyChar;
        var keyModifiers = keyPress.ConsoleKeyInfo.Modifiers;
        if (keyChar is '\0' or ' ' or '{' or '(' or '[' or '<' or ':' or '"' ||
            (keyModifiers & ConsoleModifiers.Control) != 0 ||
            (keyModifiers & ConsoleModifiers.Alt) != 0)
        {
            return false;
        }

        await Initialization.ConfigureAwait(false);

        var sourceText = SourceText.From(text);
        var document = workspaceManager.CurrentDocument.WithText(sourceText);
        var completionService = CompletionService.GetService(document);
        if (completionService is null)
        {
            //fallback to default PrettyPrompt implementation
            return await defaultPromptCallbacks.ShouldOpenCompletionWindowAsync(text, caret, keyPress, cancellationToken);
        }

        var trigger = CompletionTrigger.CreateInsertionTrigger(keyChar);
        return completionService.ShouldTriggerCompletion(sourceText, caret, trigger);
    }

    public async Task<bool> ConfirmCompletionCommit(string text, int caret, KeyPress keyPress, CancellationToken cancellationToken)
    {
        var keyChar = keyPress.ConsoleKeyInfo.KeyChar;

        if (keyChar is ' ' or '=')
        {
            var node = await GetNode().ConfigureAwait(false);
            if (node is
                ArgumentSyntax //https://github.com/waf/CSharpRepl/issues/145
                or ArgumentListSyntax //https://github.com/waf/CSharpRepl/issues/200
                or AnonymousObjectMemberDeclaratorSyntax //https://github.com/waf/CSharpRepl/issues/157
                or VariableDeclaratorSyntax) // dynamic declarator https://github.com/waf/CSharpRepl/issues/231
            {

                return false;
            }
        }

        if (keyChar is '.')
        {
            var node = await GetNode().ConfigureAwait(false);
            // entering range syntax arr[1..2] https://github.com/waf/CSharpRepl/issues/279
            if (node is ArgumentSyntax { Parent: BracketedArgumentListSyntax, Expression: MemberAccessExpressionSyntax }
                or BracketedArgumentListSyntax { Parent: ElementAccessExpressionSyntax })
            {
                return false;
            }
        }

        if (keyChar is ',' or ')')
        {
            var node = await GetNode().ConfigureAwait(false);

            //https://github.com/waf/CSharpRepl/issues/201
            if (node is
                ArgumentSyntax or ParameterSyntax or ParameterListSyntax or
                IdentifierNameSyntax { Parent: ParenthesizedExpressionSyntax { Parent: ArgumentSyntax } } or
                IdentifierNameSyntax { Parent: ParameterSyntax })
            {
                return false;
            }
        }

        return true;

        async Task<SyntaxNode?> GetNode()
        {
            await Initialization.ConfigureAwait(false);

            var sourceText = SourceText.From(text);
            var document = workspaceManager.CurrentDocument.WithText(sourceText);
            var tree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
            if (tree is null) return null;
            var root = await tree.GetRootAsync(cancellationToken).ConfigureAwait(false);

            return FindNonWhitespaceNode(text, root, caret);
        }
    }

    public async Task<(string Text, int Caret)> FormatInput(string text, int caret, bool formatParentNodeOnly, CancellationToken cancellationToken)
    {
        if (caret > 0)
        {
            await Initialization.ConfigureAwait(false);

            var sourceText = SourceText.From(text);
            var document = workspaceManager.CurrentDocument.WithText(sourceText);

            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root is null) return (text, caret);

            var token = root.FindToken(caret - 1);
            if (token.IsKind(SyntaxKind.None)) return (text, caret);

            int caretTokenOffset = caret - token.SpanStart;
            var annotation = new SyntaxAnnotation();
            document = document.WithSyntaxRoot(root.ReplaceToken(token, token.WithAdditionalAnnotations(annotation)));

            Task<Document> formattedDocumentTask;
            if (formatParentNodeOnly)
            {
                if (token.Parent is not { } parent)
                {
                    return (text, caret);
                }
                if (parent is BlockSyntax)
                {
                    parent = parent.Parent;
                    if (parent is null) return (text, caret);
                }
                var span = TextSpan.FromBounds(parent.FullSpan.Start, Math.Min(parent.FullSpan.End, token.SpanStart));
                formattedDocumentTask = Formatter.FormatAsync(document, span, cancellationToken: cancellationToken);
            }
            else
            {
                formattedDocumentTask = Formatter.FormatAsync(document, cancellationToken: cancellationToken);
            }
            var formattedDocument = await formattedDocumentTask.ConfigureAwait(false);
            var formattedText = await formattedDocument.GetTextAsync(cancellationToken).ConfigureAwait(false);
            if (formattedText is null) return (text, caret);

            root = await formattedDocument.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root is null) return (text, caret);

            token = root.DescendantTokens().FirstOrDefault(t => t.HasAnnotation(annotation));
            if (token.IsKind(SyntaxKind.None)) return (text, caret);

            var newCaret = token.SpanStart + caretTokenOffset;
            if (newCaret >= 0 && newCaret <= formattedText.Length)
            {
                return (formattedText.ToString(), newCaret);
            }
            else
            {
                Debug.Assert(false);
                return (text, caret);
            }
        }

        return (text, caret);
    }

    private SyntaxNode? FindNonWhitespaceNode(string text, SyntaxNode root, int caret)
    {
        var node = root.FindNode(new TextSpan(caret, 0));
        if (node.IsKind(SyntaxKind.CompilationUnit))
        {
            for (int i = caret - 1; i >= 0; i--)
            {
                if (!char.IsWhiteSpace(text[i]))
                {
                    return root.FindNode(new TextSpan(i, 0));
                }
            }
        }
        return node;
    }

    public async Task<EvaluationResult> ConvertToIntermediateLanguage(string csharpCode, bool debugMode)
    {
        await Initialization.ConfigureAwait(false);
        return disassembler.Disassemble(csharpCode, debugMode);
    }

    /// <summary>
    /// Roslyn services can be a bit slow to initialize the first time they're executed.
    /// Warm them up in the background so it doesn't affect the user.
    /// </summary>
    public Task WarmUpAsync(string[] args) =>
        Task.Run(async () =>
        {
            await Initialization.ConfigureAwait(false);

            logger.Log("Warm-up Starting");

            const string RuntimeHelperName = "CSharpRepl.Services.RuntimeHelper.cs";
            Task evaluationTask;
            using (var runtimeHelperStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(RuntimeHelperName))
            {
                if (runtimeHelperStream != null)
                {
                    var runtimeHelperText = new StreamReader(runtimeHelperStream).ReadToEnd();
                    evaluationTask = EvaluateAsync(runtimeHelperText, args);
                }
                else
                {
                    evaluationTask = Task.CompletedTask;
                    console.WriteErrorLine($"Unable to load '{RuntimeHelperName}'");
                }
            }

            var highlightTask = SyntaxHighlightAsync(@"_ = ""REPL Warmup""");
            var formattingTask = FormatInput(@"_=""REPL Warmup"";", caret: 16, formatParentNodeOnly: false, default);
            var completionTask = Task.WhenAny(
                (await CompleteAsync(@"C", 1))
                    .Where(completion => completion.Item.DisplayText.StartsWith("C"))
                    .Take(15)
                    .Select(completion => completion.GetDescriptionAsync(cancellationToken: default))
            );

            await Task.WhenAll(evaluationTask, highlightTask, formattingTask, completionTask).ConfigureAwait(false);
            logger.Log("Warm-up Complete");
        });

    private async Task<string> InterceptInputWhenRefStructReturnNeedsToBeHandled(string input, CancellationToken cancellationToken)
    {
        await Initialization.ConfigureAwait(false);

        if ((await scriptRunner.HasValueReturningStatement(input, cancellationToken).ConfigureAwait(false)).TryGet(out var result))
        {
            var root = result.Expression.SyntaxTree.GetRoot(cancellationToken);
            var expressionToBeWrapped = result.Expression;
            var wrappedExpression = expressionToBeWrapped;
            if (result.Type is { Name: "Span" or "ReadOnlySpan", ContainingNamespace.Name: "System" })
            {
                wrappedExpression = Wrap(nameof(__CSharpRepl_RuntimeHelper.HandleSpanOutput), result.Expression);
            }
            else if (result.Type is { Name: "Memory" or "ReadOnlyMemory", ContainingNamespace.Name: "System" })
            {
                wrappedExpression = Wrap(nameof(__CSharpRepl_RuntimeHelper.HandleMemoryOutput), result.Expression);
            }
            else if (result.Type.IsRefLikeType)
            {
                var toStringMethod = result.Type
                    .GetMembers(nameof(ToString))
                    .OfType<IMethodSymbol>()
                    .Where(m => m.ReturnType.SpecialType == SpecialType.System_String && m.Parameters.Length == 0 && m.IsOverride)
                    .FirstOrDefault();

                if (toStringMethod != null)
                {
                    expressionToBeWrapped = SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            expressionToBeWrapped,
                            SyntaxFactory.IdentifierName(nameof(ToString))));
                }
                else
                {
                    expressionToBeWrapped = SyntaxFactory.LiteralExpression(
                        SyntaxKind.StringLiteralExpression,
                        SyntaxFactory.Literal($"Cannot output a value of '{result.Type.Name}' because it's a ref-struct. It has to override ToString() to see its value."));
                }
                wrappedExpression = Wrap(
                    nameof(__CSharpRepl_RuntimeHelper.HandleRefStructOutput),
                    expressionToBeWrapped);
            }

            if (result.Expression != wrappedExpression)
            {
                root = root.ReplaceNode(result.Expression, wrappedExpression);
                return root.GetText().ToString();
            }
        }
        return input;

        static ExpressionSyntax Wrap(string runtimeHelperMethod, ExpressionSyntax expressionToBeWrapped) =>
             SyntaxFactory.InvocationExpression(
                            SyntaxFactory.MemberAccessExpression(
                                SyntaxKind.SimpleMemberAccessExpression,
                                SyntaxFactory.IdentifierName(nameof(__CSharpRepl_RuntimeHelper)),
                                SyntaxFactory.IdentifierName(runtimeHelperMethod)))
                        .WithArgumentList(
                            SyntaxFactory.ArgumentList(
                                SyntaxFactory.SingletonSeparatedList(
                                    SyntaxFactory.Argument(
                                        expressionToBeWrapped))));
    }
}