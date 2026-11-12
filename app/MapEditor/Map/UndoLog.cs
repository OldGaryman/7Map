using System.Collections;

namespace MapEditor.Map;

/// Old values of one edit step for one kind of data (heights, road mask, water …), at most one entry per index:
/// only the value before the first change of the step is kept (later changes of the same cell in the same step
/// need nothing, undoing goes back to this value anyway). Big generator steps (cities, roads, wilderness) change many
/// cells several times; before, every change was stored (8 bytes each), now 4 + sizeof(T) bytes per changed cell.
/// Which cells are already stored: a hash set for small steps, a bit field over the whole map from 64k entries on;
/// both are released when the step is finished (Seal).
public sealed class UndoLog<T> : IEnumerable<(int i, T v)> where T : struct
{
    const int Chunk = 1 << 16;
    const int SetLimit = 1 << 16;
    readonly List<int[]> idx = new();
    readonly List<T[]> val = new();
    readonly int capacity;   // number of possible indices (for the bit field)
    HashSet<int> seenSet = new();
    ulong[] seenBits;
    bool sealedLog;
    public int Count { get; private set; }

    public UndoLog(int capacity) => this.capacity = capacity;

    /// remember the old value of index i, if not already remembered in this step
    public void Add(int i, T old)
    {
        if (sealedLog) throw new InvalidOperationException("UndoLog: step already finished.");
        if (seenBits != null)
        {
            ref ulong word = ref seenBits[i >> 6];
            ulong bit = 1UL << (i & 63);
            if ((word & bit) != 0) return;
            word |= bit;
        }
        else if (seenSet != null)
        {
            if (!seenSet.Add(i)) return;
            if (seenSet.Count > SetLimit && capacity > 0)
            {
                seenBits = new ulong[(capacity + 63) / 64];
                foreach (int k in seenSet) seenBits[k >> 6] |= 1UL << (k & 63);
                seenSet = null;
            }
        }
        int c = Count % Chunk;
        if (c == 0) { idx.Add(new int[Chunk]); val.Add(new T[Chunk]); }
        idx[^1][c] = i;
        val[^1][c] = old;
        Count++;
    }

    /// whether index i was changed in this step (only while the step is running, see Seal)
    public bool Contains(int i) => seenBits != null ? (seenBits[i >> 6] & (1UL << (i & 63))) != 0 : seenSet != null && seenSet.Contains(i);

    /// step finished: release the lookup and the unused rest of the last block (no Add afterwards)
    public void Seal()
    {
        if (sealedLog) return;
        sealedLog = true;
        seenSet = null;
        seenBits = null;
        int c = Count % Chunk;
        if (c > 0)
        {
            var li = idx[^1]; Array.Resize(ref li, c); idx[^1] = li;
            var lv = val[^1]; Array.Resize(ref lv, c); val[^1] = lv;
        }
    }

    public IEnumerator<(int i, T v)> GetEnumerator()
    {
        for (int b = 0; b < idx.Count; b++)
        {
            int n = b == idx.Count - 1 ? Count - b * Chunk : Chunk;
            var bi = idx[b]; var bv = val[b];
            for (int k = 0; k < n; k++) yield return (bi[k], bv[k]);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
