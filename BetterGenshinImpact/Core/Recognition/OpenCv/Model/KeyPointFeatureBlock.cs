using System;
using OpenCvSharp;
using System.Collections.Generic;

namespace BetterGenshinImpact.Core.Recognition.OpenCv.Model;

public class KeyPointFeatureBlock : IDisposable
{
    public List<KeyPoint> KeyPointList { get; set; } = [];

    private KeyPoint[]? keyPointArray;

    public KeyPoint[] KeyPointArray
    {
        get
        {
            keyPointArray ??= [.. KeyPointList];
            return keyPointArray;
        }
    }

    public List<int> KeyPointIndexList { get; set; } = [];

    public Mat? Descriptor;

    public int MergedCenterCellCol = -1;
    public int MergedCenterCellRow = -1;

    public void Dispose()
    {
        Descriptor?.Dispose();
        Descriptor = null;
    }
}
