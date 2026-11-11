using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// The engine's "Sound" class. The script plays sounds by alias (INI/ALIASES/sound_alias.ini, music_alias.ini: one alias = several files,
/// one is chosen at random) or by file name below RESOURCE/Sounds, and sets "schemes" (INI/sound_scheme.ini: ambient channels that play a
/// sound at random intervals, e.g. birds in the jungle). WAV files are PCM; music is OGG.
/// </summary>
public partial class SoundView : Node
{
    /// <summary>One [section] of an INI/ALIASES file. minDistance/maxDistance are the DirectSound 3D distances: full volume up to min, 1/d fall-off, silent past max.</summary>
    sealed class Alias { public List<(string file, float vol)> Files = new(); public float Volume = 1f; public float MinDist = -1f, MaxDist = -1f; }

    sealed class Voice
    {
        public int Id;
        public AudioStreamPlayer? Player;
        public AudioStreamPlayer3D? Player3D;      // a 3D sound at a world position (SOUND_WAV_3D with an alias that has distances)
        public string Name = "";
        public bool Looped, Music, Speech, Started;
        public float Volume = 1f;
        public AudioStream? Stream;
        public Node? Node => (Node?)Player ?? Player3D;
        public bool Playing => Player?.Playing == true || Player3D?.Playing == true;
        public void Play() { Player?.Play(); Player3D?.Play(); }
        public void Halt() { Player?.Stop(); Player3D?.Stop(); }
        public float Db { set { if (Player != null) Player.VolumeDb = value; if (Player3D != null) Player3D.VolumeDb = value; } }
    }

    /// <summary>The sound service (created by the first script message to "Sound"): engine classes play their own sounds through it.</summary>
    public static SoundView? Instance;

    sealed class Channel { public string Sound = ""; public float Min, Max, Volume; public bool Loop; public double Timer; public int LoopVoice; }

    readonly Dictionary<string, Alias> aliases = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, List<Channel>> schemes = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<int, Voice> voices = new();
    readonly List<Channel> active = new();
    readonly Random rng = new();
    readonly Dictionary<string, AudioStream?> cache = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, string>? fileIndex;
    int nextId = 1;
    float fxVolume = 1f, musicVolume = 1f, speechVolume = 1f;
    static readonly HashSet<string> warned = new();
    public bool Enabled = true;
    public Interp? it0;

    public SoundView()
    {
        Name = "Sound";
        Instance = this;
        // the original SoundService reads every ini of INI/ALIASES (sound_alias, music_alias, Greetings_alias = NPC voices, max)
        string dir = GameAssets.Resource("INI\\ALIASES");
        var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.ini").Select(Path.GetFileName).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList() : new List<string?>();
        foreach (var f in files) LoadAliases(f!);
        LoadSchemes();
    }

    public override void _ExitTree() { if (Instance == this) Instance = null; }

    string SoundsDir => GameAssets.Resource("Sounds");

    static float ParseF(string? s, float def)
    {
        if (s == null) return def;
        int sc = s.IndexOf(';'); if (sc >= 0) s = s.Substring(0, sc);
        return float.TryParse(s.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : def;
    }

    void LoadAliases(string file)
    {
        string path = GameAssets.Resource("INI\\ALIASES\\" + file);
        if (!File.Exists(path)) return;
        var ini = IniFile.Load(path);
        foreach (var (name, sec) in ini.Sections)
        {
            var a = new Alias();
            foreach (var raw in sec.All("name"))
            {
                string line = raw; int sc = line.IndexOf(';'); if (sc >= 0) line = line.Substring(0, sc);
                var p = line.Split(',');
                if (p[0].Trim().Length == 0) continue;
                a.Files.Add((p[0].Trim(), p.Length > 1 ? ParseF(p[1], 1f) : 1f));
            }
            a.Volume = ParseF(sec.Get("volume"), 1f);
            a.MinDist = ParseF(sec.Get("minDistance"), -1f);
            a.MaxDist = ParseF(sec.Get("maxDistance"), -1f);
            if (a.Files.Count > 0) aliases[name.Trim()] = a;
        }
    }

    public bool HasAlias(string name) => aliases.ContainsKey(name);
    public int AliasCount => aliases.Count;

    void LoadSchemes()
    {
        string path = GameAssets.Resource("INI\\sound_scheme.ini");
        if (!File.Exists(path)) return;
        var ini = IniFile.Load(path);
        foreach (var (name, sec) in ini.Sections)
        {
            var list = new List<Channel>();
            foreach (var (k, v) in sec.Lines)
            {
                bool loop = k.Equals("ch_loop", StringComparison.OrdinalIgnoreCase);
                if (!loop && !k.Equals("ch", StringComparison.OrdinalIgnoreCase)) continue;
                var p = v.Split(',').Select(x => x.Trim()).ToArray();
                if (p.Length < 4) continue;
                float F(string s) => float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : 0f;
                list.Add(new Channel { Sound = p[0], Min = F(p[1]), Max = F(p[2]), Volume = F(p[3]), Loop = loop });
            }
            schemes[name] = list;
        }
    }

    // ------------------------------------------------------------------ files
    string? FindFile(string name)
    {
        string rel = name.Replace('/', '\\');
        string direct = Path.Combine(SoundsDir, rel.Replace('\\', Path.DirectorySeparatorChar));
        foreach (var ext in new[] { "", ".wav", ".ogg" })
            if (File.Exists(direct + ext)) return direct + ext;
        if (fileIndex == null)
        {
            fileIndex = new(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.EnumerateFiles(SoundsDir, "*.*", SearchOption.AllDirectories))
                if (f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
                {
                    fileIndex.TryAdd(Path.GetFileName(f), f);
                    fileIndex.TryAdd(Path.GetFileNameWithoutExtension(f), f);
                }
        }
        string leaf = Path.GetFileName(rel);
        return fileIndex.TryGetValue(leaf, out var r) ? r : null;
    }

    (string? file, float vol) Resolve(string name)
    {
        if (aliases.TryGetValue(name, out var a))
        {
            var (f, v) = a.Files[rng.Next(a.Files.Count)];
            return (FindFile(f), v * a.Volume);
        }
        return (FindFile(name), 1f);
    }

    AudioStream? Load(string file, bool loop)
    {
        string key = file + (loop ? "|loop" : "");
        if (cache.TryGetValue(key, out var s)) return s;
        AudioStream? r = null;
        try
        {
            if (file.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
            {
                var o = AudioStreamOggVorbis.LoadFromFile(file);
                if (o != null) o.Loop = loop;
                r = o;
            }
            else r = LoadWav(File.ReadAllBytes(file), loop);
        }
        catch (Exception e) { GD.PrintErr($"[sound] {file}: {e.Message}"); }
        cache[key] = r;
        return r;
    }

    static AudioStreamWav? LoadWav(byte[] d, bool loop)
    {
        if (d.Length < 44 || d[0] != 'R' || d[1] != 'I') return null;
        int pos = 12, channels = 1, rate = 22050, bits = 16;
        byte[]? data = null;
        while (pos + 8 <= d.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(d, pos, 4);
            int size = BitConverter.ToInt32(d, pos + 4);
            if (id == "fmt ") { channels = BitConverter.ToInt16(d, pos + 10); rate = BitConverter.ToInt32(d, pos + 12); bits = BitConverter.ToInt16(d, pos + 22); }
            else if (id == "data") { data = new byte[Math.Min(size, d.Length - pos - 8)]; Array.Copy(d, pos + 8, data, 0, data.Length); break; }
            pos += 8 + size + (size & 1);
        }
        if (data == null) return null;
        var w = new AudioStreamWav { MixRate = rate, Stereo = channels == 2 };
        if (bits == 8)
        {
            for (int i = 0; i < data.Length; i++) data[i] ^= 0x80;     // unsigned -> signed
            w.Format = AudioStreamWav.FormatEnum.Format8Bits;
        }
        else w.Format = AudioStreamWav.FormatEnum.Format16Bits;
        w.Data = data;
        if (loop)
        {
            w.LoopMode = AudioStreamWav.LoopModeEnum.Forward;
            w.LoopBegin = 0;
            w.LoopEnd = data.Length / (bits / 8) / channels;
        }
        return w;
    }

    // ------------------------------------------------------------------ voices
    /// <summary>Creates a voice. <paramref name="pos"/> = a world position in script (Direct3D) space for a 3D sound: it is attenuated by the alias
    /// minDistance/maxDistance like the original DirectSound 3D buffers (aliases without distances stay plain stereo, as before).</summary>
    int Create(string name, bool music, bool looped, float vol, bool startNow, Vector3? pos = null, bool speech = false)
    {
        var (file, avol) = Resolve(name);
        if (file == null)
        {
            if (warned.Add(name)) GD.Print("[sound] not found: " + name);
            return nextId++;      // a valid id, silent
        }
        aliases.TryGetValue(name, out var al);
        Vector3? gpos = pos is { } p && al != null && al.MaxDist > 0f ? new Vector3(p.X, p.Y, -p.Z) : null;
        // a one-shot 3D sound out of hearing range is not started at all (many walking people would otherwise create voices every step)
        if (gpos is { } g0 && !looped && GetViewport()?.GetCamera3D() is { } cam && cam.GlobalPosition.DistanceTo(g0) > al!.MaxDist) return nextId++;
        if (System.Environment.GetEnvironmentVariable("SEADOGS_SOUNDLOG") != null)
            GD.Print($"[sound] t={Godot.Time.GetTicksMsec()} play '{name}' -> {Path.GetFileName(file)} music={music} loop={looped} now={startNow}" + (speech ? " speech" : "") + (gpos is { } gp ? FormattableString.Invariant($" 3d=({pos!.Value.X:0.0} {pos.Value.Y:0.0} {pos.Value.Z:0.0}) min={al!.MinDist} max={al.MaxDist}") : ""));
        var v = new Voice { Id = nextId++, Name = name, Looped = looped, Music = music, Speech = speech, Volume = vol * avol, Stream = Load(file, looped) };
        voices[v.Id] = v;
        if (v.Stream != null)
        {
            if (gpos is { } g)
            {
                v.Player3D = new AudioStreamPlayer3D
                {
                    Stream = v.Stream, Position = g, DopplerTracking = AudioStreamPlayer3D.DopplerTrackingEnum.Disabled,
                    AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.InverseDistance,
                    UnitSize = Math.Max(al!.MinDist, 0.3f), MaxDistance = al.MaxDist, MaxDb = 0f,
                };
                AddChild(v.Player3D);
                v.Player3D.Finished += () => { if (!v.Looped) Release(v.Id); };
            }
            else
            {
                v.Player = new AudioStreamPlayer { Stream = v.Stream };
                AddChild(v.Player);
                v.Player.Finished += () => { if (!v.Looped) Release(v.Id); };
            }
            ApplyVolume(v);
        }
        if (startNow) Start(v);
        return v.Id;
    }

    // ------------------------------------------------------------------ engine-side API (characters, dialog)
    /// <summary>A 3D effect at a script-space position (VOLUME_FX), e.g. a character's step or sword.</summary>
    /// <summary>True while a voice started with Play3D/Create is still alive (one-shots are released when they finish).</summary>
    public bool VoiceAlive(int id) => id > 0 && voices.ContainsKey(id);

    public int Play3D(string name, float x, float y, float z) => Create(name, false, false, 1f, true, new Vector3(x, y, z));

    /// <summary>A stereo voice line at the speech volume (the dialog's NPC greeting).</summary>
    public int PlaySpeech(string name) => Create(name, false, false, 1f, true, null, true);

    public void StopVoice(int id) { if (id > 0) Stop(id, 0); }

    void Start(Voice v)
    {
        if (!Enabled || v.Node == null) return;
        v.Started = true;
        if (!v.Playing) v.Play();
    }

    void ApplyVolume(Voice v)
    {
        if (v.Node == null) return;
        float vol = v.Volume * (v.Music ? musicVolume : v.Speech ? speechVolume : fxVolume);
        v.Db = vol <= 0.001f ? -80f : Mathf.LinearToDb(Math.Min(vol, 1.5f));
    }

    void Release(int id)
    {
        if (id == 0) { foreach (var v in voices.Values.ToList()) Release(v.Id); return; }
        if (voices.Remove(id, out var vo)) vo.Node?.QueueFree();
    }

    void Stop(int id, float fadeMs)
    {
        foreach (var v in (id == 0 ? voices.Values.ToList() : voices.Values.Where(x => x.Id == id).ToList()))
        {
            if (v.Node is not { } node) continue;
            if (fadeMs > 50)
            {
                var tw = CreateTween();
                tw.TweenProperty(node, "volume_db", -80f, fadeMs / 1000f);
                tw.TweenCallback(Callable.From(() => { if (IsInstanceValid(node)) v.Halt(); }));
            }
            else v.Halt();
            v.Started = false;
        }
    }

    // ------------------------------------------------------------------ script messages
    public V OnMessage(string fmt, V[] d)
    {
        int msg = d.Length > 0 ? Conv.ToInt(d[0]) : 0;
        string S(int i) => i < d.Length ? Conv.ToStr(d[i]) : "";
        int I(int i) => i < d.Length ? Conv.ToInt(d[i]) : 0;
        float F(int i) => i < d.Length ? Conv.ToFloat(d[i]) : 0f;
        switch (msg)
        {
            case 77003:                                  // PLAY: name, type, volume class, just-cache, looped, cached, [fade, silence | x,y,z]
            {
                bool music = I(3) == 2 || I(2) == 2;
                bool justCache = I(4) != 0;
                // SOUND_WAV_3D (1) / SOUND_MP3_3D (3) with "fff" x, y, z after the fade time: a positioned sound
                Vector3? pos = (I(2) == 1 || I(2) == 3) && d.Length >= 11 ? new Vector3(F(8), F(9), F(10)) : null;
                int id = Create(S(1), music, I(5) != 0, 1f, !justCache, pos, I(3) == 3);
                return V.Int(id);
            }
            case 77004: Stop(I(1), F(2)); return V.Int(0);                    // STOP id, fade
            case 77005: Release(I(1)); return V.Int(0);                       // RELEASE
            case 77012:                                                       // RESUME id, fade
                if (voices.TryGetValue(I(1), out var rv)) { Start(rv); if (rv.Node != null && F(2) > 50) { rv.Db = -60f; ApplyVolume(rv); } }
                else if (I(1) == 0) foreach (var v in voices.Values) if (v.Looped) Start(v);
                return V.Int(0);
            case 77011: if (voices.TryGetValue(I(1), out var rs)) rs.Play(); return V.Int(0);
            case 77008:                                                       // SET_VOLUME id, volume
                if (voices.TryGetValue(I(1), out var sv)) { sv.Volume = F(2); ApplyVolume(sv); }
                return V.Int(0);
            case 77009: return V.Int(voices.TryGetValue(I(1), out var pv) && pv.Playing ? 1 : 0);
            case 77022:                                                       // GET_MASTER_VOLUME "eee": fx, music, speech
                it0?.WriteRef(d[1], V.Float(fxVolume)); it0?.WriteRef(d[2], V.Float(musicVolume)); it0?.WriteRef(d[3], V.Float(speechVolume));
                return V.Int(0);
            case 77021: fxVolume = F(1); musicVolume = F(2); speechVolume = d.Length > 3 ? F(3) : speechVolume; foreach (var v in voices.Values) ApplyVolume(v); return V.Int(0);
            case 77013: ClearScheme(); return V.Int(0);                       // SCHEME_RESET
            case 77014: ClearScheme(); AddScheme(S(1)); return V.Int(0);      // SCHEME_SET
            case 77015: AddScheme(S(1)); return V.Int(0);                     // SCHEME_ADD
            case 77016: Enabled = I(1) != 0; if (!Enabled) Stop(0, 0); return V.Int(0);
        }
        return V.Int(0);
    }

    void ClearScheme()
    {
        foreach (var c in active) if (c.LoopVoice != 0) Release(c.LoopVoice);
        active.Clear();
    }

    void AddScheme(string name)
    {
        if (!schemes.TryGetValue(name, out var list)) { if (warned.Add("scheme " + name)) GD.Print("[sound] unknown scheme " + name); return; }
        foreach (var c in list)
        {
            var copy = new Channel { Sound = c.Sound, Min = c.Min, Max = c.Max, Volume = c.Volume, Loop = c.Loop };
            copy.Timer = copy.Loop ? 0 : copy.Min + rng.NextDouble() * Math.Max(0, copy.Max - copy.Min);
            active.Add(copy);
        }
    }

    public override void _Process(double delta)
    {
        if (!Enabled) return;
        foreach (var c in active)
        {
            if (c.Loop)
            {
                if (c.LoopVoice == 0) { c.LoopVoice = Create(c.Sound, false, true, c.Volume, true); }
                continue;
            }
            c.Timer -= delta;
            if (c.Timer > 0) continue;
            Create(c.Sound, false, false, c.Volume, true);
            c.Timer = c.Min + rng.NextDouble() * Math.Max(0, c.Max - c.Min);
        }
    }
}
