using System.Diagnostics;
using System.Runtime;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;

namespace AnywhereOcr;

/// <summary>
/// Native replacement for PaddleOCR/PaddleOCR_worker.py. Same localhost protocol:
///   GET  /health        -> model name
///   POST /ocr[?all=1]   -> body is the path of an image file; replies with text lines or JSON
/// </summary>
public static class Program
{
    private static readonly object OcrLock = new();

    public static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);

        int port = 48626;
        string device = "cpu";
        string tier = "medium";
        string? modelsDir = null;
        string? testImage = null;
        bool testAll = false;
        int repeat = 1;
        int threads = 0;
        bool verbose = false;
        bool noWarmup = false;
        int idleUnload = 300;
        if (int.TryParse(Environment.GetEnvironmentVariable("ANYWHERE_OCR_IDLE_UNLOAD"), out int envIdle)) idleUnload = envIdle;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value");
            switch (a)
            {
                case "--port": port = int.Parse(Next()); break;
                case "--device": device = Next().ToLowerInvariant(); break;
                case "--model-tier": tier = Next(); break;
                case "--models": modelsDir = Next(); break;
                case "--test": testImage = Next(); break;
                case "--all": testAll = true; break;
                case "--repeat": repeat = int.Parse(Next()); break;
                case "--threads": threads = int.Parse(Next()); break;
                case "--verbose": verbose = true; break;
                case "--no-warmup": noWarmup = true; break;
                case "--idle-unload": idleUnload = int.Parse(Next()); break;
                case "--download-only": noWarmup = false; testImage = null; repeat = 0; break;
                case "-h":
                case "--help":
                    Console.WriteLine(
                        "AnywhereOcrWorker [--port 48626] [--device cpu|gpu:N] [--model-tier medium|small|tiny]\n" +
                        "                  [--models DIR] [--threads N] [--verbose]\n" +
                        "                  [--idle-unload SECONDS]   release the models after this long without\n" +
                        "                                            requests (default 300, 0 keeps them loaded)\n" +
                        "                  [--test IMAGE [--all] [--repeat N]]   run once on IMAGE and exit\n" +
                        "                  [--download-only]                     load the models, then exit");
                    return 0;
                default:
                    Console.Error.WriteLine($"unknown argument: {a}");
                    return 2;
            }
        }

        string modelName = $"PP-OCRv6_{tier}";
        string? dir = FindModelsDir(modelsDir, modelName);
        if (dir == null)
        {
            Console.Error.WriteLine(
                $"Cannot find {modelName}_det.onnx. Export the models with tools/export_onnx.py into the " +
                "'models' folder of the project, or pass --models DIR / set ANYWHERE_OCR_MODELS.");
            return 3;
        }
        string detPath = Path.Combine(dir, $"{modelName}_det.onnx");
        string recPath = Path.Combine(dir, $"{modelName}_rec.onnx");
        string dictPath = Path.Combine(dir, $"{modelName}_dict.txt");
        if (!File.Exists(dictPath)) dictPath = Path.Combine(dir, "PP-OCRv6_medium_dict.txt");
        foreach (var p in new[] { detPath, recPath, dictPath })
        {
            if (!File.Exists(p)) { Console.Error.WriteLine($"missing file: {p}"); return 3; }
        }

        bool wantGpu = device.StartsWith("gpu") || device.StartsWith("dml");
        int deviceId = 0;
        int colon = device.IndexOf(':');
        if (colon >= 0) int.TryParse(device.AsSpan(colon + 1), out deviceId);

        OcrEngine.Verbose = verbose;
        OcrEngine LoadEngine()
        {
            var sw = Stopwatch.StartNew();
            OcrEngine loaded;
            string backend;
            try
            {
                if (!wantGpu) throw new InvalidOperationException("cpu requested");
                loaded = new OcrEngine(modelName, detPath, recPath, dictPath, OrtSettings.MakeSessionOptions(true, deviceId, threads));
                backend = $"DirectML device {deviceId}";
            }
            catch (Exception e)
            {
                if (wantGpu) Console.Error.WriteLine($"DirectML unavailable ({e.Message}); using CPU");
                loaded = new OcrEngine(modelName, detPath, recPath, dictPath, OrtSettings.MakeSessionOptions(false, 0, threads));
                backend = $"CPU ({(threads > 0 ? threads : Environment.ProcessorCount)} threads)";
            }
            Console.Error.WriteLine($"{modelName} loaded from {dir} on {backend} in {sw.ElapsedMilliseconds} ms"
                + (loaded.Recognizer.UsesGraphArgMax ? "" : " (recognizer graph lacks ctc_argmax outputs; run tools/export_onnx.py --augment-only for lower memory)"));

            if (!noWarmup)
            {
                sw.Restart();
                var warm = new BgrImage(320, 96);
                Array.Fill(warm.Data, (byte)255);
                loaded.Run(warm);
                Console.Error.WriteLine($"warm-up finished in {sw.ElapsedMilliseconds} ms");
            }
            return loaded;
        }

        OcrEngine? engine = LoadEngine();
        if (repeat == 0) return 0;

        if (testImage != null)
        {
            var img = BgrImage.Load(testImage);
            List<OcrLine> lines = new();
            var sw = Stopwatch.StartNew();
            for (int r = 0; r < repeat; r++)
            {
                sw.Restart();
                lines = engine.Run(img);
                Console.Error.WriteLine($"run {r + 1}: {lines.Count} boxes in {sw.ElapsedMilliseconds} ms");
            }
            Console.WriteLine(testAll ? ToJson(lines) : ToPlainText(lines));
            TrimMemory();
            var proc = Process.GetCurrentProcess();
            Console.Error.WriteLine($"memory: working set {proc.WorkingSet64 >> 20} MB, private {proc.PrivateMemorySize64 >> 20} MB");
            return 0;
        }

        long lastUse = Environment.TickCount64;
        var server = new MiniHttpServer(port, (method, target, body) =>
        {
            if (method == "GET" && target == "/health")
                return new HttpReply(200, "text/plain", Encoding.UTF8.GetBytes(modelName));
            if (method != "POST" || !target.StartsWith("/ocr"))
                return new HttpReply(404, "text/plain", Encoding.UTF8.GetBytes("Not found"));

            string imagePath = Encoding.UTF8.GetString(body).Trim();
            bool all = target.EndsWith("?all=1");
            lock (OcrLock)
            {
                try
                {
                    if (!File.Exists(imagePath))
                        return new HttpReply(500, "text/plain", Encoding.UTF8.GetBytes($"FileNotFoundError: {imagePath}"));
                    var t = Stopwatch.StartNew();
                    engine ??= LoadEngine();
                    var img = BgrImage.Load(imagePath);
                    var lines = engine.Run(img);
                    lastUse = Environment.TickCount64;
                    if (verbose) Console.Error.WriteLine($"{img.Width}x{img.Height}: {lines.Count} boxes in {t.ElapsedMilliseconds} ms");
                    return all
                        ? new HttpReply(200, "application/json", Encoding.UTF8.GetBytes(ToJson(lines)))
                        : new HttpReply(200, "text/plain", Encoding.UTF8.GetBytes(ToPlainText(lines)));
                }
                catch (Exception e)
                {
                    return new HttpReply(500, "text/plain", Encoding.UTF8.GetBytes($"{e.GetType().Name}: {e.Message}"));
                }
                finally
                {
                    try { File.Delete(imagePath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    TrimMemory();
                }
            }
        });
        server.Start();
        TrimMemory();
        Console.Error.WriteLine($"listening on http://127.0.0.1:{port}/ (health: /health, ocr: POST /ocr)"
            + (idleUnload > 0 ? $", models are released after {idleUnload} s without requests" : ""));

        // Idle unloading: the DirectML/CPU allocators keep every buffer they ever needed, so the
        // only way to give that memory back while the process stays resident is to drop the sessions.
        while (true)
        {
            Thread.Sleep(1000);
            if (idleUnload <= 0) continue;
            lock (OcrLock)
            {
                if (engine != null && Environment.TickCount64 - lastUse > idleUnload * 1000L)
                {
                    engine.Dispose();
                    engine = null;
                    TrimMemory();
                    if (verbose) Console.Error.WriteLine("models released after idle period; they reload on the next request");
                }
            }
        }
    }

    /// <summary>Give freed buffers back to the OS between requests so the idle footprint stays low.</summary>
    private static void TrimMemory()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
    }

    private static string ToPlainText(List<OcrLine> lines) =>
        string.Join("\n", lines.Where(l => l.Text.Length > 0).Select(l => l.Text));

    private static string ToJson(List<OcrLine> lines)
    {
        var polys = lines.Select(l => new[] { new[] { l.X[0], l.Y[0] }, new[] { l.X[1], l.Y[1] }, new[] { l.X[2], l.Y[2] }, new[] { l.X[3], l.Y[3] } }).ToArray();
        var result = new[]
        {
            new Dictionary<string, object>
            {
                ["rec_texts"] = lines.Select(l => l.Text).ToArray(),
                ["rec_scores"] = lines.Select(l => l.Score).ToArray(),
                ["rec_polys"] = polys,
                ["dt_polys"] = polys,
                ["rec_boxes"] = lines.Select(l => new[] { l.X.Min(), l.Y.Min(), l.X.Max(), l.Y.Max() }).ToArray(),
            }
        };
        return JsonSerializer.Serialize(result, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    private static string? FindModelsDir(string? explicitDir, string modelName)
    {
        string probe = $"{modelName}_det.onnx";
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(explicitDir)) candidates.Add(explicitDir);
        var env = Environment.GetEnvironmentVariable("ANYWHERE_OCR_MODELS");
        if (!string.IsNullOrEmpty(env)) candidates.Add(env);
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6 && d != null; i++, d = d.Parent)
        {
            candidates.Add(d.FullName);
            candidates.Add(Path.Combine(d.FullName, "models"));
        }
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "models"));
        foreach (var c in candidates)
            if (File.Exists(Path.Combine(c, probe))) return Path.GetFullPath(c);
        return null;
    }
}
