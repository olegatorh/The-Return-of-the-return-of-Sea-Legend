namespace StormScript;

public abstract class Node
{
    public int Line;
    public string File = "";
}

// ------------------------------------------------------------------ expressions
public abstract class Expr : Node { }

/// <summary>Numeric literal. <see cref="Const"/> is the #define it came from, if any.</summary>
public sealed class NumberLit(string text, bool isFloat, string? constName) : Expr
{
    public string Text = text;
    public bool IsFloat = isFloat;
    public string? Const = constName;
}

public sealed class StringLit(string value, string? constName) : Expr
{
    public string Value = value;
    public string? Const = constName;
    public string? Translated;      // filled lazily by the interpreter's language layer
    public bool TranslationChecked;
}

public sealed class IdentExpr(string name) : Expr { public string Name = name; }

/// <summary>Brace initializer: { a, b, c }</summary>
public sealed class ArrayInit(List<Expr> items) : Expr { public List<Expr> Items = items; }

/// <summary>target.Name or target.(Dynamic)</summary>
public sealed class MemberExpr(Expr target, string? name, Expr? dynamic) : Expr
{
    public Expr Target = target;
    public string? Name = name;
    public Expr? Dynamic = dynamic;
}

public sealed class IndexExpr(Expr target, Expr index) : Expr { public Expr Target = target, Index = index; }

/// <summary>Function call. IsDynamic is the "call var(args)" form where the callee is a string holding a function name.</summary>
public sealed class CallExpr(Expr callee, List<Expr> args, bool isDynamic) : Expr
{
    public Expr Callee = callee;
    public List<Expr> Args = args;
    public bool IsDynamic = isDynamic;
}

public sealed class UnaryExpr(string op, Expr operand, bool postfix) : Expr
{
    public string Op = op;
    public Expr Operand = operand;
    public bool Postfix = postfix;
}

public sealed class BinaryExpr(string op, Expr l, Expr r) : Expr { public string Op = op; public Expr L = l, R = r; }

public sealed class AssignExpr(string op, Expr target, Expr value) : Expr { public string Op = op; public Expr Target = target, Value = value; }

// ------------------------------------------------------------------ statements
public abstract class Stmt : Node { }

public sealed class BlockStmt(List<Stmt> body) : Stmt { public List<Stmt> Body = body; }

public sealed class VarDecl(string type, string name, Expr? arraySize, bool isArray, Expr? init) : Stmt
{
    public string Type = type;
    public string Name = name;
    public bool IsArray = isArray;
    public Expr? ArraySize = arraySize;
    public Expr? Init = init;
}

/// <summary>Several declarators from one statement, e.g. "int i,q;"</summary>
public sealed class DeclGroup(List<VarDecl> decls) : Stmt { public List<VarDecl> Decls = decls; }

public sealed class ExprStmt(Expr e) : Stmt { public Expr E = e; }
public sealed class IfStmt(Expr cond, Stmt then, Stmt? @else) : Stmt { public Expr Cond = cond; public Stmt Then = then; public Stmt? Else = @else; }
public sealed class WhileStmt(Expr cond, Stmt body) : Stmt { public Expr Cond = cond; public Stmt Body = body; }
public sealed class DoWhileStmt(Stmt body, Expr cond) : Stmt { public Stmt Body = body; public Expr Cond = cond; }
public sealed class ForStmt(Stmt? init, Expr? cond, Expr? step, Stmt body) : Stmt
{
    public Stmt? Init = init;
    public Expr? Cond = cond, Step = step;
    public Stmt Body = body;
}

public sealed class SwitchCase(Expr? value, List<Stmt> body) : Node
{
    public Expr? Value = value;   // null = default
    public List<Stmt> Body = body;
}

public sealed class SwitchStmt(Expr subject, List<SwitchCase> cases) : Stmt { public Expr Subject = subject; public List<SwitchCase> Cases = cases; }
public sealed class BreakStmt : Stmt { }
public sealed class ContinueStmt : Stmt { }
public sealed class ReturnStmt(Expr? value) : Stmt { public Expr? Value = value; }
public sealed class EmptyStmt : Stmt { }

// ------------------------------------------------------------------ top level
public sealed class Param(string type, string name, bool isArray)
{
    public string Type = type, Name = name;
    public bool IsArray = isArray;
}

/// <summary>Function definition or prototype (Body == null). Kind is "", "native" or "extern".</summary>
public sealed class FuncDecl(string kind, string retType, string name, List<Param> ps, BlockStmt? body) : Stmt
{
    public string Kind = kind;
    public string RetType = retType, Name = name;
    public List<Param> Params = ps;
    public BlockStmt? Body = body;
}

public sealed class ScriptUnit
{
    public string EntryFile = "";
    public List<Stmt> Items = new();    // FuncDecl | VarDecl | DeclGroup
    public List<string> Warnings = new();
    /// <summary>Syntax errors the parser recovered from (statement skipped). Each is a defect in the shipped script.</summary>
    public List<string> Errors = new();
}
