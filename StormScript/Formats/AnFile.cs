using System.Buffers.Binary;

namespace StormScript.Formats;

/// <summary>
/// Storm Engine .an skeletal animation: i32 frames, i32 joints, f32 fps; parent[joints] (i32), jointPos[joints] (3 x f32,
/// local offset to the parent in the bind pose), rootPos[frames] (3 x f32), then a quaternion (x,y,z,w) per joint per
/// frame, joint-major. Direct3D (left-handed) space.
/// </summary>
public sealed class AnFile
{
    public int NFrames, NJoints;
    public float Fps;
    public int[] Parents = [];
    public float[] JointPos = [];       // joints * 3
    public float[] RootPos = [];        // frames * 3
    public float[] Quat = [];           // joints * frames * 4

    public static AnFile Parse(byte[] d)
    {
        var a = new AnFile
        {
            NFrames = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(0)),
            NJoints = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(4)),
            Fps = BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(8)),
        };
        long need = 12L + 4L * a.NJoints + 12L * a.NJoints + 12L * a.NFrames + 16L * a.NJoints * a.NFrames;
        if (a.NFrames <= 0 || a.NJoints <= 0 || need != d.Length)
            throw new InvalidDataException($"bad .an: {a.NFrames} frames, {a.NJoints} joints, size {d.Length} != {need}");
        int p = 12;
        a.Parents = new int[a.NJoints];
        for (int i = 0; i < a.NJoints; i++, p += 4) a.Parents[i] = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p));
        a.JointPos = ReadFloats(d, ref p, a.NJoints * 3);
        a.RootPos = ReadFloats(d, ref p, a.NFrames * 3);
        a.Quat = ReadFloats(d, ref p, a.NJoints * a.NFrames * 4);
        return a;
    }

    static float[] ReadFloats(byte[] d, ref int p, int n)
    {
        var r = new float[n];
        Buffer.BlockCopy(d, p, r, 0, n * 4);     // little-endian host assumed (x86/x64/ARM)
        p += n * 4;
        return r;
    }
}

/// <summary>Named actions from the .ani text file: [name] start_time/end_time (frames), speed, type, data lines.</summary>
public sealed class AniActions
{
    public sealed class Action
    {
        public string Name = "";
        public int Start, End;
        public float Speed = 1f;
        public bool Reverse;
        public Dictionary<string, string> Data = new(StringComparer.OrdinalIgnoreCase);
    }

    public string AnimationFile = "";
    public Dictionary<string, Action> Actions = new(StringComparer.OrdinalIgnoreCase);

    public static AniActions Parse(string text)
    {
        var r = new AniActions();
        Action? cur = null;
        foreach (var raw in text.Split('\n'))
        {
            string line = raw;
            int sc = line.IndexOf(';');
            if (sc >= 0) line = line.Substring(0, sc);
            line = line.Trim();
            if (line.Length == 0) continue;
            if (line[0] == '[' && line.EndsWith(']'))
            {
                cur = new Action { Name = line.Substring(1, line.Length - 2).Trim() };
                r.Actions[cur.Name] = cur;
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
            if (cur == null)
            {
                if (k.Equals("animation", StringComparison.OrdinalIgnoreCase)) r.AnimationFile = v;
                continue;
            }
            switch (k.ToLowerInvariant())
            {
                case "start_time": cur.Start = (int)float.Parse(v, System.Globalization.CultureInfo.InvariantCulture); break;
                case "end_time": cur.End = (int)float.Parse(v, System.Globalization.CultureInfo.InvariantCulture); break;
                case "speed": cur.Speed = float.Parse(v, System.Globalization.CultureInfo.InvariantCulture); break;
                case "type": cur.Reverse = v.Equals("reverse", StringComparison.OrdinalIgnoreCase); break;
                case "data":
                {
                    var parts = v.Split(',');
                    if (parts.Length >= 2) cur.Data[parts[0].Trim().Trim('"')] = parts[1].Trim().Trim('"');
                    break;
                }
            }
        }
        return r;
    }
}
