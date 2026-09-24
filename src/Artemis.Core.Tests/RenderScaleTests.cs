using Artemis.Core;
using Xunit;

namespace Artemis.Core.Tests;

public class RenderScaleTests
{
    [Fact]
    public void QuarterScaleKeepsBothHalvesOfDualLedKeyDistinct()
    {
        int previousMultiplier = RenderScale.RenderScaleMultiplier;
        try
        {
            RenderScale.SetRenderScaleMultiplier(4);

            // Primary and secondary key LEDs with sub-pixel heights at 25% scale.
            var primary = RenderScale.CreateScaleCompatibleRect(862, 1022.5f, 11.6f, 6.1f);
            var secondary = RenderScale.CreateScaleCompatibleRect(862, 1031, 11.6f, 4.9f);

            Assert.True(primary.Width >= 4 && primary.Height >= 4);
            Assert.True(secondary.Width >= 4 && secondary.Height >= 4);
            Assert.True(primary.Bottom <= secondary.Top);
            Assert.InRange(1026, primary.Top, primary.Bottom - 1);
            Assert.InRange(1034, secondary.Top, secondary.Bottom - 1);
        }
        finally
        {
            RenderScale.SetRenderScaleMultiplier(previousMultiplier);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void SmallLedNeverDisappearsAtSupportedScales(int multiplier)
    {
        int previousMultiplier = RenderScale.RenderScaleMultiplier;
        try
        {
            RenderScale.SetRenderScaleMultiplier(multiplier);
            var rect = RenderScale.CreateScaleCompatibleRect(861.25f, 1031, 2.5f, 4.9f);

            Assert.True(rect.Width >= multiplier);
            Assert.True(rect.Height >= multiplier);
            Assert.Equal(0, rect.Left % multiplier);
            Assert.Equal(0, rect.Top % multiplier);
        }
        finally
        {
            RenderScale.SetRenderScaleMultiplier(previousMultiplier);
        }
    }
}
