// Copyright (c) clast-project. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Clast.Sketches.Theta;

/// <summary>
/// Configures and creates <see cref="UpdateThetaSketch"/> instances.
/// </summary>
/// <remarks>
/// The defaults match the reference implementations exactly — 4096 nominal
/// entries, the QuickSelect family, seed 9001, resize factor X8, no sampling —
/// so <c>UpdateThetaSketch.Builder().Build()</c> produces a sketch that
/// serializes identically to <c>UpdateSketch.builder().build()</c> in
/// datasketches-java given the same inputs.
/// </remarks>
public sealed class UpdateThetaSketchBuilder
{
    private int _lgNominalEntries = ThetaLimits.LgCeilingPowerOf2(ThetaLimits.DefaultNominalEntries);
    private ulong _seed = ThetaSketch.DefaultUpdateSeed;
    private SketchFamily _family = SketchFamily.QuickSelect;
    private float _samplingProbability = 1.0f;
    private ResizeFactor _resizeFactor = ResizeFactor.X8;

    /// <summary>
    /// Sets the nominal entry count <c>k</c>, rounded up to a power of two.
    /// Larger values cost linearly more space and give error proportional to
    /// <c>1/sqrt(k)</c>. Must be between 16 and 67,108,864.
    /// </summary>
    public UpdateThetaSketchBuilder SetNominalEntries(int nominalEntries)
    {
        _lgNominalEntries = ThetaLimits.CheckNominalEntries(nominalEntries);
        return this;
    }

    /// <summary>Sets the nominal entry count as its base-2 logarithm. Must be between 4 and 26.</summary>
    public UpdateThetaSketchBuilder SetLgNominalEntries(int lgNominalEntries)
    {
        if (lgNominalEntries < ThetaLimits.MinLgNominalEntries
            || lgNominalEntries > ThetaLimits.MaxLgNominalEntries)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lgNominalEntries),
                lgNominalEntries,
                $"Log nominal entries must be between {ThetaLimits.MinLgNominalEntries} and {ThetaLimits.MaxLgNominalEntries}.");
        }
        _lgNominalEntries = lgNominalEntries;
        return this;
    }

    /// <summary>
    /// Sets the update seed. Change this only with a reason: sketches built with
    /// different seeds cannot be merged, and once sketches are stored the choice
    /// is permanent.
    /// </summary>
    public UpdateThetaSketchBuilder SetSeed(ulong seed)
    {
        _seed = seed;
        return this;
    }

    /// <summary>
    /// Selects the update algorithm: <see cref="SketchFamily.QuickSelect"/>
    /// (default) or <see cref="SketchFamily.Alpha"/>.
    /// </summary>
    /// <remarks>
    /// Alpha is more accurate standalone and is what Apache Iceberg Puffin
    /// specifies, but it requires at least 512 nominal entries and loses its
    /// advantage once the sketch goes through a union.
    /// </remarks>
    public UpdateThetaSketchBuilder SetFamily(SketchFamily family)
    {
        if (family is not (SketchFamily.QuickSelect or SketchFamily.Alpha))
        {
            throw new ArgumentOutOfRangeException(
                nameof(family), family, "An update sketch must be either QuickSelect or Alpha.");
        }
        _family = family;
        return this;
    }

    /// <summary>
    /// Sets the up-front sampling probability, in (0, 1]. Values below 1.0 make
    /// the sketch discard that fraction of input before it ever reaches the hash
    /// table, which trades accuracy for speed on very large streams.
    /// </summary>
    public UpdateThetaSketchBuilder SetSamplingProbability(float p)
    {
        if (!(p > 0.0f) || p > 1.0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(p), p, "Sampling probability must be greater than 0 and at most 1.");
        }
        _samplingProbability = p;
        return this;
    }

    /// <summary>Sets how aggressively the internal hash table grows toward full size.</summary>
    public UpdateThetaSketchBuilder SetResizeFactor(ResizeFactor resizeFactor)
    {
        _resizeFactor = resizeFactor;
        return this;
    }

    /// <summary>Creates the sketch.</summary>
    public UpdateThetaSketch Build() => _family switch
    {
        SketchFamily.Alpha =>
            new AlphaThetaSketch(_lgNominalEntries, _seed, _samplingProbability, _resizeFactor),
        _ =>
            new QuickSelectThetaSketch(_lgNominalEntries, _seed, _samplingProbability, _resizeFactor),
    };
}
