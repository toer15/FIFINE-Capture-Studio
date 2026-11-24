using FifineCaptureStudio.Rendering;
using Xunit;

namespace FifineCaptureStudio.Tests;

public class PresentationQueueTests
{
    private static PresentationFrame Frame(long sequence, long dueTicks = 0) => new(
        Bgra: new byte[4],
        SharedTextureHandle: IntPtr.Zero,
        Width: 1,
        Height: 1,
        DueTicks: dueTicks,
        Generated: false,
        SourceSequence: sequence,
        SourceArrivalTicks: 0,
        EnqueuedTicks: dueTicks);

    [Fact]
    public void Queue_DiscardsOldestFrameWhenProducerGetsAhead()
    {
        var queue = new PresentationQueue(capacity: 2);

        Assert.True(queue.Enqueue(Frame(1)));
        Assert.True(queue.Enqueue(Frame(2)));
        Assert.False(queue.Enqueue(Frame(3)));

        Assert.True(queue.TryDequeue(out var first));
        Assert.True(queue.TryDequeue(out var second));
        Assert.Equal(2, first!.SourceSequence);
        Assert.Equal(3, second!.SourceSequence);
        Assert.Equal(3, queue.SubmittedFrames);
        Assert.Equal(1, queue.DroppedFrames);
    }

    [Fact]
    public void Queue_PreservesSequenceAndDueTimeForPresenterTelemetry()
    {
        var queue = new PresentationQueue();
        var due = 123456L;
        queue.Enqueue(Frame(42, due));

        Assert.True(queue.TryPeek(out var peeked));
        Assert.Equal(42, peeked!.SourceSequence);
        Assert.Equal(due, peeked.DueTicks);
        Assert.False(peeked.IsGpu);
    }

    [Fact]
    public void Queue_NotifiesEvictionSoGpuLeasesCanBeReleased()
    {
        PresentationFrame? evicted = null;
        var queue = new PresentationQueue(capacity: 1, frame => evicted = frame);

        queue.Enqueue(Frame(1));
        queue.Enqueue(Frame(2));

        Assert.NotNull(evicted);
        Assert.Equal(1, evicted!.SourceSequence);
        Assert.True(queue.TryDequeue(out var current));
        Assert.Equal(2, current!.SourceSequence);
    }

    [Fact]
    public void Queue_CapacityMustBePositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PresentationQueue(0));
    }
}
