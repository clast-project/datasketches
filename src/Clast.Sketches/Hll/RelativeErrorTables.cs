// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.
//
// Tables transcribed mechanically from RelativeErrorTables in apache/datasketches-java. See NOTICE.

namespace Clast.Sketches.Hll;

/// <summary>
/// Measured relative error for small sketches, where the asymptotic formula
/// is not yet accurate.
/// </summary>
/// <remarks>
/// Only consulted for lgK of 12 or below; larger sketches use the closed-form
/// RSE. Indexed as <c>3 * (lgK - 4) + (numStdDev - 1)</c> for lgK 4 through 12.
/// Bounds are asymmetric, so upper and lower are tabulated separately, as are
/// the HIP and non-HIP estimators.
/// </remarks>
internal static class RelativeErrorTables
{
    /// <summary>Relative error for the given estimator, direction and confidence.</summary>
    public static double GetRelErr(bool upperBound, bool outOfOrder, int lgK, int numStdDev)
    {
        int index = ((lgK - 4) * 3) + (numStdDev - 1);
        return (outOfOrder, upperBound) switch
        {
            (false, false) => HipLowerBound[index],
            (false, true) => HipUpperBound[index],
            (true, false) => NonHipLowerBound[index],
            _ => NonHipUpperBound[index],
        };
    }

    private static readonly double[] HipLowerBound =
    [
        0.207316195, 0.502865572, 0.882303765,
        0.146981579, 0.335426881, 0.557052,
        0.104026721, 0.227683872, 0.365888317,
        0.073614601, 0.156781585, 0.245740374,
        0.05205248, 0.108783763, 0.168030442,
        0.036770852, 0.075727545, 0.11593785,
        0.025990219, 0.053145536, 0.080772263,
        0.018373987, 0.037266176, 0.056271814,
        0.012936253, 0.02613829, 0.039387631,
    ];

    private static readonly double[] HipUpperBound =
    [
        -0.207805347, -0.355574279, -0.475535095,
        -0.146988328, -0.262390832, -0.360864026,
        -0.103877775, -0.191503663, -0.269311582,
        -0.073452978, -0.138513438, -0.198487447,
        -0.051982806, -0.099703123, -0.144128618,
        -0.036768609, -0.07138158, -0.104430324,
        -0.025991325, -0.050854296, -0.0748143,
        -0.01834533, -0.036121138, -0.05327616,
        -0.012920332, -0.025572893, -0.037896952,
    ];

    private static readonly double[] NonHipLowerBound =
    [
        0.254409839, 0.682266712, 1.304022158,
        0.181817353, 0.443389054, 0.778776219,
        0.129432281, 0.295782195, 0.49252279,
        0.091640655, 0.201175925, 0.323664385,
        0.064858051, 0.138523393, 0.218805328,
        0.045851855, 0.095925072, 0.148635751,
        0.032454144, 0.067009668, 0.102660669,
        0.022921382, 0.046868565, 0.071307398,
        0.016155679, 0.032825719, 0.049677541,
    ];

    private static readonly double[] NonHipUpperBound =
    [
        -0.256980172, -0.411905944, -0.52651057,
        -0.182332109, -0.310275547, -0.412660505,
        -0.129314228, -0.230142294, -0.315636197,
        -0.091584836, -0.16834013, -0.236346847,
        -0.06487411, -0.122045231, -0.174112107,
        -0.04591465, -0.08784505, -0.126917615,
        -0.032433119, -0.062897613, -0.091862929,
        -0.022960633, -0.044875401, -0.065736049,
        -0.016186662, -0.031827816, -0.046973459,
    ];
}
