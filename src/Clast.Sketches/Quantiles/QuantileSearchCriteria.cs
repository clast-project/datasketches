// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Clast.Sketches.Quantiles;

/// <summary>
/// Whether a rank or quantile query includes the value at the boundary.
/// </summary>
/// <remarks>
/// <para>
/// The distinction only matters when a query lands exactly on a retained value,
/// but it decides which of two adjacent answers you get, so both engines and
/// test suites care. The definitions are the ones in the DataSketches
/// documentation, stated in terms of the natural rank <c>r</c> of a quantile
/// <c>q</c> — the number of stream values that compare at most (or strictly
/// less than) <c>q</c>.
/// </para>
/// <para>
/// <see cref="Inclusive"/> is the default here and in the reference library.
/// </para>
/// </remarks>
public enum QuantileSearchCriteria
{
    /// <summary>
    /// Ranks count values less than or equal to the query, and
    /// <c>GetQuantile(r)</c> returns the smallest value whose rank is at least
    /// <c>r</c>. This is the definition used by SQL <c>PERCENTILE_DISC</c>.
    /// </summary>
    Inclusive = 0,

    /// <summary>
    /// Ranks count values strictly less than the query, and
    /// <c>GetQuantile(r)</c> returns the smallest value whose rank is strictly
    /// greater than <c>r</c>.
    /// </summary>
    Exclusive = 1,
}
