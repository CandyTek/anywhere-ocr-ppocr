using System.Text;
using Microsoft.ML.OnnxRuntime;

namespace AnywhereOcr;

/// <summary>
/// PP-OCRv6 text recognition: OCRReisizeNormImg + ToBatch + CTCLabelDecode,
/// following paddlex.inference.models.text_recognition.
/// </summary>
public sealed class Recognizer : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string[] _outputNames;
    private readonly bool _graphHasArgMax;
    private readonly string[] _chars;
    private bool _outputIsProbability = true;

    public int BatchSize { get; set; } = 6;
    public int DictionarySize => _chars.Length;
    public long LastInferMs { get; private set; }
    /// <summary>True when the exported graph provides ctc_argmax/ctc_maxprob outputs (see tools/export_onnx.py).</summary>
    public bool UsesGraphArgMax => _graphHasArgMax;

    private const int ImgH = 48;
    private const int ImgW = 320;
    private const int MaxImgW = 3200;

    public Recognizer(string modelPath, string dictPath, SessionOptions options)
    {
        _session = new InferenceSession(modelPath, options);
        _inputName = _session.InputMetadata.Keys.First();
        _graphHasArgMax = _session.OutputMetadata.ContainsKey("ctc_argmax")
                          && _session.OutputMetadata.ContainsKey("ctc_maxprob");
        _outputNames = _graphHasArgMax
            ? new[] { "ctc_argmax", "ctc_maxprob" }
            : new[] { _session.OutputMetadata.Keys.First() };

        // CTCLabelDecode: ["blank"] + character_dict + [" "]
        var list = new List<string> { "blank" };
        using (var reader = new StreamReader(dictPath, Encoding.UTF8))
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
                list.Add(line);
        }
        list.Add(" ");
        _chars = list.ToArray();

        // Warm up; with the full-logits graph also find out whether it already ends in softmax.
        var probe = new float[3 * ImgH * ImgW];
        var output = Run(probe, 1, ImgW);
        if (!_graphHasArgMax)
        {
            int c = output.Classes;
            if (c != _chars.Length)
                Console.Error.WriteLine($"warning: dictionary has {_chars.Length} entries but the model emits {c} classes");
            double sum = 0; bool nonNegative = true;
            for (int i = 0; i < c; i++) { sum += output.Logits![i]; if (output.Logits[i] < 0) nonNegative = false; }
            _outputIsProbability = nonNegative && Math.Abs(sum - 1.0) < 1e-2;
        }
    }

    public void Dispose() => _session.Dispose();

    /// <summary>Recognizes every crop; results are aligned with the input order.</summary>
    public (string Text, double Score)[] Recognize(IReadOnlyList<BgrImage> crops)
    {
        int n = crops.Count;
        var results = new (string, double)[n];
        LastInferMs = 0;
        if (n == 0) return results;

        // The OCR pipeline sorts crops by aspect ratio so that each batch pads as little as possible.
        var order = Enumerable.Range(0, n)
            .OrderBy(i => crops[i].Height == 0 ? 0.0 : (double)crops[i].Width / crops[i].Height)
            .ToArray();

        for (int b = 0; b < n; b += BatchSize)
        {
            int count = Math.Min(BatchSize, n - b);
            var tensors = new float[count][];
            var widths = new int[count];
            int maxW = 0;
            for (int k = 0; k < count; k++)
            {
                var img = crops[order[b + k]];
                if (img.Width == 0 || img.Height == 0)
                {
                    tensors[k] = new float[3 * ImgH * ImgW];
                    widths[k] = ImgW;
                }
                else
                {
                    tensors[k] = ResizeNorm(img, out widths[k]);
                }
                maxW = Math.Max(maxW, widths[k]);
            }

            var batch = new float[count * 3 * ImgH * maxW];
            for (int k = 0; k < count; k++)
            {
                int wk = widths[k];
                for (int c = 0; c < 3; c++)
                    for (int y = 0; y < ImgH; y++)
                        Array.Copy(tensors[k], (c * ImgH + y) * wk, batch, ((k * 3 + c) * ImgH + y) * maxW, wk);
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var output = Run(batch, count, maxW);
            LastInferMs += sw.ElapsedMilliseconds;
            for (int k = 0; k < count; k++)
                results[order[b + k]] = Decode(output, k);
        }
        return results;
    }

    private sealed class RecOutput
    {
        public int T;
        public int Classes;
        public float[]? Logits;   // [N, T, Classes] when the graph has no argmax outputs
        public long[]? ArgMax;    // [N, T]
        public float[]? MaxProb;  // [N, T]
    }

    private RecOutput Run(float[] input, int batch, int width)
    {
        long[] shape = { batch, 3, ImgH, width };
        using var inVal = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, input.AsMemory(), shape);
        using var runOpts = OrtSettings.MakeRunOptions();
        using var outputs = _session.Run(runOpts, new[] { _inputName }, new[] { inVal }, _outputNames);
        var dims = outputs[0].GetTensorTypeAndShape().Shape;
        if (_graphHasArgMax)
        {
            return new RecOutput
            {
                T = (int)dims[1],
                ArgMax = outputs[0].GetTensorDataAsSpan<long>().ToArray(),
                MaxProb = outputs[1].GetTensorDataAsSpan<float>().ToArray(),
            };
        }
        return new RecOutput
        {
            T = (int)dims[1],
            Classes = (int)dims[2],
            Logits = outputs[0].GetTensorDataAsSpan<float>().ToArray(),
        };
    }

    /// <summary>OCRReisizeNormImg.resize + resize_norm_img for one crop (per-image max_wh_ratio).</summary>
    private static float[] ResizeNorm(BgrImage img, out int imgW)
    {
        double ratio = (double)img.Width / img.Height;
        double maxWhRatio = Math.Max((double)ImgW / ImgH, ratio);
        imgW = (int)(ImgH * maxWhRatio);
        int resizedW;
        BgrImage resized;
        if (imgW > MaxImgW)
        {
            resized = img.ResizeLinear(MaxImgW, ImgH);
            resizedW = MaxImgW;
            imgW = MaxImgW;
        }
        else
        {
            resizedW = Math.Ceiling(ImgH * ratio) > imgW ? imgW : (int)Math.Ceiling(ImgH * ratio);
            resizedW = Math.Max(resizedW, 1);
            resized = img.ResizeLinear(resizedW, ImgH);
        }

        var outp = new float[3 * ImgH * imgW];
        byte[] d = resized.Data;
        for (int y = 0; y < ImgH; y++)
        {
            for (int x = 0; x < resizedW; x++)
            {
                int s = (y * resizedW + x) * 3;
                outp[(0 * ImgH + y) * imgW + x] = (d[s] / 255f - 0.5f) / 0.5f;
                outp[(1 * ImgH + y) * imgW + x] = (d[s + 1] / 255f - 0.5f) / 0.5f;
                outp[(2 * ImgH + y) * imgW + x] = (d[s + 2] / 255f - 0.5f) / 0.5f;
            }
        }
        return outp;
    }

    /// <summary>CTCLabelDecode with is_remove_duplicate=True; blank index 0 is ignored.</summary>
    private (string, double) Decode(RecOutput output, int item)
    {
        var sb = new StringBuilder();
        double sum = 0; int count = 0; int prev = -1;
        int T = output.T;
        for (int t = 0; t < T; t++)
        {
            int idx;
            double prob;
            if (output.ArgMax != null)
            {
                idx = (int)output.ArgMax[item * T + t];
                prob = output.MaxProb![item * T + t];
            }
            else
            {
                int C = output.Classes;
                var row = output.Logits.AsSpan((item * T + t) * C, C);
                idx = 0; float max = row[0];
                for (int i = 1; i < C; i++)
                    if (row[i] > max) { max = row[i]; idx = i; }
                prob = max;
                if (!_outputIsProbability)
                {
                    double denom = 0;
                    for (int i = 0; i < C; i++) denom += Math.Exp(row[i] - max);
                    prob = 1.0 / denom;
                }
            }

            bool selected = t == 0 || idx != prev;
            prev = idx;
            if (!selected || idx == 0) continue;

            sb.Append(idx < _chars.Length ? _chars[idx] : "?");
            sum += prob; count++;
        }
        return (sb.ToString(), count == 0 ? 0.0 : sum / count);
    }
}
