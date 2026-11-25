using FifineCaptureStudio.Core.Streaming;
using Xunit;

namespace FifineCaptureStudio.Tests.Streaming;

public sealed class RegistrationTargetTests
{
    [Theory]
    [InlineData(1_000_100u, true, 1_000_100u)]
    [InlineData(900u, true, 900u)]
    [InlineData(0u, true, 1_000_100u)]
    [InlineData(0u, false, 1_000u)]
    public void Registration_uses_the_target_the_engine_reported(uint discovered, bool isPs5, uint expected)
    {
        Assert.Equal(expected, PlayStationStreamService.RegistrationTarget(discovered, isPs5));
    }
}
