using System.IO;
using System.Media;

namespace PowerControlProWpf;

internal static class UiSoundService
{
    private static readonly Dictionary<string, (MemoryStream Stream, SoundPlayer Player)> Players = new();
    internal static bool MuteForTests { get; set; }

    internal static void PlayAdminWarning() => Play("warnings/beep.wav");

    private static void Play(string resourceName)
    {
        if (MuteForTests) return;
        try
        {
            if (!Players.TryGetValue(resourceName, out var sound))
            {
                var uri = new Uri($"/{typeof(UiSoundService).Assembly.GetName().Name};component/Assets/sounds/{resourceName}", UriKind.Relative);
                var resource = System.Windows.Application.GetResourceStream(uri);
                if (resource is null) return;
                using (resource.Stream)
                {
                    var stream = new MemoryStream();
                    resource.Stream.CopyTo(stream);
                    stream.Position = 0;
                    var player = new SoundPlayer(stream);
                    player.Load();
                    sound = (stream, player);
                    Players[resourceName] = sound;
                }
            }
            sound.Player.Play();
        }
        catch (Exception)
        {
            // An unavailable audio device must never block a dialog or window action.
        }
    }
}
