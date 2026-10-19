using Microsoft.ML.OnnxRuntime;

namespace AnywhereOcr;

public sealed record OcrLine(string Text, double Score, int[] X, int[] Y);

/// <summary>
/// Detection -> SortQuadBoxes -> CropByPolys(quad) -> recognition, mirroring the
/// paddlex OCR pipeline with document preprocessing and textline orientation disabled.
/// </summary>
public sealed class OcrEngine : IDisposable
{
    public Detector Detector { get; }
    public Recognizer Recognizer { get; }
    public string ModelName { get; }

    public OcrEngine(string modelName, string detModel, string recModel, string dictPath, SessionOptions options)
    {
        ModelName = modelName;
        Detector = new Detector(detModel, options);
        Recognizer = new Recognizer(recModel, dictPath, options);
    }

    public void Dispose()
    {
        Detector.Dispose();
        Recognizer.Dispose();
    }

    /// <summary>When set, per-stage timings are written to stderr.</summary>
    public static bool Verbose { get; set; }

    public List<OcrLine> Run(BgrImage img)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var boxes = SortQuadBoxes(Detector.Detect(img));
        long tDet = sw.ElapsedMilliseconds;

        var crops = new List<BgrImage>(boxes.Count);
        foreach (var box in boxes) crops.Add(CropMinAreaRect(img, box));
        long tCrop = sw.ElapsedMilliseconds - tDet;

        var rec = Recognizer.Recognize(crops);
        long tRec = sw.ElapsedMilliseconds - tDet - tCrop;
        if (Verbose)
            Console.Error.WriteLine($"  det {tDet} ms (infer {Detector.LastInferMs} ms, post {Detector.LastPostMs} ms), crop {tCrop} ms, rec {tRec} ms (infer {Recognizer.LastInferMs} ms) for {boxes.Count} boxes");

        var lines = new List<OcrLine>(boxes.Count);
        for (int i = 0; i < boxes.Count; i++)
            lines.Add(new OcrLine(rec[i].Text, rec[i].Score, boxes[i].X, boxes[i].Y));
        return lines;
    }

    /// <summary>SortQuadBoxes: top to bottom, then left to right for boxes on the same line.</summary>
    private static List<DetBox> SortQuadBoxes(List<DetBox> boxes)
    {
        var sorted = boxes.OrderBy(b => b.Y[0]).ThenBy(b => b.X[0]).ToList();
        for (int i = 0; i < sorted.Count - 1; i++)
        {
            for (int j = i; j >= 0; j--)
            {
                if (Math.Abs(sorted[j + 1].Y[0] - sorted[j].Y[0]) < 10 && sorted[j + 1].X[0] < sorted[j].X[0])
                    (sorted[j], sorted[j + 1]) = (sorted[j + 1], sorted[j]);
                else
                    break;
            }
        }
        return sorted;
    }

    /// <summary>CropByPolys.get_minarea_rect_crop followed by get_rotate_crop_image.</summary>
    private static BgrImage CropMinAreaRect(BgrImage img, DetBox box)
    {
        var pts = new Pt[4];
        for (int i = 0; i < 4; i++) pts[i] = new Pt(box.X[i], box.Y[i]);
        var (ordered, _) = Geometry.GetMiniBoxes(pts);

        int cw = (int)Math.Max(Geometry.Dist(ordered[0], ordered[1]), Geometry.Dist(ordered[2], ordered[3]));
        int ch = (int)Math.Max(Geometry.Dist(ordered[0], ordered[3]), Geometry.Dist(ordered[1], ordered[2]));
        if (cw <= 0 || ch <= 0) return new BgrImage(0, 0);

        var dst = new[] { new Pt(0, 0), new Pt(cw, 0), new Pt(cw, ch), new Pt(0, ch) };
        var m = Geometry.GetPerspectiveTransform(ordered, dst);
        var crop = img.WarpPerspectiveCubic(Geometry.Invert3x3(m), cw, ch);
        if ((double)crop.Height / crop.Width >= 1.5) crop = crop.Rot90();
        return crop;
    }
}
