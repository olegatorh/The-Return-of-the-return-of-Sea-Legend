using StormScript.Runtime;
using Xunit;

namespace StormScript.Tests;

/// <summary>Semantics of the script interpreter. Each test compiles a tiny script and checks globals afterwards.</summary>
public sealed class InterpreterTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "ss_tests_" + Guid.NewGuid().ToString("N"));

    public InterpreterTests() => Directory.CreateDirectory(dir);
    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    ScriptHost Run(string source, string call = "Test")
    {
        File.WriteAllText(Path.Combine(dir, "main.c"), source, System.Text.Encoding.Latin1);
        var h = new ScriptHost(dir);
        h.It.Log = _ => { };
        Assert.True(h.LoadFile("main.c", "<main>"));
        h.It.CallTop(call);
        Assert.Empty(h.It.RuntimeErrors);
        return h;
    }

    static string Str(ScriptHost h, string global) => Conv.ToStr(h.It.Globals[global].Val);
    static string Attr(ScriptHost h, string global, string path) => h.It.Globals[global].Val.Attr!.Find(path, false)?.Value ?? "<none>";

    [Fact]
    public void RefFunctionReturningAddressOfGlobalIntKeepsTheVariable()
    {
        // event handlers such as BI_CommandEndChecking answer with "ref f() { ...; return &retValue; }": the caller reads the integer through the reference
        var h = Run(@"int retv; int r; ref Get() { retv = 512; return &retv; } void Test() { ref x = Get(); r = x; }");
        Assert.Equal("512", Str(h, "r"));
    }

    [Fact]
    public void StringConcatenationAndNumberFormatting()
    {
        var h = Run(@"string r; void Test() { r = ""a"" + 1 + ""b"" + 2.5; }");
        Assert.Equal("a1b2.5", Str(h, "r"));
    }

    [Fact]
    public void SwitchOnIntMatchesStringCases()
    {
        // core compiler.cpp STACK_COMPARE: the case value is the left operand, so case "1" matches the int 1 (HeroStringReactionRepeat relies on it)
        var h = Run(@"string r; void Test() { int t = 1; switch (t) { case ""0"": r = ""zero""; break; case ""1"": r = ""one""; break; } }");
        Assert.Equal("one", Str(h, "r"));
    }

    [Fact]
    public void StringEqualityIsCaseInsensitive()
    {
        var h = Run(@"int r; void Test() { if (""AbC"" == ""aBc"") r = 1; else r = 2; }");
        Assert.Equal("1", Str(h, "r"));
    }

    [Fact]
    public void MissingAttributeReadsAsEmptyString()
    {
        var h = Run(@"object o; string r; void Test() { r = ""[""+o.nothing+""]""; }");
        Assert.Equal("[]", Str(h, "r"));
    }

    [Fact]
    public void AttributeTreesAndDynamicAttributeNames()
    {
        var h = Run(@"object o; void Test() { string n = ""b""; o.a.(n).c = 5; o.a.x = ""y""; }");
        Assert.Equal("5", Attr(h, "o", "a.b.c"));
        Assert.Equal("y", Attr(h, "o", "a.x"));
    }

    [Fact]
    public void DynamicAttributeNameMayBeADottedPath()
    {
        var h = Run(@"object o; void Test() { string p = ""x.y.z""; o.(p) = 7; }");
        Assert.Equal("7", Attr(h, "o", "x.y.z"));
    }

    [Fact]
    public void ArraysAndDynamicCall()
    {
        var h = Run(@"int a[3]; int r; int Twice(int v) { return v*2; } void Test() { a[1] = 21; string f = ""Twice""; r = call f(a[1]); }");
        Assert.Equal("42", Str(h, "r"));
    }

    [Fact]
    public void DefinesAreCaseSensitiveButIdentifiersAreNot()
    {
        var h = Run("#define MAP \"map\"\nobject o; string r; void Test() { o.map = 1; Int X = 3; r = MAP + x; }");
        Assert.Equal("map3", Str(h, "r"));
        Assert.Equal("1", Attr(h, "o", "map"));       // ".map" is an attribute, not the define
    }

    [Fact]
    public void ReferenceParameterThatIsRepointedIsCopiedBack()
    {
        // Items_FindItem(id, &arItem) pattern: "itemARef = curItem" must change the caller's variable
        var h = Run(@"object items[3]; string r;
            int Find(string id, ref out) { aref cur; for (int i=0;i<3;i++) { makearef(cur, items[i]); if (cur.id == id) { out = cur; return i; } } return -1; }
            void Test() { items[1].id = ""sword""; aref found; int idx = Find(""sword"", &found); r = idx + "":"" + found.id; }");
        Assert.Equal("1:sword", Str(h, "r"));
    }

    [Fact]
    public void AddressOfVariablePassedToStringBuiltinIsItsValue()
    {
        var h = Run(@"int r; void Test() { string s = ""dep0,dep1,dep2""; r = findSubStr(&s, "","", 0); }");
        Assert.Equal("4", Str(h, "r"));
    }

    [Fact]
    public void MakeRefOfAnArrayDenotesTheArray()
    {
        var h = Run(@"string names[2]; string r; void Test() { names[0]=""x""; names[1]=""y""; ref rn; makeref(rn, names); r = rn[1] + GetArraySize(rn); }");
        Assert.Equal("y2", Str(h, "r"));
    }

    [Fact]
    public void EventsCarryDataInOrderAndPostedEventsRunAfterTheirDelay()
    {
        var h = Run(@"string r; void On() { string a = GetEventData(); int b = GetEventData(); r = r + a + b + "";""; }
            void Test() { SetEventHandler(""e"", ""On"", 0); Event(""e"", ""sl"", ""x"", 1); PostEvent(""e"", 500, ""sl"", ""y"", 2); }");
        Assert.Equal("x1;", Str(h, "r"));
        h.It.Advance(400); Assert.Equal("x1;", Str(h, "r"));
        h.It.Advance(200); Assert.Equal("x1;y2;", Str(h, "r"));
    }

    [Fact]
    public void ClearEventsKeepsStaticHandlersButDropsRuntimeOnes()
    {
        var h = Run("#event_handler(\"stat\",\"OnStat\");\nint s; int d; void OnStat(){ s++; } void OnDyn(){ d++; }\n" +
                    "void Test() { SetEventHandler(\"dyn\",\"OnDyn\",0); ClearEvents(); Event(\"stat\"); Event(\"dyn\"); }");
        Assert.Equal("1", Str(h, "s"));
        Assert.Equal("0", Str(h, "d"));
    }

    [Fact]
    public void ObjectAssignmentCopiesTree()
    {
        var h = Run(@"object a; object b; void Test() { a.x.y = 3; b = a; a.x.y = 4; }");
        Assert.Equal("3", Attr(h, "b", "x.y"));
    }

    [Fact]
    public void FloatsPrintWithTenSignificantDigits()
    {
        var h = Run(@"object o; void Test() { o.v = 1.0/3.0; }");
        Assert.Equal("0.3333333433", Attr(h, "o", "v"));   // single precision like the original VM, printed with 10 digits
    }

    [Fact]
    public void StringsHaveNoEscapesBackslashIsLiteral()
    {
        var h = Run("string r; void Test() { r = \"a\\nb\\c\"; }");
        Assert.Equal("a\\nb\\c", Str(h, "r"));
    }

    [Fact]
    public void LocalsAreFunctionWideEvenBeforeTheirDeclaration()
    {
        var h = Run(@"int r; void Test() { r = late + 1; int late = 5; }");
        Assert.Equal("1", Str(h, "r"));       // 'late' exists (0) before its declaration line
    }

    [Fact]
    public void SaveAndRestoreRoundTripOfVariablesTreesAndHandlers()
    {
        var h = Run(@"int n; string s; object o; int arr[2]; void On(){ n = 99; }
            void Test() { n = 5; s = ""hi""; o.a.b = ""deep""; arr[1] = 7; SetEventHandler(""ping"", ""On"", 0); }");
        string file = Path.Combine(dir, "t.sdsave");
        h.It.SaveState(file, new[] { "segA" }, "desc");
        Assert.Equal(new[] { "segA" }, Interp.ReadInfo(file).Segments);

        h.It.SetGlobal("n", "0");                                 // damage the state
        h.It.Globals["o"].Val.Attr!.Clear();
        h.It.ClearDynamicHandlers();
        h.It.RestoreState(file);

        Assert.Equal("5", Str(h, "n"));
        Assert.Equal("hi", Str(h, "s"));
        Assert.Equal("deep", Attr(h, "o", "a.b"));
        Assert.Equal("7", Conv.ToStr(h.It.Globals["arr"].Arr![1].Val));
        h.It.FireEvent("ping");                                   // the run-time handler came back
        Assert.Equal("99", Str(h, "n"));
    }

    [Fact]
    public void StraySemicolonBetweenFunctionHeaderAndBodyStillDefinesTheFunction()
    {
        // original defect in GeneratorUtilite.c: "float Magic(); { return 162.5; }"
        var h = Run("float Magic();\n{ return 162.5; }\nfloat r; void Test() { r = Magic(); }");
        Assert.Equal("162.5", Str(h, "r"));
    }

    [Fact]
    public void Bring2RangeMapsTheValueFromTheSecondRangeOntoTheFirst()
    {
        // the original (utils.c): min1 + (clamp(v, min2, max2) - min2) / (max2 - min2) * (max1 - min1)
        var h = Run(@"object o; void Test() { o.a = Bring2Range(0.0, 1.0, 5.0, 15.0, 10.0); o.b = Bring2Range(0.0, 1.0, 5.0, 15.0, 99.0); o.c = Bring2Range(0.0, 1.0, 5.0, 15.0, -4.0); o.d = Bring2RangeNoCheck(0.0, 1.0, 5.0, 15.0, 25.0); }");
        Assert.Equal("0.5", Attr(h, "o", "a"));
        Assert.Equal("1", Attr(h, "o", "b"));
        Assert.Equal("0", Attr(h, "o", "c"));
        Assert.Equal("2", Attr(h, "o", "d"));
    }

    [Fact]
    public void ReferenceParameterPassedOnToAnotherCallStaysAReference()
    {
        // GenerateMapEncounter(.., ref e1) calls GenerateMapEncounter_Merchant(.., e1): the callee must write through to the original variable
        var h = Run(@"int x; void Inner(ref r) { r = 5; } void Outer(ref r) { Inner(r); } void Test() { x = -1; Outer(&x); }");
        Assert.Equal("5", Str(h, "x"));
    }

    [Fact]
    public void NumericAttributeNamesAfterADynamicNameAreMembers()
    {
        // Encounters.c: rEnc.(sRank).1 / .0 - the lexer must not read ".1" as a float
        var h = Run(@"object o; string r; void Test() { string k = ""Rank.5""; o.Rank.5.1 = ""x""; o.Rank.5.0 = ""y""; r = o.(k).1 + o.(k).0; }");
        Assert.Equal("xy", Str(h, "r"));
    }

    [Fact]
    public void RefToScalarAssignedFromAnAttributeWritesTheValue()
    {
        var h = Run(@"object o; int a; int b; void Get(ref x, ref y) { x = o.p.1; y = o.p.0; } void Test() { o.p.1 = 7; o.p.0 = 3; Get(&a, &b); }");
        Assert.Equal("7", Str(h, "a"));
        Assert.Equal("3", Str(h, "b"));
    }

    [Fact]
    public void FindClassBindsALocalObjectToTheLiveEntity()
    {
        // MakeSailDmg (BattleInterface.c): object objSail; if (!FindClass(&objSail, "sail")) return; SendMessage(&objSail, ...)
        var h = Run(@"object Sail; int r; int n; void Test() { CreateEntity(&Sail, ""Sail""); object o; r = FindClass(&o, ""sail""); n = FindClass(&o, ""nothing""); }");
        Assert.Equal("1", Str(h, "r"));
        Assert.Equal("0", Str(h, "n"));
    }

    [Fact]
    public void FindClassNextWalksAllEntitiesOfTheClass()
    {
        // Abordage_ReloadEndFade (AIAbordage.c): if (FindClass(&arModel, "modelr")) { ...; while (FindClassNext(&arModel)) { ... } }
        var h = Run(@"object A; object B; object C; int n; void Test() { CreateEntity(&A, ""modelr""); CreateEntity(&B, ""model""); CreateEntity(&C, ""modelr"");
            aref m; if (FindClass(&m, ""modelr"")) { n = 1; while (FindClassNext(&m)) { n++; } } }");
        Assert.Equal("2", Str(h, "n"));
    }
}
