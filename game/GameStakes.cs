using System;
using System.Linq;
using Godot;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// Dice and card stakes. The dialog scripts (DIALOGS/Habitue_dialog.c, nodes Dice_Node_* / Cards_Node_*) offer only two fixed stakes (dice 50 / 200, cards 100 / 500) and
/// store the choice in GenQuest.Dice.iRate / GenQuest.Cards.iRate right before LaunchDiceGame / LaunchCardsGame. The scripts stay untouched: when the game screen is
/// about to start the opponent may raise the stake to a higher one, up to 7000, scaled by the hero's purse and reputation (rich partners put more on the table).
/// The raise is announced in the message log; the game screens work with any rate (the chip pictures exist for 50 / 200 / 500 / 2000 and 100 / 500 / 1000 / 5000).
/// </summary>
public sealed class GameStakes
{
    static readonly int[] Ladder = { 50, 100, 200, 500, 1000, 2000, 3500, 5000, 7000 };
    public const int MaxStake = 7000;
    readonly Interp it;
    readonly Random rng = new();
    public Action<string>? Say;

    public GameStakes(Interp it)
    {
        this.it = it;
        it.WatchedCalls.Add("LaunchDiceGame");
        it.WatchedCalls.Add("LaunchCardsGame");
        it.CallHook += n =>
        {
            if (n.Equals("LaunchDiceGame", StringComparison.OrdinalIgnoreCase)) Raise("Dice");
            else if (n.Equals("LaunchCardsGame", StringComparison.OrdinalIgnoreCase)) Raise("Cards");
        };
    }

    static float F(Attr? a) => Conv.ParseFloatPrefix(a?.Value ?? "0");

    /// <summary>The highest stake the hero's purse and name allow: money x (0.5 + reputation / 100) / 9, never above 7000.</summary>
    public static int Cap(float heroMoney, float reputation)
        => (int)Math.Min(MaxStake, heroMoney * (0.5f + Math.Clamp(reputation, 0f, 100f) / 100f) / 9f);

    /// <summary>The stake after the raise: one of the higher ladder steps up to <paramref name="cap"/> (or the original one), chosen with <paramref name="roll"/> in [0,1) and <paramref name="pick"/> in [0,1).</summary>
    public static int Choose(int rate, int cap, double roll, double pick)
    {
        var higher = Ladder.Where(r => r > rate && r <= cap).ToArray();
        if (higher.Length == 0 || roll >= 0.6) return rate;                       // 60 % of the games are raised when the purse allows it
        return higher[(int)(pick * higher.Length) % higher.Length];
    }

    void Raise(string kind)
    {
        if (!it.Globals.TryGetValue("Characters", out var cs) || cs.Arr is not { Length: > 0 } || cs.Arr[0].Val.Attr is not { } hero) return;
        var q = hero.Find("GenQuest." + kind, false);
        if (q == null) return;
        int rate = (int)F(q.Get("iRate"));
        int npcIdx = (int)F(q.Get("npcharIdx"));
        if (rate <= 0 || npcIdx < 0 || npcIdx >= cs.Arr.Length || cs.Arr[npcIdx].Val.Attr is not { } npc) return;
        int cap = Cap(F(hero.Get("Money")), F(hero.Get("reputation")));
        int now = Choose(rate, cap, rng.NextDouble(), rng.NextDouble());
        GD.Print($"[stakes] {kind}: offered {rate}, cap {cap}, played {now}");
        if (now == rate) return;
        q.GetOrCreate("iRate").Value = now.ToString();
        var money = npc.GetOrCreate("Money");
        if (F(money) < now * 8) money.Value = (now * 8).ToString();                     // the one who raises can afford it
        Say?.Invoke($"Партнер піднімає ставку: тепер по {now} монет");
    }
}
