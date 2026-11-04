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
    sealed class Alias { public List<(string file, float vol)> Files = new(); public float Volume = 1f; }

    sealed class Voice
    {
        public int Id;
        public AudioStreamPlayer? Player;
        public string Name = "";
        public bool Looped, Music, Started;
        public float Volume = 1f;
        public AudioStream? Stream;
    }

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
        LoadAliases("sound_alias.ini");
        LoadAliases("music_alias.ini");
        LoadSchemes();
    }

    string SoundsDir => GameAssets.Resource("Sounds");

    void LoadAliases(string file)
    {
        string path = GameAssets.Resource("INI\\ALIASES\\" + file);
        if (!File.Exists(path)) return;
        var ini = IniFile.Load(path);
        foreach (var (name, sec) in ini.Sections)
        {
            var a = new Alias();
            foreach (var line in sec.All("name"))
            {
                var p = line.Split(',');
                float v = p.Length > 1 && float.TryParse(p[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var vv) ? vv : 1f;
                a.Files.Add((p[0].Trim(), v));
            }
            if (float.TryParse(sec.Get("volume"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var vol)) a.Volume = vol;
            if (a.Files.Count > 0) aliases[name] = a;
        }
    }

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
    int Create(string name, bool music, bool looped, float vol, bool startNow)
    {
        var (file, avol) = Resolve(name);
        if (file == null)
        {
            if (warned.Add(name)) GD.Print("[sound] not found: " + name);
            return nextId++;      // a valid id, silent
        }
        if (System.Environment.GetEnvironmentVariable("SEADOGS_SOUNDLOG") != null) GD.Print($"[sound] play '{name}' -> {Path.GetFileName(file)} music={music} loop={looped} now={startNow}");
        var v = new Voice { Id = nextId++, Name = name, Looped = looped, Music = music, Volume = vol * avol, Stream = Load(file, looped) };
        voices[v.Id] = v;
        if (v.Stream != null)
        {
            v.Player = new AudioStreamPlayer { Stream = v.Stream };
            AddChild(v.Player);
            ApplyVolume(v);
            v.Player.Finished += () => { if (!v.Looped) Release(v.Id); };
        }
        if (startNow) Start(v);
        return v.Id;
    }

    void Start(Voice v)
    {
        if (!Enabled || v.Player == null) return;
        v.Started = true;
        if (!v.Player.Playing) v.Player.Play();
    }

    void ApplyVolume(Voice v)
    {
        if (v.Player == null) return;
        float vol = v.Volume * (v.Music ? musicVolume : fxVolume);
        v.Player.VolumeDb = vol <= 0.001f ? -80f : Mathf.LinearToDb(Math.Min(vol, 1.5f));
    }

    void Release(int id)
    {
        if (id == 0) { foreach (var v in voices.Values.ToList()) Release(v.Id); return; }
        if (voices.Remove(id, out var vo)) vo.Player?.QueueFree();
    }

    void Stop(int id, float fadeMs)
    {
        foreach (var v in (id == 0 ? voices.Values.ToList() : voices.Values.Where(x => x.Id == id).ToList()))
        {
            if (v.Player == null) continue;
            if (fadeMs > 50)
            {
                var tw = CreateTween();
                tw.TweenProperty(v.Player, "volume_db", -80f, fadeMs / 1000f);
                tw.TweenCallback(Callable.From(() => { if (v.Player != null && IsInstanceValid(v.Player)) v.Player.Stop(); }));
            }
            else v.Player.Stop();
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
                int id = Create(S(1), music, I(5) != 0, 1f, !justCache);
                return V.Int(id);
            }
            case 77004: Stop(I(1), F(2)); return V.Int(0);                    // STOP id, fade
            case 77005: Release(I(1)); return V.Int(0);                       // RELEASE
            case 77012:                                                       // RESUME id, fade
                if (voices.TryGetValue(I(1), out var rv)) { Start(rv); if (rv.Player != null && F(2) > 50) { rv.Player.VolumeDb = -60f; ApplyVolume(rv); } }
                else if (I(1) == 0) foreach (var v in voices.Values) if (v.Looped) Start(v);
                return V.Int(0);
            case 77011: if (voices.TryGetValue(I(1), out var rs) && rs.Player != null) rs.Player.Play(); return V.Int(0);
            case 77008:                                                       // SET_VOLUME id, volume
                if (voices.TryGetValue(I(1), out var sv)) { sv.Volume = F(2); ApplyVolume(sv); }
                return V.Int(0);
            case 77009: return V.Int(voices.TryGetValue(I(1), out var pv) && pv.Player?.Playing == true ? 1 : 0);
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
