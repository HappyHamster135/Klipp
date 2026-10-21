using Klipp.Core.Editing;

namespace Klipp.Tests.Editing;

public sealed class EditorGroupMoveSnapTests
{
    [Fact]
    public void APartnerEdgeCanSnapEvenWhenThePressedClipIsFarFromTheTarget()
    {
        var plan = EditorSelectionEditMath.SnapGroupMove(.94, [(1, 2), (4, 6)], [7], .1);
        Assert.Equal(1, plan.Delta, 8);
        Assert.Equal(7, plan.Guide);
        Assert.Equal(3, (4 + plan.Delta) - (1 + plan.Delta), 8);
    }

    [Fact]
    public void ClosestEdgeWinsAcrossBothVideoAndAudioSelection()
    {
        var plan = EditorSelectionEditMath.SnapGroupMove(1, [(1, 2), (4, 6)], [3.08, 7.02], .1);
        Assert.Equal(1.02, plan.Delta, 8);
        Assert.Equal(7.02, plan.Guide);
    }

    [Fact]
    public void APartnerCannotSnapTheWholeGroupBeforeTimelineZero()
    {
        var plan = EditorSelectionEditMath.SnapGroupMove(-.95, [(1, 2), (3, 6)], [1.95], .1);
        Assert.Equal(-.95, plan.Delta, 8);
        Assert.Null(plan.Guide);
    }

    [Fact]
    public void ZeroBoundaryCanSnapWithoutChangingGroupOffsets()
    {
        var plan = EditorSelectionEditMath.SnapGroupMove(-.95, [(1, 2), (3, 6)], [0], .1);
        Assert.Equal(-1, plan.Delta, 8);
        Assert.Equal(0, plan.Guide);
    }

    [Fact]
    public void ATargetOutsideTheMagnetRangeDoesNotMoveTheGroup()
    {
        var plan = EditorSelectionEditMath.SnapGroupMove(1, [(1, 2), (4, 6)], [7.2], .1);
        Assert.Equal(1, plan.Delta);
        Assert.Null(plan.Guide);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    public void InvalidTargetsAreIgnored(double target)
    {
        var plan = EditorSelectionEditMath.SnapGroupMove(1, [(1, 2)], [target], .1);
        Assert.Equal(1, plan.Delta);
        Assert.Null(plan.Guide);
    }
}
