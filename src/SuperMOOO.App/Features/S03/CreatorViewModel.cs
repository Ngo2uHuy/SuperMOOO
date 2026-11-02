namespace SuperMOOO.App.Features.S03;

/// <summary>Gom hai tab của S03 (YouTube, Talkshow) để màn nhận một DataContext.</summary>
[Service(ServiceLifetime.Transient)]
public sealed class CreatorViewModel(YouTubeCreatorViewModel youTube, TalkshowViewModel talkshow)
{
    public YouTubeCreatorViewModel YouTube => youTube;
    public TalkshowViewModel Talkshow => talkshow;
}
