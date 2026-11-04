namespace StormScript.Runtime;

/// <summary>
/// Headless stand-ins for C++ engine classes. Each one implements just enough behaviour to let the
/// scripts progress; they are the seed of the real implementations in the new engine.
/// </summary>
public static class EngineStubs
{
    public static void Install(Interp it)
    {
        // video player: "play file" message -> the video "ends" shortly after
        Func<Entity, string, V[], V> video = (e, fmt, d) =>
        {
            if (d.Length > 0 && Conv.ToInt(d[0]) == 49000) it.PostEventInternal("ievntEndVideo", 50, new List<V>());
            return V.Int(0);
        };
        it.EntityHandlers["CAviPlayer"] = video;
        it.EntityHandlers["WMVideoPlay"] = video;
    }
}
