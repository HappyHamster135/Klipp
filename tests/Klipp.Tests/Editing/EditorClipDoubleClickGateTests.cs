using Klipp.Core.Editing;

namespace Klipp.Tests.Editing;

public sealed class EditorClipDoubleClickGateTests
{
    [Theory]
    [InlineData("video:a", 0)]
    [InlineData("audio:a", 0)]
    [InlineData("audio:a", 1)]
    public void TwoReleasedBodyClicksProduceExactlyOneAction(string key, int modifiers)
    {
        var gate = new EditorClipDoubleClickGate();
        Assert.False(gate.Press(key, 1, 200, 100, modifiers));
        gate.Release(wasClick: true);
        Assert.True(gate.Press(key, 1.15, 201, 100, modifiers));
        gate.Release(wasClick: true);
        Assert.False(gate.Press(key, 1.25, 201, 100, modifiers));
    }

    [Fact]
    public void ADragOrCanceledGestureCannotBecomeADoubleClick()
    {
        var gate = new EditorClipDoubleClickGate();
        gate.Press("video:a", 1, 200, 100);
        gate.Release(wasClick: false);
        Assert.False(gate.Press("video:a", 1.15, 200, 100));
        gate.Release(wasClick: true);
        gate.Reset();
        Assert.False(gate.Press("video:a", 1.3, 200, 100));
    }

    [Fact]
    public void TrimEdgePressClearsAPendingBodyClick()
    {
        var gate = new EditorClipDoubleClickGate();
        gate.Press("audio:a", 1, 200, 100);
        gate.Release(true);
        Assert.False(gate.Press("audio:a", 1.1, 200, 100, isBody: false));
        gate.Release(true);
        Assert.False(gate.Press("audio:a", 1.2, 200, 100));
    }

    [Theory]
    [InlineData("video:b", 1.1, 200, 100, 0)]
    [InlineData("audio:a", 1.1, 200, 100, 0)]
    [InlineData("video:a", 1.6, 200, 100, 0)]
    [InlineData("video:a", .9, 200, 100, 0)]
    [InlineData("video:a", 1.1, 207, 100, 0)]
    [InlineData("video:a", 1.1, 200, 107, 0)]
    [InlineData("video:a", 1.1, 200, 100, 1)]
    public void DifferentClipTimePositionOrModifierStartsANewGesture(string key, double time, double x, double y, int modifiers)
    {
        var gate = new EditorClipDoubleClickGate();
        gate.Press("video:a", 1, 200, 100);
        gate.Release(true);
        Assert.False(gate.Press(key, time, x, y, modifiers));
    }

    [Fact]
    public void ASecondPressBeforeReleaseCannotSplit()
    {
        var gate = new EditorClipDoubleClickGate();
        gate.Press("video:a", 1, 200, 100);
        Assert.False(gate.Press("video:a", 1.1, 200, 100));
    }
}
