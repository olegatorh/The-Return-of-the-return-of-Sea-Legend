namespace StormScript.Runtime;

/// <summary>Event dispatch: SetEventHandler / Event / PostEvent / GetEventData, plus simulated time.</summary>
public sealed partial class Interp
{
    sealed class EventCtx(List<V> data) { public List<V> Data = data; public int Pos; }
    sealed record Posted(long Due, long Seq, string Name, List<V> Data);

    public readonly Dictionary<string, List<(string fn, int prio)>> Handlers = new(StringComparer.OrdinalIgnoreCase);
    readonly Stack<EventCtx> eventStack = new();
    readonly List<Posted> postQueue = new();
    long postSeq;
    public long Now;                      // simulated milliseconds
    public readonly Dictionary<string, int> EventsFired = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Handlers declared by "#event_handler" directives: they belong to the loaded script segments and survive ClearEvents().</summary>
    readonly HashSet<(string, string)> staticHandlers = new();

    public void SetStaticHandler(string evt, string fn)
    {
        staticHandlers.Add((evt.ToLowerInvariant(), fn.ToLowerInvariant()));
        SetHandler(evt, fn);
    }

    /// <summary>ClearEvents(): drops every handler that was set at run time with SetEventHandler.</summary>
    public void ClearDynamicHandlers()
    {
        foreach (var (evt, list) in Handlers.ToArray())
        {
            list.RemoveAll(h => !staticHandlers.Contains((evt.ToLowerInvariant(), h.fn.ToLowerInvariant())));
            if (list.Count == 0) Handlers.Remove(evt);
        }
    }

    public void SetHandler(string evt, string fn, int prio = 0)
    {
        if (!Handlers.TryGetValue(evt, out var l)) Handlers[evt] = l = new();
        if (!l.Any(x => string.Equals(x.fn, fn, StringComparison.OrdinalIgnoreCase)))
            l.Add((fn, prio));
    }

    public void DelHandler(string evt, string fn)
    {
        if (Handlers.TryGetValue(evt, out var l))
            l.RemoveAll(x => string.Equals(x.fn, fn, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Synchronous event. Returns the result of the last handler.</summary>
    public V FireEvent(string name, List<V>? data = null)
    {
        EventsFired[name] = EventsFired.GetValueOrDefault(name) + 1;
        if (!Handlers.TryGetValue(name, out var list) || list.Count == 0) return V.Void;
        V result = V.Void;
        var ctx = new EventCtx(data ?? new List<V>());
        eventStack.Push(ctx);
        try
        {
            foreach (var (fn, _) in list.ToArray())
            {
                ctx.Pos = 0;
                try { result = CallByName(fn, new List<V>(), null); }
                catch (ScriptError e) { Error($"in handler {fn} of event {name}: {e.Message}"); }
            }
        }
        finally { eventStack.Pop(); }
        return result;
    }

    public void PostEventInternal(string name, int delayMs, List<V> data)
        => postQueue.Add(new Posted(Now + Math.Max(0, delayMs), postSeq++, name, data));

    public int PendingPosted => postQueue.Count;

    public void ClearPosted() => postQueue.Clear();

    /// <summary>Advance simulated time and run every posted event that became due.</summary>
    public int Advance(int ms)
    {
        Now += ms;
        int ran = 0;
        while (ran < 100_000)
        {
            Posted? next = null;
            foreach (var p in postQueue)
                if (p.Due <= Now && (next == null || p.Due < next.Due || (p.Due == next.Due && p.Seq < next.Seq))) next = p;
            if (next == null) break;
            postQueue.Remove(next);
            FireEvent(next.Name, next.Data);
            ran++;
        }
        return ran;
    }

    internal V NextEventData()
    {
        if (eventStack.Count == 0) { Error("GetEventData outside an event"); return V.Void; }
        var c = eventStack.Peek();
        if (c.Pos >= c.Data.Count) { Error("GetEventData: no more data"); return V.Void; }
        return c.Data[c.Pos++];
    }
}
