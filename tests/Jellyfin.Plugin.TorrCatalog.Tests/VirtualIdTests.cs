using Xunit;

namespace Jellyfin.Plugin.TorrCatalog.Tests;

public class VirtualIdTests
{
    [Fact]
    public void MovieRoundTrips()
    {
        Assert.True(VirtualId.TryParse(VirtualId.Movie(693134).ToGuid(), out var id));
        Assert.Equal(VirtualId.Movie(693134), id);
    }

    [Fact]
    public void SameInputGivesSameGuid()
    {
        Assert.Equal(VirtualId.Movie(1).ToGuid(), VirtualId.Movie(1).ToGuid());
        Assert.Equal(VirtualId.Section("trending").ToGuid(), VirtualId.Section("trending").ToGuid());
    }

    [Fact]
    public void DifferentInputsGiveDifferentGuids()
    {
        Assert.NotEqual(VirtualId.Movie(1).ToGuid(), VirtualId.Movie(2).ToGuid());
        Assert.NotEqual(VirtualId.Section("trending").ToGuid(), VirtualId.Section("popular").ToGuid());
        Assert.NotEqual(VirtualId.Movie(1).ToGuid(), new VirtualId(VirtualKind.Section, 1).ToGuid());
    }

    [Fact]
    public void RegularGuidsAreNotVirtual()
    {
        Assert.False(VirtualId.TryParse(Guid.NewGuid(), out _));
        Assert.False(VirtualId.TryParse(Guid.Empty, out _));
    }
}
