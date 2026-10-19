using Microsoft.ML.OnnxRuntime;

namespace AnywhereOcr;

/// <summary>ONNX Runtime session/run configuration tuned for a long-lived, mostly idle process.</summary>
public static class OrtSettings
{
    /// <summary>
    /// Hand freed CPU arena memory back to the OS after every run instead of keeping it cached.
    /// Only valid for CPU sessions: a DirectML session registers no CPU arena and rejects the option.
    /// </summary>
    public static bool ShrinkArenas { get; set; }

    public static SessionOptions MakeSessionOptions(bool dml, int deviceId, int threads)
    {
        ShrinkArenas = !dml;
        var so = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        };
        // Weights go straight to the device (or the session's own allocation) instead of
        // first being staged in the CPU arena, which would then keep that memory reserved.
        so.AddSessionConfigEntry("session.use_device_allocator_for_initializers", "1");
        // A background service should not burn CPU spinning for the next run.
        so.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        if (dml)
        {
            so.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
            so.EnableMemoryPattern = false;
            so.AppendExecutionProvider_DML(deviceId);
        }
        else
        {
            so.IntraOpNumThreads = threads > 0 ? threads : Environment.ProcessorCount;
        }
        return so;
    }

    public static RunOptions MakeRunOptions()
    {
        var ro = new RunOptions();
        if (ShrinkArenas)
            ro.AddRunConfigEntry("memory.enable_memory_arena_shrinkage", "cpu:0");
        return ro;
    }
}
