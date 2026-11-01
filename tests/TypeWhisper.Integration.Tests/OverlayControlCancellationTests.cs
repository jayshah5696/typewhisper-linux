using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Integration.Tests;

public sealed class OverlayControlCancellationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public Task FinishingCancel_AndStaleButtons_CannotStopANewerRecording() => BoundedTest.RunAsync(async () =>
    {
        var pipeline = new HeldPipeline();
        await using var fixture = new OrchestratorCompositionFixture(pipeline: pipeline);
        var coordinator = fixture.Provider.GetRequiredService<OverlayCoordinator>();
        fixture.Plugin.EnqueueText("Words waiting for cleanup");
        var first = await BoundedTest.WaitAsync(fixture.Orchestrator.StartAsync());
        fixture.FeedNonSilentAudio();
        var oldOwner = Assert.IsType<OverlayPresentationToken>(coordinator.PresentedToken);
        Assert.True(fixture.Orchestrator.IsRecording);
        var stop = fixture.Orchestrator.StopOverlayAsync(oldOwner);
        try
        {
            await BoundedTest.WaitAsync(pipeline.Started.Task);
            Assert.False(fixture.Orchestrator.IsRecording);
            await BoundedTest.WaitAsync(fixture.Orchestrator.CancelOverlayAsync(oldOwner));
            Assert.True(pipeline.Token.IsCancellationRequested);
            var second = await BoundedTest.WaitAsync(fixture.Orchestrator.StartAsync());
            Assert.True(second > first);
            var currentOwner = coordinator.PresentedToken!;
            await BoundedTest.WaitAsync(fixture.Orchestrator.StopOverlayAsync(oldOwner));
            await BoundedTest.WaitAsync(fixture.Orchestrator.CancelOverlayAsync(oldOwner));
            Assert.True(fixture.Orchestrator.IsRecording);
            pipeline.Release.TrySetResult();
            await BoundedTest.WaitAsync(stop);
            Assert.True(fixture.Orchestrator.IsRecording);
            Assert.Same(currentOwner, coordinator.PresentedToken);
            Assert.Empty(fixture.InsertionPlatform.Typed);
            Assert.Empty(fixture.InsertionPlatform.ClipboardWrites);
            await BoundedTest.WaitAsync(fixture.Orchestrator.CancelOverlayAsync(currentOwner));
            Assert.False(fixture.Orchestrator.IsRecording);
        }
        finally
        {
            pipeline.Release.TrySetResult();
            await BoundedTest.WaitAsync(stop);
            if (fixture.Orchestrator.IsRecording)
                await BoundedTest.WaitAsync(fixture.Orchestrator.CancelAsync());
        }
    });

    private sealed class HeldPipeline : IPostProcessingPipeline
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }
        public async Task<PostProcessingResult> ProcessAsync(string rawText, PipelineOptions options, CancellationToken ct = default)
        {
            Token = ct;
            Started.TrySetResult();
            await Release.Task;
            ct.ThrowIfCancellationRequested();
            return new PostProcessingResult { Text = rawText };
        }
    }
}
