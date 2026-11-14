using System;

namespace SeaDogs;

/// <summary>
/// The goods carousel of the cargo exchange between two ships (INI/INTERFACES/transfer_goods.ini, script transfer_goods.c): FillScroll puts the goods that at least one
/// of the two ships carries first and the rest after them with spec2 = true (the original draws those dimmed, still selectable). The owner wants the goods nobody carries
/// not shown at all. The script's indexing (pic order -> Goods[] via goodsNum) is untouched: the hidden entries only sit behind the visible ones, so the strip simply ends earlier.
/// </summary>
public static class GoodsFilter
{
    /// <summary>The goods list of the exchange screen: the node GOODSLIST of transfer_goods.ini only (the shop screens keep their dimmed entries).</summary>
    public static bool HidesEmpty(string iniFile, string node)
        => node.Equals("GOODSLIST", StringComparison.OrdinalIgnoreCase)
           && iniFile.Replace('/', '\\').EndsWith("transfer_goods.ini", StringComparison.OrdinalIgnoreCase);

    /// <summary>How many leading pictures are shown: those before the first dimmed one (spec2 = true), at most <paramref name="listSize"/>; never fewer than one.</summary>
    public static int VisibleCount(int listSize, Func<int, bool> isDimmed)
    {
        int n = 0;
        while (n < listSize && !isDimmed(n)) n++;
        return Math.Max(1, Math.Min(n, listSize));
    }
}
