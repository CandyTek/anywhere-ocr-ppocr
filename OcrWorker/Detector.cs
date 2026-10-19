using Microsoft.ML.OnnxRuntime;

namespace AnywhereOcr;

public sealed class DetBox
{
    public int[] X = new int[4];
    public int[] Y = new int[4];
    public double Score;
}

/// <summary>
/// PP-OCRv6 text detection: DetResizeForTest + NormalizeImage + DB post-processing,
/// following paddlex.inference.models.text_detection with the OCR pipeline's defaults.
/// </summary>
public sealed class Detector : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string _outputName;

    public int LimitSideLen { get; set; } = 64;
    public string LimitType { get; set; } = "min";
    public int MaxSideLimit { get; set; } = 4000;
    public float Thresh { get; set; } = 0.3f;
    public double BoxThresh { get; set; } = 0.6;
    public double UnclipRatio { get; set; } = 1.5;
    public int MaxCandidates { get; set; } = 1000;
    private const int MinSize = 3;

    // PaddleOCR applies the ImageNet mean/std in channel order to a BGR image.
    private static readonly float[] Mean = { 0.485f, 0.456f, 0.406f };
    private static readonly float[] Std = { 0.229f, 0.224f, 0.225f };

    public Detector(string modelPath, SessionOptions options)
    {
        _session = new InferenceSession(modelPath, options);
        _inputName = _session.InputMetadata.Keys.First();
        _outputName = _session.OutputMetadata.Keys.First();
    }

    public void Dispose() => _session.Dispose();

    public List<DetBox> Detect(BgrImage img)
    {
        int srcW = img.Width, srcH = img.Height;
        var work = img;
        if (srcH + srcW < 64) work = img.PadTo(32, 32);
        int h = work.Height, w = work.Width;

        double ratio = 1.0;
        if (LimitType == "max")
        {
            if (Math.Max(h, w) > LimitSideLen) ratio = (double)LimitSideLen / Math.Max(h, w);
        }
        else
        {
            if (Math.Min(h, w) < LimitSideLen) ratio = (double)LimitSideLen / Math.Min(h, w);
        }
        int rh = (int)(h * ratio), rw = (int)(w * ratio);
        if (Math.Max(rh, rw) > MaxSideLimit)
        {
            double r2 = (double)MaxSideLimit / Math.Max(rh, rw);
            rh = (int)(rh * r2); rw = (int)(rw * r2);
        }
        rh = Math.Max((int)(Math.Round(rh / 32.0, MidpointRounding.ToEven) * 32), 32);
        rw = Math.Max((int)(Math.Round(rw / 32.0, MidpointRounding.ToEven) * 32), 32);

        var resized = work.ResizeLinear(rw, rh);
        int plane = rh * rw;
        var input = new float[3 * plane];
        byte[] d = resized.Data;
        float m0 = Mean[0], m1 = Mean[1], m2 = Mean[2], s0 = Std[0], s1 = Std[1], s2 = Std[2];
        for (int i = 0; i < plane; i++)
        {
            int s = i * 3;
            input[i] = (d[s] / 255f - m0) / s0;
            input[plane + i] = (d[s + 1] / 255f - m1) / s1;
            input[2 * plane + i] = (d[s + 2] / 255f - m2) / s2;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        float[] pred = Run(input, rh, rw, out int ph, out int pw);
        LastInferMs = sw.ElapsedMilliseconds;
        sw.Restart();
        var boxes = PostProcess(pred, pw, ph, srcW, srcH);
        LastPostMs = sw.ElapsedMilliseconds;
        return boxes;
    }

    public long LastInferMs { get; private set; }
    public long LastPostMs { get; private set; }

    private float[] Run(float[] input, int h, int w, out int outH, out int outW)
    {
        long[] shape = { 1, 3, h, w };
        using var inVal = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, input.AsMemory(), shape);
        using var runOpts = OrtSettings.MakeRunOptions();
        using var outputs = _session.Run(runOpts, new[] { _inputName }, new[] { inVal }, new[] { _outputName });
        var dims = outputs[0].GetTensorTypeAndShape().Shape;
        outH = (int)dims[^2];
        outW = (int)dims[^1];
        return outputs[0].GetTensorDataAsSpan<float>().ToArray();
    }

    /// <summary>DBPostProcess.boxes_from_bitmap over 8-connected components of pred &gt; thresh.</summary>
    private List<DetBox> PostProcess(float[] pred, int W, int H, int destW, int destH)
    {
        var boxes = new List<DetBox>();
        int n = W * H;
        var fg = new byte[n];
        float thresh = Thresh;
        for (int i = 0; i < n; i++) fg[i] = pred[i] > thresh ? (byte)1 : (byte)0;
        var visited = new bool[n];
        var stack = new Stack<int>();
        var boundary = new List<Pt>();
        double widthScale = (double)destW / W, heightScale = (double)destH / H;
        int candidates = 0;

        for (int sy = 0; sy < H; sy++)
        {
            for (int sx = 0; sx < W; sx++)
            {
                int start = sy * W + sx;
                if (fg[start] == 0 || visited[start]) continue;
                if (++candidates > MaxCandidates) return boxes;

                boundary.Clear();
                visited[start] = true;
                stack.Push(start);
                while (stack.Count > 0)
                {
                    int cur = stack.Pop();
                    int cy = cur / W, cx = cur - cy * W;
                    bool isBoundary = false;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int ny = cy + dy;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            int nx = cx + dx;
                            bool fourNeighbour = dx == 0 || dy == 0;
                            if (nx < 0 || ny < 0 || nx >= W || ny >= H)
                            {
                                if (fourNeighbour) isBoundary = true;
                                continue;
                            }
                            int ni = ny * W + nx;
                            if (fg[ni] == 0)
                            {
                                if (fourNeighbour) isBoundary = true;
                            }
                            else if (!visited[ni])
                            {
                                visited[ni] = true;
                                stack.Push(ni);
                            }
                        }
                    }
                    if (isBoundary) boundary.Add(new Pt(cx, cy));
                }

                var (box, sside) = Geometry.GetMiniBoxes(boundary);
                if (sside < MinSize) continue;
                double score = Geometry.BoxScoreFast(pred, W, H, box);
                if (BoxThresh > score) continue;

                var expanded = Geometry.UnclipRect(box, UnclipRatio);
                var (box2, sside2) = Geometry.GetMiniBoxes(expanded);
                if (sside2 < MinSize + 2) continue;

                var db = new DetBox { Score = score };
                for (int i = 0; i < 4; i++)
                {
                    db.X[i] = (int)Math.Max(0, Math.Min(Math.Round(box2[i].X * widthScale, MidpointRounding.ToEven), destW));
                    db.Y[i] = (int)Math.Max(0, Math.Min(Math.Round(box2[i].Y * heightScale, MidpointRounding.ToEven), destH));
                }
                boxes.Add(db);
            }
        }
        return boxes;
    }
}
