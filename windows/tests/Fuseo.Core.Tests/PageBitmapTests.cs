using Fuseo.Core.Imaging;
using Xunit;

namespace Fuseo.Core.Tests;

public class PageBitmapTests
{
    [Fact]
    public void Clone_IsIndependentBuffer()
    {
        var a = PageBitmap.Solid(4, 3, 10, 20, 30);
        var b = a.Clone();
        b.SetPixel(0, 0, 0, 0, 0, 255);
        Assert.Equal((10, 20, 30, 255), a.GetPixel(0, 0));
        Assert.Equal(16, a.Stride);
    }

    [Fact]
    public void RejectsMismatchedBuffer()
    {
        Assert.Throws<ArgumentException>(() => new PageBitmap(2, 2, new byte[15]));
    }
}
