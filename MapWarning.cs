namespace OsuStableRemake;

// stable's warning for maps that award no pp: the yellow notification band for 5s when play starts.
// TODO: the Qualified wording is a guess (stable's exact text is unknown); only the Loved text was captured.
sealed partial class Player
{
    bool warningChecked;

    void UpdateMapWarning()
    {
        if (warningChecked || back.StatusOf(mapMd5 ??= Replays.Md5Of(map.File)) is not { } status) return;
        warningChecked = true;
        string? text = status switch
        {
            7 => "This map is in a loved state.\nNo performance points will be awarded.\nLeaderboards may be reset.",
            6 => "This map is in a qualified state.\nNo performance points will be awarded.\nAll scores will be deleted when it is ranked.",
            _ => null,
        };
        if (text != null) Notification.Show(text, 5000, isWarning: true);
    }
}
