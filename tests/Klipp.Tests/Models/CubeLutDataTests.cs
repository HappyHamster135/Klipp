using System.Numerics;
using Klipp.Core.Models;

namespace Klipp.Tests.Models;

public sealed class CubeLutDataTests
{
    private static readonly string[] Identity = ["LUT_3D_SIZE 2", "0 0 0", "1 0 0", "0 1 0", "1 1 0", "0 0 1", "1 0 1", "0 1 1", "1 1 1"];

    [Theory]
    [InlineData(0.8f, 0.5f, 0.2f)]
    [InlineData(0.8f, 0.2f, 0.5f)]
    [InlineData(0.5f, 0.2f, 0.8f)]
    [InlineData(0.5f, 0.8f, 0.2f)]
    [InlineData(0.2f, 0.8f, 0.5f)]
    [InlineData(0.2f, 0.5f, 0.8f)]
    public void IdentityPreservesAllSixTetrahedra(float r, float g, float b)
    {
        var input = new Vector3(r, g, b);
        Vector3.Distance(CubeLutData.Parse(Identity).Sample(input), input).ShouldBeLessThan(0.00001f);
    }

    [Fact]
    public void InputDomainAndOutOfRangeInputsAreHandled()
    {
        var lut = CubeLutData.Parse(Identity.Concat(["DOMAIN_MIN 0.2 0.2 0.2", "DOMAIN_MAX 0.8 0.8 0.8"]));
        Vector3.Distance(lut.Sample(new Vector3(0.5f)), new Vector3(0.5f)).ShouldBeLessThan(0.00001f);
        lut.Sample(new Vector3(-1, 2, 0.2f)).ShouldBe(new Vector3(0, 1, 0));
    }

    [Fact]
    public void GpuTableUsesBlueFastRgbaLayout()
    {
        var bytes = CubeLutData.Parse(Identity).BuildBlueFastTransferTable(2);
        var values = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
        values.Take(8).ShouldBe([0f, 0f, 0f, 1f, 0f, 0f, 1f, 1f]);
        values.Skip(16).Take(4).ShouldBe([1f, 0f, 0f, 1f]);
    }

    [Fact]
    public void IncompleteOrNonFiniteLutIsRejected()
    {
        Should.Throw<FormatException>(() => CubeLutData.Parse(Identity.Take(8)));
        Should.Throw<FormatException>(() => CubeLutData.Parse(Identity.Take(8).Append("NaN 1 1")));
    }
}
