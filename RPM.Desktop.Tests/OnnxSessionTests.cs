using System;
using System.IO;
using Microsoft.ML.OnnxRuntime;
using RPM.Onnx;

namespace RPM.Desktop.Tests;

/// <summary>
/// <see cref="OnnxSession.Open"/> owns the options it is given: it keeps them alive until the session
/// exists and disposes them afterwards, also when the model does not open.
/// </summary>
public class OnnxSessionTests
{
    private static string Model(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "OnnxModels", name);

    [Fact]
    public void The_session_opens_and_the_options_are_disposed()
    {
        var options = new SessionOptions { IntraOpNumThreads = 1 };

        using var session = OnnxSession.Open(Model("ultraface.onnx"), options);

        Assert.NotEmpty(session.InputMetadata);
        Assert.True(options.IsClosed);
    }

    [Fact]
    public void A_model_that_does_not_open_still_disposes_the_options()
    {
        var options = new SessionOptions();

        Assert.ThrowsAny<OnnxRuntimeException>(
            () => OnnxSession.Open(Model("no-such-model.onnx"), options));
        Assert.True(options.IsClosed);
    }

    /// <summary>Every model the app opens, opened while another thread collects and finalises
    /// without a pause. The options are built in the call, so nothing but Open holds them.</summary>
    [Fact]
    public void Sessions_open_while_the_collector_runs()
    {
        using var stop = new System.Threading.CancellationTokenSource();
        var collector = new System.Threading.Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }) { IsBackground = true };
        collector.Start();

        try
        {
            for (var i = 0; i < 20; i++)
                foreach (var name in new[] { "ultraface.onnx", "u2netp.onnx" })
                {
                    using var session = OnnxSession.Open(Model(name), new SessionOptions
                    {
                        IntraOpNumThreads = 2,
                        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    });
                    Assert.NotEmpty(session.InputMetadata);
                }
        }
        finally
        {
            stop.Cancel();
            collector.Join();
        }
    }
}
