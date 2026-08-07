// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers.Binary;

namespace Clast.Sketches.Quantiles;

/// <summary>
/// The KLL algorithm, independent of element type.
/// </summary>
/// <remarks>
/// <para>
/// The sketch is one flat items array partitioned into levels by an index
/// array: level <c>i</c> occupies <c>levels[i] .. levels[i+1] - 1</c>. Every
/// level is kept sorted except level 0, and the array is packed at the top —
/// free space is at the bottom, below <c>levels[0]</c>, and level 0 fills
/// downward into it.
/// </para>
/// <para>
/// The invariants the rest of the code relies on:
/// </para>
/// <list type="number">
/// <item><description>After any compaction, update, or merge, every level except level 0 is sorted.</description></item>
/// <item><description>After a compaction there is room for at least one more item in level 0.</description></item>
/// <item><description>There are no gaps except at the bottom, so <c>levels[0] == 0</c> means completely full.</description></item>
/// <item><description>The sum of the weights of all retained items equals <c>n</c>.</description></item>
/// <item><description><c>items.Length == levels[numLevels]</c>.</description></item>
/// </list>
/// <para>
/// Compaction discards half the items of a level by taking every other one,
/// starting from a randomly chosen offset. That randomness is what makes the
/// estimate unbiased — and it means two sketches fed identical streams do not
/// generally serialize to identical bytes, unlike Theta or HLL.
/// </para>
/// </remarks>
/// <typeparam name="T">The element type.</typeparam>
/// <typeparam name="TOps">The element operations; see <see cref="IQuantileItemOps{T}"/>.</typeparam>
internal sealed class KllSketchCore<T, TOps>
    where TOps : struct, IQuantileItemOps<T>
{
    private readonly int _k;
    private readonly int _m;
    private readonly Random _random;

    private long _n;
    private int _minK;
    private bool _levelZeroSorted;
    private int[] _levels;
    private T[] _items;
    private T _minItem;
    private T _maxItem;

    /// <summary>Cached sorted view, invalidated by every mutation.</summary>
    private KllSortedView<T, TOps>? _sortedView;

    public KllSketchCore(int k, int m, Random? random = null)
    {
        KllLevels.CheckM(m);
        KllLevels.CheckK(k, m);
        _k = k;
        _m = m;
        _random = random ?? SharedRandom.Instance;
        _levels = [k, k];
        _items = new T[k];
        _n = 0;
        _minK = k;
        _levelZeroSorted = false;
        _minItem = default!;
        _maxItem = default!;
    }

    public int K => _k;

    public int M => _m;

    public long N => _n;

    public int MinK => _minK;

    public bool IsEmpty => _n == 0;

    public bool IsEstimationMode => NumLevels > 1;

    public int NumLevels => _levels.Length - 1;

    public int NumRetained => _levels[NumLevels] - _levels[0];

    public bool IsLevelZeroSorted => _levelZeroSorted;

    public T MinItem => _minItem;

    public T MaxItem => _maxItem;

    /// <summary>Presents one item to the sketch.</summary>
    public void Update(T item)
    {
        UpdateMinMax(item);
        int freeSpace = _levels[0];
        if (freeSpace == 0)
        {
            CompressWhileUpdating();
            freeSpace = _levels[0];
        }
        _n++;
        _levelZeroSorted = false;
        int nextPos = freeSpace - 1;
        _levels[0] = nextPos;
        _items[nextPos] = item;
        _sortedView = null;
    }

    private void UpdateMinMax(T item)
    {
        TOps ops = default;
        if (_n == 0)
        {
            _minItem = item;
            _maxItem = item;
        }
        else
        {
            if (ops.LessThan(item, _minItem)) { _minItem = item; }
            if (ops.LessThan(_maxItem, item)) { _maxItem = item; }
        }
    }

    /// <summary>
    /// Grows the sketch by one level at the top, shifting everything up so the
    /// new free space lands at the bottom where level 0 can use it.
    /// </summary>
    /// <remarks>
    /// Only valid when the sketch is completely full — <c>levels[0] == 0</c> —
    /// which is part of the growth scheme, not an incidental condition.
    /// </remarks>
    private void AddEmptyTopLevelToCompletelyFullSketch()
    {
        int[] curLevels = _levels;
        int curNumLevels = NumLevels;
        int curTotalCapacity = curLevels[curNumLevels];

        int deltaCapacity = KllLevels.LevelCapacity(_k, curNumLevels + 1, 0, _m);
        int newTotalCapacity = curTotalCapacity + deltaCapacity;

        // On the heap the levels array is always exactly numLevels + 1 long, so
        // it always needs to grow here.
        int[] newLevels = new int[curNumLevels + 2];
        Array.Copy(curLevels, newLevels, curLevels.Length);
        int newNumLevels = curNumLevels + 1;

        // Shift every existing boundary up by the new bottom-level capacity,
        // then place the new top boundary.
        for (int level = 0; level <= newNumLevels - 1; level++)
        {
            newLevels[level] += deltaCapacity;
        }
        newLevels[newNumLevels] = newTotalCapacity;

        T[] newItems = new T[newTotalCapacity];
        Array.Copy(_items, 0, newItems, deltaCapacity, curTotalCapacity);

        _levels = newLevels;
        _items = newItems;
    }

    /// <summary>
    /// Halves the lowest over-capacity level, promoting the survivors into the
    /// level above.
    /// </summary>
    /// <remarks>
    /// Valid only in the special case of exactly reaching capacity while
    /// updating; merging goes through <see cref="GeneralCompress"/> instead.
    /// </remarks>
    private void CompressWhileUpdating()
    {
        TOps ops = default;
        int level = KllLevels.FindLevelToCompact(_k, _m, NumLevels, _levels);
        if (level == NumLevels - 1)
        {
            // Compacting the top level needs somewhere to promote into. This
            // grows the items array and shifts the level boundaries, so the
            // levels array must be re-read afterwards.
            AddEmptyTopLevelToCompletelyFullSketch();
        }

        int[] levels = _levels;
        T[] items = _items;
        int rawBeg = levels[level];
        int rawEnd = levels[level + 1];
        // Safe because a new top level was just added if one was needed.
        int popAbove = levels[level + 2] - rawEnd;
        int rawPop = rawEnd - rawBeg;
        bool oddPop = (rawPop & 1) == 1;
        int adjBeg = oddPop ? rawBeg + 1 : rawBeg;
        int adjPop = oddPop ? rawPop - 1 : rawPop;
        int halfAdjPop = adjPop / 2;

        if (level == 0)
        {
            // Level zero is the only unsorted one, and compaction needs order.
            ops.Sort(items, adjBeg, adjPop);
        }

        if (popAbove == 0)
        {
            RandomlyHalveUp(items, adjBeg, adjPop);
        }
        else
        {
            RandomlyHalveDown(items, adjBeg, adjPop);
            MergeSorted(items, adjBeg, halfAdjPop, items, rawEnd, popAbove, items, adjBeg + halfAdjPop);
        }

        levels[level + 1] -= halfAdjPop;

        if (oddPop)
        {
            levels[level] = levels[level + 1] - 1;  // the level keeps its one leftover item
            items[levels[level]] = items[rawBeg];
        }
        else
        {
            levels[level] = levels[level + 1];      // the level is now empty
        }

        // Shift the levels below up into the space just freed, so it ends up at
        // the bottom where level zero can fill into it.
        if (level > 0)
        {
            int amount = rawBeg - levels[0];
            Array.Copy(items, levels[0], items, levels[0] + halfAdjPop, amount);
        }
        for (int lvl = 0; lvl < level; lvl++)
        {
            levels[lvl] += halfAdjPop;
        }
    }

    /// <summary>Keeps every other item from the low end, at a random parity.</summary>
    private void RandomlyHalveDown(T[] buf, int start, int length)
    {
        int halfLength = length / 2;
        int offset = _random.Next(2);
        int j = start + offset;
        for (int i = start; i < start + halfLength; i++)
        {
            buf[i] = buf[j];
            j += 2;
        }
    }

    /// <summary>Keeps every other item from the high end, at a random parity.</summary>
    private void RandomlyHalveUp(T[] buf, int start, int length)
    {
        int halfLength = length / 2;
        int offset = _random.Next(2);
        int j = start + length - 1 - offset;
        for (int i = start + length - 1; i >= start + halfLength; i--)
        {
            buf[i] = buf[j];
            j -= 2;
        }
    }

    /// <summary>Merges two sorted runs into <paramref name="bufC"/>; only C is written.</summary>
    private static void MergeSorted(
        T[] bufA, int startA, int lenA,
        T[] bufB, int startB, int lenB,
        T[] bufC, int startC)
    {
        TOps ops = default;
        int limA = startA + lenA;
        int limB = startB + lenB;
        int limC = startC + lenA + lenB;

        int a = startA;
        int b = startB;

        for (int c = startC; c < limC; c++)
        {
            if (a == limA)
            {
                bufC[c] = bufB[b];
                b++;
            }
            else if (b == limB || ops.LessThan(bufA[a], bufB[b]))
            {
                bufC[c] = bufA[a];
                a++;
            }
            else
            {
                bufC[c] = bufB[b];
                b++;
            }
        }
    }

    /// <summary>Merges another sketch of the same element type into this one.</summary>
    public void Merge(KllSketchCore<T, TOps> other)
    {
        TOps ops = default;
        if (other.IsEmpty) { return; }

        // Capture the mutable state that the level-zero updates below will change.
        bool myEmpty = IsEmpty;
        T myMin = _minItem;
        T myMax = _maxItem;
        int myMinK = _minK;
        long finalN = checked(_n + other._n);

        int otherNumLevels = other.NumLevels;
        int[] otherLevels = other._levels;
        T[] otherItems = other._items;

        // Level zero of the other sketch has weight 1, so those items can simply
        // be presented as ordinary updates.
        for (int i = otherLevels[0]; i < otherLevels[1]; i++)
        {
            Update(otherItems[i]);
        }

        int myCurNumLevels = NumLevels;
        int[] myCurLevels = _levels;
        T[] myCurItems = _items;

        int myNewNumLevels = myCurNumLevels;
        int[] myNewLevels = myCurLevels;
        T[] myNewItems = myCurItems;

        if (otherNumLevels > 1)
        {
            int tmpSpaceNeeded = NumRetained
                + KllLevels.NumRetainedAboveLevelZero(otherNumLevels, otherLevels);
            T[] workbuf = new T[tmpSpaceNeeded];

            int provisionalNumLevels = Math.Max(myCurNumLevels, otherNumLevels);
            int ub = Math.Max(KllLevels.UbOnNumLevels(finalN), provisionalNumLevels);
            int[] worklevels = new int[ub + 2];   // ub + 1 is not enough
            int[] outlevels = new int[ub + 2];

            PopulateWorkArrays(
                workbuf, worklevels, provisionalNumLevels,
                myCurNumLevels, myCurLevels, myCurItems,
                otherNumLevels, otherLevels, otherItems);

            // workbuf is deliberately both input and output.
            (int numLevels, int targetItemCount, int curItemCount) = GeneralCompress(
                _k, _m, provisionalNumLevels, workbuf, worklevels, workbuf, outlevels, _levelZeroSorted);

            myNewNumLevels = numLevels;
            myNewItems = targetItemCount == myCurItems.Length ? myCurItems : new T[targetItemCount];

            int freeSpaceAtBottom = targetItemCount - curItemCount;
            Array.Copy(workbuf, outlevels[0], myNewItems, freeSpaceAtBottom, curItemCount);
            int theShift = freeSpaceAtBottom - outlevels[0];

            int finalLevelsArrLen = myCurLevels.Length < myNewNumLevels + 1
                ? myNewNumLevels + 1
                : myCurLevels.Length;
            myNewLevels = new int[finalLevelsArrLen];
            for (int lvl = 0; lvl < myNewNumLevels + 1; lvl++)
            {
                myNewLevels[lvl] = outlevels[lvl] + theShift;
            }
        }

        _n = finalN;
        if (other.IsEstimationMode)
        {
            // Only an estimating source can degrade our accuracy; an exact one
            // brings over its items verbatim.
            _minK = Math.Min(myMinK, other._minK);
        }

        _levels = myNewLevels;
        _items = myNewItems;

        if (myEmpty)
        {
            _minItem = other._minItem;
            _maxItem = other._maxItem;
        }
        else
        {
            _minItem = ops.LessThan(other._minItem, myMin) ? other._minItem : myMin;
            _maxItem = ops.LessThan(myMax, other._maxItem) ? other._maxItem : myMax;
        }

        _sortedView = null;
    }

    /// <summary>
    /// Interleaves the two sketches' levels into one work buffer, level by
    /// level. Level zero of the other sketch is already accounted for.
    /// </summary>
    private static void PopulateWorkArrays(
        T[] workBuf, int[] workLevels, int provisionalNumLevels,
        int myCurNumLevels, int[] myCurLevels, T[] myCurItems,
        int otherNumLevels, int[] otherLevels, T[] otherItems)
    {
        workLevels[0] = 0;

        int selfPopZero = KllLevels.CurrentLevelSize(0, myCurNumLevels, myCurLevels);
        Array.Copy(myCurItems, myCurLevels[0], workBuf, workLevels[0], selfPopZero);
        workLevels[1] = workLevels[0] + selfPopZero;

        for (int lvl = 1; lvl < provisionalNumLevels; lvl++)
        {
            int selfPop = KllLevels.CurrentLevelSize(lvl, myCurNumLevels, myCurLevels);
            int otherPop = KllLevels.CurrentLevelSize(lvl, otherNumLevels, otherLevels);
            workLevels[lvl + 1] = workLevels[lvl] + selfPop + otherPop;

            if (selfPop > 0 && otherPop == 0)
            {
                Array.Copy(myCurItems, myCurLevels[lvl], workBuf, workLevels[lvl], selfPop);
            }
            else if (selfPop == 0 && otherPop > 0)
            {
                Array.Copy(otherItems, otherLevels[lvl], workBuf, workLevels[lvl], otherPop);
            }
            else if (selfPop > 0 && otherPop > 0)
            {
                MergeSorted(
                    myCurItems, myCurLevels[lvl], selfPop,
                    otherItems, otherLevels[lvl], otherPop,
                    workBuf, workLevels[lvl]);
            }
        }
    }

    /// <summary>
    /// The general compaction used when merging: walks up the levels, copying
    /// those that fit and halving those that do not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For each level: if it does not need compacting, copy it over. Otherwise
    /// copy zero or one item over, then halve up if the level above is empty,
    /// or halve down and merge up if it is not. Either way, adjust the
    /// boundaries of the level above.
    /// </para>
    /// <para>
    /// This trashes <paramref name="inBuf"/> and <paramref name="inLevels"/>,
    /// and is safe when the input and output buffers are the same array. Every
    /// level except level zero must be sorted on entry and will be on exit.
    /// </para>
    /// </remarks>
    /// <returns>The new level count, the target item capacity, and the current item count.</returns>
    private (int NumLevels, int TargetItemCount, int CurrentItemCount) GeneralCompress(
        int k, int m, int numLevelsIn,
        T[] inBuf, int[] inLevels,
        T[] outBuf, int[] outLevels,
        bool isLevelZeroSorted)
    {
        TOps ops = default;
        int numLevels = numLevelsIn;
        int currentItemCount = inLevels[numLevels] - inLevels[0];   // shrinks with each compaction
        int targetItemCount = KllLevels.ComputeTotalItemCapacity(k, m, numLevels); // grows if we add levels
        bool doneYet = false;
        outLevels[0] = 0;
        int curLevel = -1;

        while (!doneYet)
        {
            curLevel++;

            // At the top level, fabricate an empty level above for convenience;
            // numLevels is not incremented until we actually compact into it.
            if (curLevel == numLevels - 1)
            {
                inLevels[curLevel + 2] = inLevels[curLevel + 1];
            }

            int rawBeg = inLevels[curLevel];
            int rawLim = inLevels[curLevel + 1];
            int rawPop = rawLim - rawBeg;

            if (currentItemCount < targetItemCount
                || rawPop < KllLevels.LevelCapacity(k, numLevels, curLevel, m))
            {
                // Copy the level over as is. Because inBuf and outBuf may be the
                // same array, this must never move data upwards.
                Array.Copy(inBuf, rawBeg, outBuf, outLevels[curLevel], rawPop);
                outLevels[curLevel + 1] = outLevels[curLevel] + rawPop;
            }
            else
            {
                // Both the sketch and this level are too full, so compact it.
                // This may add a level, which changes the sketch's capacity.
                int popAbove = inLevels[curLevel + 2] - rawLim;
                bool oddPop = (rawPop & 1) == 1;
                int adjBeg = oddPop ? rawBeg + 1 : rawBeg;
                int adjPop = oddPop ? rawPop - 1 : rawPop;
                int halfAdjPop = adjPop / 2;

                if (oddPop)
                {
                    outBuf[outLevels[curLevel]] = inBuf[rawBeg];
                    outLevels[curLevel + 1] = outLevels[curLevel] + 1;
                }
                else
                {
                    outLevels[curLevel + 1] = outLevels[curLevel];
                }

                if (curLevel == 0 && !isLevelZeroSorted)
                {
                    ops.Sort(inBuf, adjBeg, adjPop);
                }

                if (popAbove == 0)
                {
                    RandomlyHalveUp(inBuf, adjBeg, adjPop);
                }
                else
                {
                    RandomlyHalveDown(inBuf, adjBeg, adjPop);
                    MergeSorted(inBuf, adjBeg, halfAdjPop, inBuf, rawLim, popAbove, inBuf, adjBeg + halfAdjPop);
                }

                currentItemCount -= halfAdjPop;
                inLevels[curLevel + 1] -= halfAdjPop;

                // Compacting the old top level creates a new one, and with it
                // the capacity of a new bottom level.
                if (curLevel == numLevels - 1)
                {
                    numLevels++;
                    targetItemCount += KllLevels.LevelCapacity(k, numLevels, 0, m);
                }
            }

            if (curLevel == numLevels - 1) { doneYet = true; }
        }

        return (numLevels, targetItemCount, currentItemCount);
    }

    /// <summary>
    /// The sorted view: retained items in order, with cumulative weights.
    /// Cached, since every rank and quantile query needs it and nothing but a
    /// mutation invalidates it.
    /// </summary>
    public KllSortedView<T, TOps> GetSortedView()
    {
        if (IsEmpty) { throw new InvalidOperationException("The sketch is empty."); }
        return _sortedView ??= BuildSortedView();
    }

    private KllSortedView<T, TOps> BuildSortedView()
    {
        TOps ops = default;
        if (!_levelZeroSorted)
        {
            ops.Sort(_items, _levels[0], _levels[1] - _levels[0]);
            _levelZeroSorted = true;
        }

        int numQuantiles = NumRetained;
        T[] quantiles = new T[numQuantiles];
        long[] cumWeights = new long[numQuantiles];

        // Copy the retained items out, recording each level's weight alongside,
        // then sort the whole thing carrying the weights along.
        int numLevels = NumLevels;
        int[] myLevels = new int[numLevels + 1];
        int offset = _levels[0];
        Array.Copy(_items, offset, quantiles, 0, numQuantiles);

        int dstLevel = 0;
        long weight = 1;
        for (int srcLevel = 0; srcLevel < numLevels; srcLevel++)
        {
            int fromIndex = _levels[srcLevel] - offset;
            int toIndex = _levels[srcLevel + 1] - offset;
            if (fromIndex < toIndex)
            {
                for (int i = fromIndex; i < toIndex; i++) { cumWeights[i] = weight; }
                myLevels[dstLevel] = fromIndex;
                myLevels[dstLevel + 1] = toIndex;
                dstLevel++;
            }
            weight *= 2;
        }

        BlockyTandemMergeSort(quantiles, cumWeights, myLevels, dstLevel);

        long subtotal = 0;
        for (int i = 0; i < cumWeights.Length; i++)
        {
            subtotal += cumWeights[i];
            cumWeights[i] = subtotal;
        }

        return new KllSortedView<T, TOps>(quantiles, cumWeights, _n, _minItem, _maxItem);
    }

    /// <summary>
    /// Sorts the retained items while carrying their weights along.
    /// </summary>
    /// <remarks>
    /// Each level is already sorted, so this is a merge of sorted runs rather
    /// than a general sort — the "blocky" part. The recursion alternates source
    /// and destination arrays so no copy is needed between passes.
    /// </remarks>
    private static void BlockyTandemMergeSort(T[] quantiles, long[] weights, int[] levels, int numLevels)
    {
        if (numLevels == 1) { return; }

        T[] quantilesTmp = (T[])quantiles.Clone();
        long[] weightsTmp = new long[quantiles.Length];
        Array.Copy(weights, weightsTmp, quantiles.Length);

        BlockyTandemMergeSortRecursion(quantilesTmp, weightsTmp, quantiles, weights, levels, 0, numLevels);
    }

    private static void BlockyTandemMergeSortRecursion(
        T[] quantilesSrc, long[] weightsSrc,
        T[] quantilesDst, long[] weightsDst,
        int[] levels, int startingLevel, int numLevels)
    {
        if (numLevels == 1) { return; }
        int numLevels1 = numLevels / 2;
        int numLevels2 = numLevels - numLevels1;
        int startingLevel1 = startingLevel;
        int startingLevel2 = startingLevel + numLevels1;

        // Swap the roles of source and destination on the way down.
        BlockyTandemMergeSortRecursion(
            quantilesDst, weightsDst, quantilesSrc, weightsSrc, levels, startingLevel1, numLevels1);
        BlockyTandemMergeSortRecursion(
            quantilesDst, weightsDst, quantilesSrc, weightsSrc, levels, startingLevel2, numLevels2);
        TandemMerge(
            quantilesSrc, weightsSrc, quantilesDst, weightsDst, levels,
            startingLevel1, numLevels1, startingLevel2, numLevels2);
    }

    private static void TandemMerge(
        T[] quantilesSrc, long[] weightsSrc,
        T[] quantilesDst, long[] weightsDst,
        int[] levelStarts,
        int startingLevel1, int numLevels1,
        int startingLevel2, int numLevels2)
    {
        TOps ops = default;
        int fromIndex1 = levelStarts[startingLevel1];
        int toIndex1 = levelStarts[startingLevel1 + numLevels1];
        int fromIndex2 = levelStarts[startingLevel2];
        int toIndex2 = levelStarts[startingLevel2 + numLevels2];
        int iSrc1 = fromIndex1;
        int iSrc2 = fromIndex2;
        int iDst = fromIndex1;

        while (iSrc1 < toIndex1 && iSrc2 < toIndex2)
        {
            if (ops.LessThan(quantilesSrc[iSrc1], quantilesSrc[iSrc2]))
            {
                quantilesDst[iDst] = quantilesSrc[iSrc1];
                weightsDst[iDst] = weightsSrc[iSrc1];
                iSrc1++;
            }
            else
            {
                quantilesDst[iDst] = quantilesSrc[iSrc2];
                weightsDst[iDst] = weightsSrc[iSrc2];
                iSrc2++;
            }
            iDst++;
        }

        if (iSrc1 < toIndex1)
        {
            Array.Copy(quantilesSrc, iSrc1, quantilesDst, iDst, toIndex1 - iSrc1);
            Array.Copy(weightsSrc, iSrc1, weightsDst, iDst, toIndex1 - iSrc1);
        }
        else if (iSrc2 < toIndex2)
        {
            Array.Copy(quantilesSrc, iSrc2, quantilesDst, iDst, toIndex2 - iSrc2);
            Array.Copy(weightsSrc, iSrc2, weightsDst, iDst, toIndex2 - iSrc2);
        }
    }

    // ---- Serialization ----

    /// <summary>The structure <see cref="ToByteArray"/> will write.</summary>
    private KllStructure TargetStructure =>
        _n == 0 ? KllStructure.CompactEmpty
        : _n == 1 ? KllStructure.CompactSingle
        : KllStructure.CompactFull;

    /// <summary>Bytes <see cref="ToByteArray"/> will produce.</summary>
    public int SerializedSizeBytes
    {
        get
        {
            TOps ops = default;
            return TargetStructure switch
            {
                KllStructure.CompactEmpty => KllPreamble.DataStartSingleItem,
                KllStructure.CompactSingle => KllPreamble.DataStartSingleItem + ops.SizeBytes,
                _ => KllPreamble.DataStart
                     + (NumLevels * sizeof(int))
                     + (2 * ops.SizeBytes)
                     + (NumRetained * ops.SizeBytes),
            };
        }
    }

    /// <summary>Serializes to the compact form the reference library reads.</summary>
    public byte[] ToByteArray()
    {
        TOps ops = default;
        KllStructure structure = TargetStructure;
        byte[] bytes = new byte[SerializedSizeBytes];
        Span<byte> image = bytes;

        image[KllPreamble.PreambleIntsByte] = KllPreamble.PreambleIntsFor(structure);
        image[KllPreamble.SerVerByte] = KllPreamble.SerVerFor(structure);
        image[KllPreamble.FamilyByte] = (byte)SketchFamily.Kll;
        image[KllPreamble.FlagsByte] = (byte)(
            (IsEmpty ? KllPreamble.EmptyFlagMask : 0)
            | (_levelZeroSorted ? KllPreamble.LevelZeroSortedFlagMask : 0)
            | (_n == 1 ? KllPreamble.SingleItemFlagMask : 0));
        BinaryPrimitives.WriteUInt16LittleEndian(image.Slice(KllPreamble.KShort), (ushort)_k);
        image[KllPreamble.MByte] = (byte)_m;
        // Byte 7 is reserved.

        if (structure == KllStructure.CompactEmpty) { return bytes; }

        if (structure == KllStructure.CompactSingle)
        {
            ops.Write(image.Slice(KllPreamble.DataStartSingleItem), _items[_levels[0]]);
            return bytes;
        }

        BinaryPrimitives.WriteInt64LittleEndian(image.Slice(KllPreamble.NLong), _n);
        BinaryPrimitives.WriteUInt16LittleEndian(image.Slice(KllPreamble.MinKShort), (ushort)_minK);
        image[KllPreamble.NumLevelsByte] = (byte)NumLevels;
        // Byte 19 is reserved.

        int offset = KllPreamble.DataStart;

        // The compact form drops the last levels entry: it is always the total
        // capacity, which the reader recomputes from k, m and numLevels.
        for (int i = 0; i < NumLevels; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(image.Slice(offset), _levels[i]);
            offset += sizeof(int);
        }

        ops.Write(image.Slice(offset), _minItem);
        offset += ops.SizeBytes;
        ops.Write(image.Slice(offset), _maxItem);
        offset += ops.SizeBytes;

        int retained = NumRetained;
        int start = _levels[0];
        for (int i = 0; i < retained; i++)
        {
            ops.Write(image.Slice(offset), _items[start + i]);
            offset += ops.SizeBytes;
        }

        return bytes;
    }

    /// <summary>Reconstructs a sketch from any of the four serialized structures.</summary>
    public static KllSketchCore<T, TOps> Deserialize(ReadOnlySpan<byte> image, Random? random = null)
    {
        TOps ops = default;
        if (image.Length < KllPreamble.DataStartSingleItem)
        {
            throw new ArgumentException(
                $"A KLL image must be at least {KllPreamble.DataStartSingleItem} bytes; got {image.Length}.",
                nameof(image));
        }

        int preInts = image[KllPreamble.PreambleIntsByte];
        int serVer = image[KllPreamble.SerVerByte];
        KllStructure structure = KllPreamble.StructureFrom(preInts, serVer);

        int familyId = image[KllPreamble.FamilyByte];
        if (familyId != (int)SketchFamily.Kll)
        {
            throw new ArgumentException(
                $"Not a KLL image: family ID is {familyId}, expected {(int)SketchFamily.Kll}.", nameof(image));
        }

        int flags = image[KllPreamble.FlagsByte];
        int k = BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(KllPreamble.KShort));
        int m = image[KllPreamble.MByte];
        KllLevels.CheckM(m);
        KllLevels.CheckK(k, m);

        bool emptyFlag = (flags & KllPreamble.EmptyFlagMask) != 0;
        var sketch = new KllSketchCore<T, TOps>(k, m, random ?? SharedRandom.Instance)
        {
            _levelZeroSorted = (flags & KllPreamble.LevelZeroSortedFlagMask) != 0,
        };

        switch (structure)
        {
            case KllStructure.CompactEmpty:
            {
                if (!emptyFlag)
                {
                    throw new ArgumentException("A compact empty KLL image must have the empty flag set.", nameof(image));
                }
                sketch._n = 0;
                sketch._minK = k;
                sketch._levels = [k, k];
                sketch._items = new T[k];
                break;
            }

            case KllStructure.CompactSingle:
            {
                if (emptyFlag)
                {
                    throw new ArgumentException("A single-item KLL image must not have the empty flag set.", nameof(image));
                }
                RequireLength(image, KllPreamble.DataStartSingleItem + ops.SizeBytes);
                sketch._n = 1;
                sketch._minK = k;
                sketch._levels = [k - 1, k];
                sketch._items = new T[k];
                T item = ops.Read(image.Slice(KllPreamble.DataStartSingleItem));
                sketch._items[k - 1] = item;
                sketch._minItem = item;
                sketch._maxItem = item;
                break;
            }

            default:
            {
                bool updatable = structure == KllStructure.Updatable;
                if (!updatable && emptyFlag)
                {
                    throw new ArgumentException("A compact full KLL image must not have the empty flag set.", nameof(image));
                }
                RequireLength(image, KllPreamble.DataStart);
                sketch._n = BinaryPrimitives.ReadInt64LittleEndian(image.Slice(KllPreamble.NLong));
                sketch._minK = BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(KllPreamble.MinKShort));
                int numLevels = image[KllPreamble.NumLevelsByte];
                if (numLevels < 1)
                {
                    throw new ArgumentException("A KLL image must declare at least one level.", nameof(image));
                }

                int levelsOnWire = updatable ? numLevels + 1 : numLevels;
                RequireLength(image, KllPreamble.DataStart + (levelsOnWire * sizeof(int)) + (2 * ops.SizeBytes));

                int[] levels = new int[numLevels + 1];
                int offset = KllPreamble.DataStart;
                for (int i = 0; i < levelsOnWire; i++)
                {
                    levels[i] = BinaryPrimitives.ReadInt32LittleEndian(image.Slice(offset));
                    offset += sizeof(int);
                }
                if (!updatable)
                {
                    // The compact form omits the top boundary; it is the total
                    // capacity implied by k, m and the level count.
                    levels[numLevels] = KllLevels.ComputeTotalItemCapacity(k, m, numLevels);
                }

                sketch._minItem = ops.Read(image.Slice(offset));
                offset += ops.SizeBytes;
                sketch._maxItem = ops.Read(image.Slice(offset));
                offset += ops.SizeBytes;

                int capacity = levels[numLevels];
                ValidateLevels(levels, numLevels, capacity);

                int freeSpace = levels[0];
                int numItems = updatable ? capacity : capacity - freeSpace;
                // In long arithmetic: an updatable image carries its capacity on
                // the wire, so a corrupt one could otherwise overflow this and
                // pass a length check it should fail.
                RequireLength(image, (long)offset + ((long)numItems * ops.SizeBytes));

                T[] items = new T[capacity];
                int dst = updatable ? 0 : freeSpace;
                for (int i = 0; i < numItems; i++)
                {
                    items[dst + i] = ops.Read(image.Slice(offset));
                    offset += ops.SizeBytes;
                }

                sketch._levels = levels;
                sketch._items = items;
                break;
            }
        }

        return sketch;
    }

    private static void RequireLength(ReadOnlySpan<byte> image, long required)
    {
        if (image.Length < required)
        {
            throw new ArgumentException(
                $"Truncated KLL image: need at least {required} bytes, got {image.Length}.", nameof(image));
        }
    }

    /// <summary>
    /// Rejects level boundaries that are not monotonic or run past the implied
    /// capacity, which would otherwise index out of the items array later.
    /// </summary>
    private static void ValidateLevels(int[] levels, int numLevels, int capacity)
    {
        if (levels[0] < 0 || capacity < 0)
        {
            throw new ArgumentException("A KLL image has a negative level boundary.");
        }
        for (int i = 0; i < numLevels; i++)
        {
            if (levels[i] > levels[i + 1])
            {
                throw new ArgumentException("A KLL image has non-monotonic level boundaries.");
            }
        }
        if (levels[numLevels] != capacity)
        {
            throw new ArgumentException("A KLL image's top level boundary disagrees with its capacity.");
        }
    }

    /// <summary>
    /// The shared default source of compaction randomness.
    /// </summary>
    /// <remarks>
    /// <see cref="Random"/> is not thread-safe and sketches are not either, but
    /// two sketches on two threads sharing this instance would be a data race
    /// the caller never asked for, so each thread gets its own.
    /// </remarks>
    private static class SharedRandom
    {
        [ThreadStatic]
        private static Random? _instance;

        public static Random Instance => _instance ??= new Random();
    }
}
