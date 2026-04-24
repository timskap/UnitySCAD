using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SCADPlugin.Editor.Graph.Import
{
    // Text-level preprocessor that extracts `module` and `function`
    // definitions out of the source before lexing. Definitions are
    // preserved verbatim (comments and all) so the runtime OpenSCAD still
    // sees them; the cleaned source keeps line numbers intact by replacing
    // stripped regions with whitespace. This keeps the main parser simple
    // — it never has to handle user definitions, for-loops, or anything
    // else that lives inside a module body.
    internal static class ScadSourcePreprocessor
    {
        public class ParsedModuleDef
        {
            public string name;
            public List<Param> parameters;
            public List<Stmt> body;
        }

        public class Result
        {
            public string cleaned;
            public string preamble;
            public HashSet<string> userModules = new HashSet<string>();
            public HashSet<string> userFunctions = new HashSet<string>();
            public Dictionary<string, ParsedModuleDef> parsedModules = new Dictionary<string, ParsedModuleDef>();
        }

        public static Result Extract(string source)
        {
            var r = new Result();
            source ??= string.Empty;
            var cleaned = new StringBuilder(source.Length);
            var preamble = new StringBuilder();

            int i = 0;
            int n = source.Length;
            while (i < n)
            {
                char c = source[i];

                // Line comment
                if (c == '/' && i + 1 < n && source[i + 1] == '/')
                {
                    int start = i;
                    while (i < n && source[i] != '\n') i++;
                    cleaned.Append(source, start, i - start);
                    continue;
                }
                // Block comment
                if (c == '/' && i + 1 < n && source[i + 1] == '*')
                {
                    int start = i;
                    i += 2;
                    while (i + 1 < n && !(source[i] == '*' && source[i + 1] == '/')) i++;
                    if (i + 1 < n) i += 2;
                    cleaned.Append(source, start, i - start);
                    continue;
                }
                // String literal
                if (c == '"')
                {
                    int start = i;
                    i++;
                    while (i < n && source[i] != '"')
                    {
                        if (source[i] == '\\' && i + 1 < n) i++;
                        i++;
                    }
                    if (i < n) i++;
                    cleaned.Append(source, start, i - start);
                    continue;
                }

                // Identifier — test for `module` / `function` at a word boundary.
                if (IsIdentStart(c) && (i == 0 || !IsIdentCont(source[i - 1])))
                {
                    int wordStart = i;
                    while (i < n && IsIdentCont(source[i])) i++;
                    string word = source.Substring(wordStart, i - wordStart);

                    if (word == "module" || word == "function")
                    {
                        bool isFunction = word == "function";
                        int defStart = wordStart;

                        int j = SkipSpaces(source, i);
                        int nameStart = j;
                        while (j < n && IsIdentCont(source[j])) j++;
                        string name = source.Substring(nameStart, j - nameStart);

                        // Record start/end of the parenthesised parameter
                        // list so we can re-parse it later.
                        while (j < n && source[j] != '(') j++;
                        int paramsOpen = j;
                        j = SkipBalanced(source, j, '(', ')');
                        int paramsClose = j; // one past the closing ')'

                        int bodyStart = -1, bodyEnd = -1;
                        if (isFunction)
                        {
                            j = SkipToSemicolon(source, j);
                            if (j < n) j++;
                        }
                        else
                        {
                            j = SkipSpaces(source, j);
                            if (j < n && source[j] == '{')
                            {
                                bodyStart = j + 1; // content after '{'
                                j = SkipBalanced(source, j, '{', '}');
                                bodyEnd = j - 1;   // content before '}'
                            }
                        }

                        preamble.Append(source, defStart, j - defStart);
                        preamble.Append('\n');

                        if (isFunction)
                        {
                            r.userFunctions.Add(name);
                        }
                        else
                        {
                            r.userModules.Add(name);

                            // Second pass: re-parse the params and body so the
                            // graph builder can inline this module call. On
                            // any parse failure we simply skip — the raw
                            // preamble path still keeps the generated SCAD
                            // compilable via a fallback UserModuleCallNode.
                            if (bodyStart >= 0 && bodyEnd >= bodyStart &&
                                paramsClose > paramsOpen + 1)
                            {
                                var parsed = TryParseModuleDef(
                                    name,
                                    paramsOpen + 1, paramsClose - 1,
                                    bodyStart, bodyEnd, source);
                                if (parsed != null && !r.parsedModules.ContainsKey(name))
                                    r.parsedModules[name] = parsed;
                            }
                        }

                        // Replace definition with whitespace so line numbers
                        // reported by the lexer still match the original
                        // source.
                        for (int k = defStart; k < j; k++)
                            cleaned.Append(source[k] == '\n' ? '\n' : ' ');

                        i = j;
                        continue;
                    }

                    cleaned.Append(word);
                    continue;
                }

                cleaned.Append(c);
                i++;
            }

            r.cleaned = cleaned.ToString();
            r.preamble = preamble.ToString();
            return r;
        }

        static int SkipSpaces(string s, int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            return i;
        }

        static int SkipToSemicolon(string s, int i)
        {
            while (i < s.Length && s[i] != ';')
            {
                if (s[i] == '"')
                {
                    i++;
                    while (i < s.Length && s[i] != '"')
                    {
                        if (s[i] == '\\' && i + 1 < s.Length) i++;
                        i++;
                    }
                    if (i < s.Length) i++;
                    continue;
                }
                if (i + 1 < s.Length && s[i] == '/' && s[i + 1] == '/')
                {
                    while (i < s.Length && s[i] != '\n') i++;
                    continue;
                }
                if (i + 1 < s.Length && s[i] == '/' && s[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < s.Length && !(s[i] == '*' && s[i + 1] == '/')) i++;
                    if (i + 1 < s.Length) i += 2;
                    continue;
                }
                i++;
            }
            return i;
        }

        static int SkipBalanced(string s, int start, char open, char close)
        {
            if (start >= s.Length || s[start] != open) return start;
            int depth = 0;
            int i = start;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '"')
                {
                    i++;
                    while (i < s.Length && s[i] != '"')
                    {
                        if (s[i] == '\\' && i + 1 < s.Length) i++;
                        i++;
                    }
                    if (i < s.Length) i++;
                    continue;
                }
                if (i + 1 < s.Length && c == '/' && s[i + 1] == '/')
                {
                    while (i < s.Length && s[i] != '\n') i++;
                    continue;
                }
                if (i + 1 < s.Length && c == '/' && s[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < s.Length && !(s[i] == '*' && s[i + 1] == '/')) i++;
                    if (i + 1 < s.Length) i += 2;
                    continue;
                }
                if (c == open) depth++;
                else if (c == close)
                {
                    depth--;
                    if (depth == 0) return i + 1;
                }
                i++;
            }
            return i;
        }

        static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';
        static bool IsIdentCont(char c) => char.IsLetterOrDigit(c) || c == '_';

        static ParsedModuleDef TryParseModuleDef(
            string name,
            int paramsStart, int paramsEnd,
            int bodyStart, int bodyEnd,
            string source)
        {
            try
            {
                var paramsText = source.Substring(paramsStart, paramsEnd - paramsStart);
                var bodyText   = source.Substring(bodyStart,   bodyEnd   - bodyStart);

                List<Param> parameters;
                if (string.IsNullOrWhiteSpace(paramsText))
                {
                    parameters = new List<Param>();
                }
                else
                {
                    var pLex = new Lexer(paramsText);
                    var pParser = new Parser(pLex.Tokenize());
                    parameters = pParser.ParseFormalParams();
                    if (parameters == null) return null;
                }

                var bLex = new Lexer(bodyText);
                var bParser = new Parser(bLex.Tokenize());
                var body = bParser.ParseProgram();
                // Tolerate parse warnings inside the body — the builder
                // will fall back to CustomStatementNode for anything that
                // didn't turn into a usable statement.

                return new ParsedModuleDef
                {
                    name = name,
                    parameters = parameters,
                    body = body,
                };
            }
            catch
            {
                return null;
            }
        }
    }
    // Hand-written lexer + recursive-descent parser for the subset of
    // OpenSCAD we need for graph import. Scope: assignments, module calls
    // (both block-form and trailing-statement), literals (number / bool /
    // string / vector / undef), identifiers, simple arithmetic
    // (+ - * / %), unary minus, parenthesised expressions, positional and
    // keyword arguments. Out of scope in this pass: `module` / `function`
    // definitions, `for`, `if`, list comprehensions, `include` / `use`.
    // Anything outside the supported grammar is either skipped with a
    // warning or preserved as raw SCAD inside a CustomStatementNode.

    internal enum TokKind
    {
        Number, Ident, DollarIdent, String, True, False, Undef,
        LParen, RParen, LBrace, RBrace, LBracket, RBracket,
        Semi, Comma, Colon, Eq, Plus, Minus, Star, Slash, Percent,
        Eof
    }

    internal class Tok
    {
        public TokKind kind;
        public string text;
        public double numValue;
        public int line;
        public override string ToString() => $"{kind}('{text}')";
    }

    internal class Lexer
    {
        readonly string _src;
        int _pos;
        int _line = 1;
        public readonly List<string> errors = new List<string>();

        public Lexer(string source)
        {
            _src = source ?? string.Empty;
        }

        public List<Tok> Tokenize()
        {
            var list = new List<Tok>();
            while (true)
            {
                SkipWhitespaceAndComments();
                if (_pos >= _src.Length)
                {
                    list.Add(new Tok { kind = TokKind.Eof, line = _line });
                    return list;
                }

                char c = _src[_pos];

                if (char.IsDigit(c) || (c == '.' && _pos + 1 < _src.Length && char.IsDigit(_src[_pos + 1])))
                {
                    list.Add(ReadNumber()); continue;
                }
                if (IsIdentStart(c))
                {
                    list.Add(ReadIdent()); continue;
                }
                if (c == '$')
                {
                    list.Add(ReadDollarIdent()); continue;
                }
                if (c == '"')
                {
                    list.Add(ReadString()); continue;
                }

                switch (c)
                {
                    case '(': _pos++; list.Add(new Tok { kind = TokKind.LParen,   text = "(", line = _line }); continue;
                    case ')': _pos++; list.Add(new Tok { kind = TokKind.RParen,   text = ")", line = _line }); continue;
                    case '{': _pos++; list.Add(new Tok { kind = TokKind.LBrace,   text = "{", line = _line }); continue;
                    case '}': _pos++; list.Add(new Tok { kind = TokKind.RBrace,   text = "}", line = _line }); continue;
                    case '[': _pos++; list.Add(new Tok { kind = TokKind.LBracket, text = "[", line = _line }); continue;
                    case ']': _pos++; list.Add(new Tok { kind = TokKind.RBracket, text = "]", line = _line }); continue;
                    case ';': _pos++; list.Add(new Tok { kind = TokKind.Semi,     text = ";", line = _line }); continue;
                    case ',': _pos++; list.Add(new Tok { kind = TokKind.Comma,    text = ",", line = _line }); continue;
                    case ':': _pos++; list.Add(new Tok { kind = TokKind.Colon,    text = ":", line = _line }); continue;
                    case '=': _pos++; list.Add(new Tok { kind = TokKind.Eq,       text = "=", line = _line }); continue;
                    case '+': _pos++; list.Add(new Tok { kind = TokKind.Plus,     text = "+", line = _line }); continue;
                    case '-': _pos++; list.Add(new Tok { kind = TokKind.Minus,    text = "-", line = _line }); continue;
                    case '*': _pos++; list.Add(new Tok { kind = TokKind.Star,     text = "*", line = _line }); continue;
                    case '/': _pos++; list.Add(new Tok { kind = TokKind.Slash,    text = "/", line = _line }); continue;
                    case '%': _pos++; list.Add(new Tok { kind = TokKind.Percent,  text = "%", line = _line }); continue;
                }

                errors.Add($"Line {_line}: unexpected character '{c}'");
                _pos++;
            }
        }

        static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';
        static bool IsIdentCont(char c) => char.IsLetterOrDigit(c) || c == '_';

        void SkipWhitespaceAndComments()
        {
            while (_pos < _src.Length)
            {
                char c = _src[_pos];
                if (c == '\n') { _line++; _pos++; }
                else if (char.IsWhiteSpace(c)) { _pos++; }
                else if (c == '/' && _pos + 1 < _src.Length && _src[_pos + 1] == '/')
                {
                    while (_pos < _src.Length && _src[_pos] != '\n') _pos++;
                }
                else if (c == '/' && _pos + 1 < _src.Length && _src[_pos + 1] == '*')
                {
                    _pos += 2;
                    while (_pos + 1 < _src.Length && !(_src[_pos] == '*' && _src[_pos + 1] == '/'))
                    {
                        if (_src[_pos] == '\n') _line++;
                        _pos++;
                    }
                    if (_pos + 1 < _src.Length) _pos += 2;
                }
                else break;
            }
        }

        Tok ReadNumber()
        {
            int start = _pos;
            while (_pos < _src.Length && (char.IsDigit(_src[_pos]) || _src[_pos] == '.')) _pos++;
            if (_pos < _src.Length && (_src[_pos] == 'e' || _src[_pos] == 'E'))
            {
                _pos++;
                if (_pos < _src.Length && (_src[_pos] == '+' || _src[_pos] == '-')) _pos++;
                while (_pos < _src.Length && char.IsDigit(_src[_pos])) _pos++;
            }
            string text = _src.Substring(start, _pos - start);
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v);
            return new Tok { kind = TokKind.Number, text = text, numValue = v, line = _line };
        }

        Tok ReadIdent()
        {
            int start = _pos;
            while (_pos < _src.Length && IsIdentCont(_src[_pos])) _pos++;
            string text = _src.Substring(start, _pos - start);
            TokKind k = text switch
            {
                "true"  => TokKind.True,
                "false" => TokKind.False,
                "undef" => TokKind.Undef,
                _       => TokKind.Ident,
            };
            return new Tok { kind = k, text = text, line = _line };
        }

        Tok ReadDollarIdent()
        {
            int start = _pos;
            _pos++; // past '$'
            while (_pos < _src.Length && IsIdentCont(_src[_pos])) _pos++;
            return new Tok
            {
                kind = TokKind.DollarIdent,
                text = _src.Substring(start, _pos - start),
                line = _line,
            };
        }

        Tok ReadString()
        {
            int line = _line;
            _pos++; // past "
            var sb = new StringBuilder();
            while (_pos < _src.Length && _src[_pos] != '"')
            {
                if (_src[_pos] == '\\' && _pos + 1 < _src.Length)
                {
                    char next = _src[_pos + 1];
                    sb.Append(next switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        'r' => '\r',
                        '\\' => '\\',
                        '"' => '"',
                        _ => next,
                    });
                    _pos += 2;
                }
                else
                {
                    if (_src[_pos] == '\n') _line++;
                    sb.Append(_src[_pos]);
                    _pos++;
                }
            }
            if (_pos < _src.Length) _pos++; // past closing "
            return new Tok { kind = TokKind.String, text = sb.ToString(), line = line };
        }
    }

    // ---- AST ----

    internal abstract class Expr { }
    internal class NumExpr   : Expr { public double value; }
    internal class BoolExpr  : Expr { public bool value; }
    internal class StringExpr: Expr { public string value; }
    internal class UndefExpr : Expr { }
    internal class IdentExpr : Expr { public string name; public bool isDollar; }
    internal class VecExpr   : Expr { public List<Expr> items = new List<Expr>(); }
    internal class RangeExpr : Expr { public Expr start, step, end; }
    internal class UnaryExpr : Expr { public string op; public Expr operand; }
    internal class BinaryExpr: Expr { public string op; public Expr left; public Expr right; }
    internal class CallExpr  : Expr { public string name; public List<Arg> args = new List<Arg>(); }

    internal class Arg { public string name; public Expr value; }
    internal class Param { public string name; public Expr defaultValue; }

    internal abstract class Stmt { }
    internal class AssignStmt : Stmt { public string name; public Expr value; public bool isDollar; }
    internal class ModuleStmt : Stmt { public string name; public List<Arg> args = new List<Arg>(); public List<Stmt> children = new List<Stmt>(); }

    // ---- Parser ----

    internal class Parser
    {
        readonly List<Tok> _t;
        int _p;
        public readonly List<string> errors = new List<string>();

        public Parser(List<Tok> tokens) { _t = tokens; }

        Tok Peek() => _t[_p];
        Tok Peek(int ahead) => _p + ahead < _t.Count ? _t[_p + ahead] : _t[_t.Count - 1];
        bool Match(TokKind k) { if (Peek().kind == k) { _p++; return true; } return false; }
        Tok Consume() => _t[_p++];
        Tok Expect(TokKind k)
        {
            if (Peek().kind != k)
            {
                errors.Add($"Line {Peek().line}: expected {k}, got {Peek().kind} ('{Peek().text}')");
                return Peek();
            }
            return _t[_p++];
        }

        public List<Stmt> ParseProgram()
        {
            var list = new List<Stmt>();
            while (Peek().kind != TokKind.Eof)
            {
                var s = ParseStatement();
                if (s != null) list.Add(s);
                else _p++; // advance on error to avoid infinite loop
            }
            return list;
        }

        // External entry point for parsing the formal parameter list of a
        // user-defined module/function, extracted from source by the
        // preprocessor. Input has the form `a, b=5, c` (no surrounding
        // parens). Returns null on any parse error so the caller can fall
        // back to the raw-preamble path.
        public List<Param> ParseFormalParams()
        {
            var list = new List<Param>();
            while (Peek().kind != TokKind.Eof)
            {
                if (Peek().kind != TokKind.Ident && Peek().kind != TokKind.DollarIdent)
                {
                    errors.Add($"Line {Peek().line}: expected parameter name, got {Peek().kind}");
                    return null;
                }
                var name = Consume().text;
                Expr def = null;
                if (Match(TokKind.Eq)) def = ParseExpression();
                list.Add(new Param { name = name, defaultValue = def });
                if (!Match(TokKind.Comma)) break;
            }
            if (Peek().kind != TokKind.Eof)
            {
                errors.Add($"Line {Peek().line}: trailing input in parameter list");
                return null;
            }
            return list;
        }

        Stmt ParseStatement()
        {
            var t = Peek();

            // Bare block (e.g. at top level): treat as an implicit union.
            if (t.kind == TokKind.LBrace)
            {
                var union = new ModuleStmt { name = "union" };
                Consume();
                while (Peek().kind != TokKind.RBrace && Peek().kind != TokKind.Eof)
                {
                    var child = ParseStatement();
                    if (child != null) union.children.Add(child);
                    else _p++;
                }
                Match(TokKind.RBrace);
                return union;
            }

            // Assignment: `ident = expr ;` or `$ident = expr ;` — only at
            // statement level. Dollar-assignments (`$fn = 40;`) carry a
            // flag so the graph builder can route them to the preamble
            // instead of creating an exposed user parameter.
            if ((t.kind == TokKind.Ident || t.kind == TokKind.DollarIdent) &&
                Peek(1).kind == TokKind.Eq)
            {
                Consume(); // ident
                Consume(); // =
                var value = ParseExpression();
                Expect(TokKind.Semi);
                return new AssignStmt
                {
                    name = t.text,
                    value = value,
                    isDollar = t.kind == TokKind.DollarIdent,
                };
            }

            // Module call: `name(args) ... ;` or `name(args) { ... }` or
            // `name(args) otherCall(...)` (nested single-child form).
            if (t.kind == TokKind.Ident)
            {
                Consume();
                Expect(TokKind.LParen);
                var args = ParseArgList(TokKind.RParen);
                Expect(TokKind.RParen);

                var m = new ModuleStmt { name = t.text, args = args };

                if (Peek().kind == TokKind.Semi)
                {
                    Consume();
                    return m;
                }
                if (Peek().kind == TokKind.LBrace)
                {
                    Consume();
                    while (Peek().kind != TokKind.RBrace && Peek().kind != TokKind.Eof)
                    {
                        var child = ParseStatement();
                        if (child != null) m.children.Add(child);
                        else _p++;
                    }
                    Match(TokKind.RBrace);
                    return m;
                }
                // Chained form: `translate(...) cube(...);`
                var single = ParseStatement();
                if (single != null) m.children.Add(single);
                return m;
            }

            // Stray semicolon
            if (t.kind == TokKind.Semi)
            {
                Consume();
                return null;
            }

            errors.Add($"Line {t.line}: unexpected token '{t.text}' ({t.kind})");
            return null;
        }

        List<Arg> ParseArgList(TokKind terminator)
        {
            var list = new List<Arg>();
            while (Peek().kind != terminator && Peek().kind != TokKind.Eof)
            {
                var arg = new Arg();
                if ((Peek().kind == TokKind.Ident || Peek().kind == TokKind.DollarIdent)
                    && Peek(1).kind == TokKind.Eq)
                {
                    arg.name = Peek().text;
                    _p += 2;
                }
                arg.value = ParseExpression();
                list.Add(arg);
                if (!Match(TokKind.Comma)) break;
            }
            return list;
        }

        // Expression grammar: additive over multiplicative over unary over primary.
        Expr ParseExpression() => ParseAdd();

        Expr ParseAdd()
        {
            var left = ParseMul();
            while (Peek().kind == TokKind.Plus || Peek().kind == TokKind.Minus)
            {
                var op = Consume().text;
                var right = ParseMul();
                left = new BinaryExpr { op = op, left = left, right = right };
            }
            return left;
        }

        Expr ParseMul()
        {
            var left = ParseUnary();
            while (Peek().kind == TokKind.Star || Peek().kind == TokKind.Slash || Peek().kind == TokKind.Percent)
            {
                var op = Consume().text;
                var right = ParseUnary();
                left = new BinaryExpr { op = op, left = left, right = right };
            }
            return left;
        }

        Expr ParseUnary()
        {
            if (Peek().kind == TokKind.Minus)
            {
                Consume();
                return new UnaryExpr { op = "-", operand = ParseUnary() };
            }
            if (Peek().kind == TokKind.Plus)
            {
                Consume();
                return ParseUnary();
            }
            return ParsePrimary();
        }

        Expr ParsePrimary()
        {
            var t = Peek();
            switch (t.kind)
            {
                case TokKind.Number:
                    Consume();
                    return new NumExpr { value = t.numValue };
                case TokKind.True:
                    Consume();
                    return new BoolExpr { value = true };
                case TokKind.False:
                    Consume();
                    return new BoolExpr { value = false };
                case TokKind.Undef:
                    Consume();
                    return new UndefExpr();
                case TokKind.String:
                    Consume();
                    return new StringExpr { value = t.text };
                case TokKind.Ident:
                    Consume();
                    if (Peek().kind == TokKind.LParen)
                    {
                        Consume();
                        var args = ParseArgList(TokKind.RParen);
                        Expect(TokKind.RParen);
                        return new CallExpr { name = t.text, args = args };
                    }
                    return new IdentExpr { name = t.text };
                case TokKind.DollarIdent:
                    Consume();
                    return new IdentExpr { name = t.text, isDollar = true };
                case TokKind.LParen:
                    Consume();
                    var inner = ParseExpression();
                    Expect(TokKind.RParen);
                    return inner;
                case TokKind.LBracket:
                {
                    Consume();
                    if (Peek().kind == TokKind.RBracket)
                    {
                        Consume();
                        return new VecExpr();
                    }
                    var first = ParseExpression();
                    // Range form: [a : b] or [a : step : b]
                    if (Peek().kind == TokKind.Colon)
                    {
                        Consume();
                        var second = ParseExpression();
                        Expr third = null;
                        if (Peek().kind == TokKind.Colon)
                        {
                            Consume();
                            third = ParseExpression();
                        }
                        Expect(TokKind.RBracket);
                        return third == null
                            ? new RangeExpr { start = first, step = null, end = second }
                            : new RangeExpr { start = first, step = second, end = third };
                    }
                    // Plain vector literal
                    var vec = new VecExpr();
                    vec.items.Add(first);
                    while (Match(TokKind.Comma))
                    {
                        if (Peek().kind == TokKind.RBracket) break;
                        vec.items.Add(ParseExpression());
                    }
                    Expect(TokKind.RBracket);
                    return vec;
                }
            }

            errors.Add($"Line {t.line}: unexpected token '{t.text}' in expression");
            Consume();
            return new NumExpr { value = 0 };
        }
    }
}
