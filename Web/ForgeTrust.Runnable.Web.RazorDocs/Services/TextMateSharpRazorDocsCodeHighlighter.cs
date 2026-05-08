using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using TextMateSharp.Grammars;
using TextMateSharp.Registry;

namespace ForgeTrust.Runnable.Web.RazorDocs.Services;

/// <summary>
/// TextMateSharp-backed implementation of RazorDocs' internal code-block highlighting contract.
/// </summary>
internal sealed class TextMateSharpRazorDocsCodeHighlighter : IRazorDocsCodeHighlighter
{
    internal const int MaxHighlightedCodeBlockCharacters = 120_000;
    internal const int MaxHighlightedCodeBlockLines = 2_000;

    private static readonly TimeSpan TokenizationTimeout = TimeSpan.FromSeconds(1);
    private readonly RazorDocsCodeLanguageCatalog _languageCatalog;
    private readonly ILogger<TextMateSharpRazorDocsCodeHighlighter> _logger;
    private readonly RegistryOptions _registryOptions;
    private readonly Registry _registry;
    private readonly ConcurrentDictionary<string, Lazy<IGrammar?>> _grammars = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the TextMateSharp highlighter.
    /// </summary>
    /// <param name="languageCatalog">Language normalization catalog.</param>
    /// <param name="logger">Logger used for fallback diagnostics.</param>
    public TextMateSharpRazorDocsCodeHighlighter(
        RazorDocsCodeLanguageCatalog languageCatalog,
        ILogger<TextMateSharpRazorDocsCodeHighlighter> logger)
    {
        ArgumentNullException.ThrowIfNull(languageCatalog);
        ArgumentNullException.ThrowIfNull(logger);

        _languageCatalog = languageCatalog;
        _logger = logger;
        _registryOptions = new RegistryOptions(ThemeName.DarkPlus);
        _registry = new Registry(_registryOptions);
    }

    /// <inheritdoc />
    public RazorDocsHighlightedCode Highlight(RazorDocsCodeBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);

        var language = _languageCatalog.Normalize(block.Language);
        var code = block.Code ?? string.Empty;

        if (language.IsPlainText || IsOverHighlightingThreshold(code))
        {
            return RenderPlain(code, language);
        }

        try
        {
            var grammar = LoadGrammar(language);
            if (grammar is null)
            {
                return RenderPlain(code, language);
            }

            return RenderHighlighted(code, language, grammar);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Falling back to escaped plaintext for RazorDocs code block language {Language}.",
                language.NormalizedLanguage);
            return RenderPlain(code, language);
        }
    }

    internal int CachedGrammarCount => _grammars.Count;

    private IGrammar? LoadGrammar(RazorDocsCodeLanguage language)
    {
        if (language.TextMateLanguageId is null)
        {
            return null;
        }

        var cached = _grammars.GetOrAdd(
            language.TextMateLanguageId,
            textMateLanguageId => new Lazy<IGrammar?>(
                () =>
                {
                    try
                    {
                        var scope = _registryOptions.GetScopeByLanguageId(textMateLanguageId);
                        return string.IsNullOrWhiteSpace(scope) ? null : _registry.LoadGrammar(scope);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Failed to load TextMate grammar for RazorDocs language {Language}.",
                            language.NormalizedLanguage);
                        return null;
                    }
                },
                LazyThreadSafetyMode.ExecutionAndPublication));

        return cached.Value;
    }

    private RazorDocsHighlightedCode RenderHighlighted(
        string code,
        RazorDocsCodeLanguage language,
        IGrammar grammar)
    {
        var builder = new StringBuilder(code.Length + 256);
        AppendWrapperOpen(builder, language, isHighlighted: true);

        IStateStack ruleStack = StateStack.NULL;
        var lines = NormalizeLineEndings(code).Split('\n');

        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            if (lineIndex > 0)
            {
                builder.Append('\n');
            }

            var line = lines[lineIndex];
            if (line.Length == 0)
            {
                var emptyResult = grammar.TokenizeLine(line, ruleStack, TokenizationTimeout);
                ruleStack = emptyResult.RuleStack;
                continue;
            }

            var result = grammar.TokenizeLine(line, ruleStack, TokenizationTimeout);
            ruleStack = result.RuleStack;
            AppendTokens(builder, line, result.Tokens);
        }

        AppendWrapperClose(builder);
        return new RazorDocsHighlightedCode(builder.ToString(), language.NormalizedLanguage, IsHighlighted: true);
    }

    private static void AppendTokens(StringBuilder builder, string line, IReadOnlyList<IToken> tokens)
    {
        if (tokens.Count == 0)
        {
            builder.Append(WebUtility.HtmlEncode(line));
            return;
        }

        var previousEnd = 0;
        foreach (var token in tokens)
        {
            var start = Math.Clamp(token.StartIndex, 0, line.Length);
            var end = Math.Clamp(token.EndIndex, start, line.Length);

            if (start > previousEnd)
            {
                builder.Append(WebUtility.HtmlEncode(line[previousEnd..start]));
            }

            if (end > start)
            {
                AppendToken(builder, line[start..end], ResolveTokenClass(token.Scopes));
            }

            previousEnd = end;
        }

        if (previousEnd < line.Length)
        {
            builder.Append(WebUtility.HtmlEncode(line[previousEnd..]));
        }
    }

    private static void AppendToken(StringBuilder builder, string text, string? tokenClass)
    {
        var encoded = WebUtility.HtmlEncode(text);
        if (string.IsNullOrWhiteSpace(tokenClass) || string.IsNullOrWhiteSpace(text))
        {
            builder.Append(encoded);
            return;
        }

        builder
            .Append("<span class=\"doc-token doc-token--")
            .Append(tokenClass)
            .Append("\">")
            .Append(encoded)
            .Append("</span>");
    }

    private static string? ResolveTokenClass(IReadOnlyList<string> scopes)
    {
        if (ContainsScope(scopes, "markup.deleted"))
        {
            return "deleted";
        }

        if (ContainsScope(scopes, "markup.inserted"))
        {
            return "inserted";
        }

        if (ContainsScope(scopes, "comment"))
        {
            return "comment";
        }

        if (ContainsScope(scopes, "string"))
        {
            return "string";
        }

        if (ContainsScope(scopes, "constant.numeric"))
        {
            return "number";
        }

        if (ContainsScope(scopes, "constant.language") || ContainsScope(scopes, "constant.other"))
        {
            return "literal";
        }

        if (ContainsScope(scopes, "storage") || ContainsScope(scopes, "keyword"))
        {
            return "keyword";
        }

        if (ContainsScope(scopes, "entity.name.type") || ContainsScope(scopes, "support.type"))
        {
            return "type";
        }

        if (ContainsScope(scopes, "entity.name.function")
            || ContainsScope(scopes, "support.function")
            || ContainsScope(scopes, "variable.function"))
        {
            return "member";
        }

        if (ContainsScope(scopes, "variable.parameter"))
        {
            return "parameter";
        }

        if (ContainsScope(scopes, "punctuation"))
        {
            return "punctuation";
        }

        if (ContainsScope(scopes, "operator"))
        {
            return "operator";
        }

        return null;
    }

    private static bool ContainsScope(IReadOnlyList<string> scopes, string value)
    {
        return scopes.Any(scope => scope.Contains(value, StringComparison.OrdinalIgnoreCase));
    }

    private static RazorDocsHighlightedCode RenderPlain(string code, RazorDocsCodeLanguage language)
    {
        var builder = new StringBuilder(code.Length + 192);
        AppendWrapperOpen(builder, language, isHighlighted: false);
        builder.Append(WebUtility.HtmlEncode(NormalizeLineEndings(code)));
        AppendWrapperClose(builder);
        return new RazorDocsHighlightedCode(builder.ToString(), language.NormalizedLanguage, IsHighlighted: false);
    }

    private static void AppendWrapperOpen(StringBuilder builder, RazorDocsCodeLanguage language, bool isHighlighted)
    {
        builder
            .Append("<pre class=\"doc-code ")
            .Append(isHighlighted ? "doc-code--highlighted" : "doc-code--plain")
            .Append(" doc-code--language-")
            .Append(language.NormalizedLanguage)
            .Append(" language-")
            .Append(language.ClassLanguage)
            .Append("\"><span class=\"doc-code__language\">")
            .Append(WebUtility.HtmlEncode(language.Label))
            .Append("</span><code>");
    }

    private static void AppendWrapperClose(StringBuilder builder)
    {
        builder.Append("</code></pre>");
    }

    private static bool IsOverHighlightingThreshold(string code)
    {
        if (code.Length > MaxHighlightedCodeBlockCharacters)
        {
            return true;
        }

        var lineCount = 1;
        for (var index = 0; index < code.Length; index++)
        {
            var ch = code[index];
            if (ch == '\n' || ch == '\r')
            {
                lineCount++;
                if (lineCount > MaxHighlightedCodeBlockLines)
                {
                    return true;
                }

                if (ch == '\r' && index + 1 < code.Length && code[index + 1] == '\n')
                {
                    index++;
                }
            }
        }

        return false;
    }

    private static string NormalizeLineEndings(string value)
    {
        return value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }
}
