using Klipp.Core.Editing;

namespace Klipp.Tests.Editing;

public sealed class EditorPreviewRefreshGateTests
{
    [Fact]
    public void PointerUpdatesAreCoalescedButReleaseAlwaysRefreshes()
    {
        var gate = new EditorPreviewRefreshGate(1.0 / 30);
        Assert.True(gate.TryEnter(0, false));
        Assert.False(gate.TryEnter(0.01, false));
        Assert.False(gate.TryEnter(0.02, false));
        Assert.True(gate.TryEnter(0.034, false));
        Assert.True(gate.TryEnter(0.035, true));
        Assert.True(gate.TryEnter(0.036, false));
    }
}
