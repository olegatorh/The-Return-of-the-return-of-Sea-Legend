namespace StormScript;

/// <summary>Pre-order traversal of the AST.</summary>
public static class Walker
{
    public static void Walk(Node? n, Action<Node> visit)
    {
        if (n == null) return;
        visit(n);
        switch (n)
        {
            case ArrayInit ai: foreach (var x in ai.Items) Walk(x, visit); break;
            case MemberExpr m: Walk(m.Target, visit); Walk(m.Dynamic, visit); break;
            case IndexExpr ix: Walk(ix.Target, visit); Walk(ix.Index, visit); break;
            case CallExpr c: Walk(c.Callee, visit); foreach (var a in c.Args) Walk(a, visit); break;
            case UnaryExpr u: Walk(u.Operand, visit); break;
            case BinaryExpr b: Walk(b.L, visit); Walk(b.R, visit); break;
            case AssignExpr a: Walk(a.Target, visit); Walk(a.Value, visit); break;
            case BlockStmt bl: foreach (var s in bl.Body) Walk(s, visit); break;
            case VarDecl v: Walk(v.ArraySize, visit); Walk(v.Init, visit); break;
            case DeclGroup g: foreach (var d in g.Decls) Walk(d, visit); break;
            case ExprStmt e: Walk(e.E, visit); break;
            case IfStmt i: Walk(i.Cond, visit); Walk(i.Then, visit); Walk(i.Else, visit); break;
            case WhileStmt w: Walk(w.Cond, visit); Walk(w.Body, visit); break;
            case DoWhileStmt d: Walk(d.Body, visit); Walk(d.Cond, visit); break;
            case ForStmt f: Walk(f.Init, visit); Walk(f.Cond, visit); Walk(f.Step, visit); Walk(f.Body, visit); break;
            case SwitchStmt sw:
                Walk(sw.Subject, visit);
                foreach (var c in sw.Cases) { Walk(c.Value, visit); foreach (var s in c.Body) Walk(s, visit); }
                break;
            case ReturnStmt r: Walk(r.Value, visit); break;
            case FuncDecl fd: Walk(fd.Body, visit); break;
        }
    }
}
