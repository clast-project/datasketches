// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers;

namespace Clast.Sketches.Theta;

/// <summary>
/// Sorts a sketch's retained hashes into ascending order.
/// </summary>
/// <remarks>
/// <para>
/// A comparison sort is the wrong tool here. The retained hashes are, by
/// construction, uniformly distributed over <c>[0, theta)</c> — that is exactly
/// what the sketch guarantees — and the bound is known. That makes a
/// distribution sort applicable: scatter each hash into a bucket chosen by its
/// position within the range, then tidy each bucket. With one bucket per
/// element the buckets hold about one item each, so the tidying is nearly free.
/// </para>
/// <para>
/// The win is not fewer instructions but fewer unpredictable branches. A
/// comparison sort mispredicts on roughly half its comparisons, which costs far
/// more than the compare itself; the scatter below has no data-dependent
/// branches at all.
/// </para>
/// </remarks>
internal static class ThetaSort
{
    /// <summary>
    /// Below this, the bucket machinery costs more than it saves and the
    /// framework sort is simply better.
    /// </summary>
    private const int DistributionSortThreshold = 256;

    /// <summary>
    /// Above this many items in one bucket, stop insertion-sorting it. Uniform
    /// input never gets here; this only bounds the damage if the assumption is
    /// ever wrong.
    /// </summary>
    private const int MaxInsertionSortRun = 32;

    /// <summary>
    /// Sorts <paramref name="values"/> ascending, given that every entry is a
    /// non-negative hash below <paramref name="thetaLong"/>.
    /// </summary>
    public static void Sort(long[] values, int count, long thetaLong)
    {
        // Set operations walk an ordered sketch and keep a subset in place, so
        // their results arrive sorted already. Detecting that is one predictable
        // pass, against a distribution sort that would otherwise do its full
        // scatter for nothing.
        if (IsSorted(values, count))
        {
            return;
        }

        if (count < DistributionSortThreshold || thetaLong <= 0)
        {
            Array.Sort(values, 0, count);
            return;
        }

        int bucketCount = count;
        // Maps a hash to its fractional position in [0, theta), scaled to the
        // bucket count. Converting to double loses bits below the 53rd, but
        // rounding, multiplication by a positive scale and truncation are each
        // non-decreasing — so the bucket index is monotonic in the hash, which is
        // the only property the sort depends on.
        double scale = bucketCount / (double)thetaLong;

        int[] offsets = ArrayPool<int>.Shared.Rent(bucketCount + 1);
        long[] scratch = ArrayPool<long>.Shared.Rent(count);
        try
        {
            Array.Clear(offsets, 0, bucketCount + 1);

            for (int i = 0; i < count; i++)
            {
                offsets[BucketOf(values[i], scale, bucketCount)]++;
            }

            // Turn the counts into starting offsets.
            int running = 0;
            for (int b = 0; b < bucketCount; b++)
            {
                int inBucket = offsets[b];
                offsets[b] = running;
                running += inBucket;
            }

            for (int i = 0; i < count; i++)
            {
                long value = values[i];
                scratch[offsets[BucketOf(value, scale, bucketCount)]++] = value;
            }

            // Each offsets[b] now points one past its bucket, so consecutive
            // entries delimit the buckets.
            Array.Copy(scratch, values, count);

            int start = 0;
            for (int b = 0; b < bucketCount; b++)
            {
                int end = offsets[b];
                int length = end - start;
                if (length > 1)
                {
                    if (length <= MaxInsertionSortRun)
                    {
                        InsertionSort(values, start, end);
                    }
                    else
                    {
                        Array.Sort(values, start, length);
                    }
                }
                start = end;
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(offsets);
            ArrayPool<long>.Shared.Return(scratch);
        }
    }

    /// <summary>
    /// True if the values already ascend. Bails at the first descent, so on
    /// unsorted input this costs a couple of comparisons rather than a pass.
    /// </summary>
    private static bool IsSorted(long[] values, int count)
    {
        for (int i = 1; i < count; i++)
        {
            if (values[i] < values[i - 1])
            {
                return false;
            }
        }
        return true;
    }

    private static int BucketOf(long value, double scale, int bucketCount)
    {
        int bucket = (int)(value * scale);
        // A hash equal to theta cannot occur, but rounding in the conversion
        // above could still land exactly on the end.
        return bucket >= bucketCount ? bucketCount - 1 : bucket;
    }

    private static void InsertionSort(long[] values, int start, int end)
    {
        for (int i = start + 1; i < end; i++)
        {
            long value = values[i];
            int j = i - 1;
            while (j >= start && values[j] > value)
            {
                values[j + 1] = values[j];
                j--;
            }
            values[j + 1] = value;
        }
    }
}
