using Xunit;

namespace Jellyfin.Plugin.TorrCatalog.Tests;

public sealed class WatchStateTests : IDisposable
{
    static readonly long Hour = TimeSpan.FromHours(1).Ticks;

    readonly string _dir = Directory.CreateTempSubdirectory("torrcatalog-watch").FullName;
    readonly Guid _alice = Guid.NewGuid();
    readonly Guid _bob = Guid.NewGuid();
    readonly VirtualId _movie = VirtualId.Movie(42);

    string FilePath => Path.Combine(_dir, "watch-state.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void ProgressIsPerUserAndSurvivesRestart()
    {
        new WatchState(FilePath).ReportPosition(_alice, _movie, Hour / 2, Hour);

        var reloaded = new WatchState(FilePath);

        Assert.Equal(Hour / 2, reloaded.Get(_alice, _movie)!.PositionTicks);
        Assert.Null(reloaded.Get(_bob, _movie));
        Assert.Single(reloaded.Resumable(_alice));
        Assert.Empty(reloaded.Resumable(_bob));
    }

    [Fact]
    public void ThresholdsFollowJellyfin()
    {
        var state = new WatchState(FilePath);

        state.ReportPosition(_alice, _movie, Hour / 100, Hour); // 1%: not worth resuming
        Assert.Equal(0, state.Get(_alice, _movie)!.PositionTicks);

        state.ReportPosition(_alice, _movie, Hour * 95 / 100, Hour); // 95%: watched
        Assert.True(state.Get(_alice, _movie)!.Played);
        Assert.Empty(state.Resumable(_alice));

        state.ReportPosition(_alice, _movie, Hour / 2, Hour); // rewatching keeps the mark
        Assert.True(state.Get(_alice, _movie)!.Played);

        state.SetPlayed(_alice, _movie, false);
        Assert.False(state.Get(_alice, _movie)!.Played);
    }

    [Fact]
    public void VersionsShareTheBaseItemsProgress()
    {
        var state = new WatchState(FilePath);
        var version = _movie.WithRelease("abcdef0011223344");

        state.ReportPosition(_alice, version, Hour / 2, Hour);
        var data = state.UserData(_alice, version.ToGuid(), Hour);

        Assert.Equal(Hour / 2, state.Get(_alice, _movie)!.PositionTicks);
        Assert.Equal(50, data.PlayedPercentage);
        Assert.Equal(version.ToGuid(), data.ItemId);
    }
}
