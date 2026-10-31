namespace OsuStableRemake;

// stable's keyboard bindings: its list as shown in the bindings dialog (a leading '_' marks a category header) and
// the osu!.cfg key each row edits, with stable's defaults for keys the cfg doesn't have.
static class KeyBindings
{
    public static readonly (string Name, string Key, string Default)[] Rows =
    [
        ("_osu!", "", ""),
        ("Left Click", "keyOsuLeft", "Z"), ("Right Click", "keyOsuRight", "X"), ("Smoke", "keyOsuSmoke", "C"),
        ("_osu!taiko", "", ""),
        ("Drum Centre (Left)", "keyTaikoInnerLeft", "X"), ("Drum Centre (Right)", "keyTaikoInnerRight", "C"),
        ("Drum Rim (Left)", "keyTaikoOuterLeft", "Z"), ("Drum Rim (Right)", "keyTaikoOuterRight", "V"),
        ("_osu!catch", "", ""),
        ("Move Left", "keyFruitsLeft", "Left"), ("Move Right", "keyFruitsRight", "Right"),
        ("Dash! (2x movement speed)", "keyFruitsDash", "LeftShift"),
        ("_osu!mania", "", ""),
        ("Increase Speed", "keyIncreaseSpeed", "F4"), ("Decrease Speed", "keyDecreaseSpeed", "F3"),
        ("_In-Game", "", ""),
        ("Game Pause", "keyPause", "Escape"), ("Skip Cutscene", "keySkip", "Space"), ("Toggle Scoreboard", "keyToggleScoreboard", "Tab"),
        ("Increase Local Song Offset", "keyIncreaseAudioOffset", "OemPlus"), ("Decrease Local Song Offset", "keyDecreaseAudioOffset", "OemMinus"),
        ("Quick Retry (hold briefly)", "keyQuickRetry", "OemTilde"),
        ("_Universal", "", ""),
        ("Toggle Framerate Limit", "keyToggleFrameLimiter", "F7"), ("Toggle Chat", "keyToggleChat", "F8"),
        ("Toggle Extended Chat", "keyToggleExtendedChat", "F9"), ("Save Screenshot", "keyScreenshot", "F12"),
        ("Increase Volume", "keyVolumeIncrease", "Up"), ("Decrease Volume", "keyVolumeDecrease", "Down"),
        ("Disable mouse buttons", "keyDisableMouseButtons", "F10"), ("Boss key", "keyBossKey", "Insert"),
        ("_Editor", "", ""),
        ("Select Tool", "keySelectTool", "D1"), ("Normal Tool", "keyNormalTool", "D2"), ("Slider Tool", "keySliderTool", "D3"),
        ("Spinner Tool", "keySpinnerTool", "D4"), ("New Combo Toggle", "keyNewComboToggle", "Q"), ("Whistle Toggle", "keyWhistleToggle", "W"),
        ("Finish Toggle", "keyFinishToggle", "E"), ("Clap Toggle", "keyClapToggle", "R"), ("Grid Snap Toggle", "keyGridSnapToggle", "T"),
        ("Distance Snap Toggle", "keyDistSnapToggle", "Y"), ("Note Lock Toggle", "keyNoteLockToggle", "L"),
        ("Nudge Left", "keyNudgeLeft", "J"), ("Nudge Right", "keyNudgeRight", "K"), ("Help Toggle", "keyHelpToggle", "H"),
        ("Jump To First Note", "keyJumpToBegin", "Z"), ("Play From Beginning", "keyPlayFromBegin", "X"), ("Audio Pause", "keyAudioPause", "C"),
        ("Jump To End", "keyJumpToEnd", "V"), ("Grid Size Change", "keyGridChange", "G"), ("Add Timing Section", "keyTimingSection", "None"),
        ("Add Inheriting Section", "keyInheritingSection", "None"), ("Remove Section", "keyRemoveSection", "None"),
        ("_Mod Select", "", ""),
        ("Easy", "keyEasy", "Q"), ("No Fail", "keyNoFail", "W"), ("Half Time", "keyHalfTime", "E"), ("Hard Rock", "keyHardRock", "A"),
        ("Sudden Death", "keySuddenDeath", "S"), ("Double Time", "keyDoubleTime", "D"), ("Hidden", "keyHidden", "F"),
        ("Flashlight", "keyFlashlight", "G"), ("Relax", "keyRelax", "Z"), ("Autopilot", "keyAutopilot", "X"), ("Spun Out", "keySpunOut", "C"),
        ("Auto", "keyAuto", "V"), ("Score v2", "keyScoreV2", "B"),
    ];

    // The binding as stable shows it: the cfg key name without "Oem", digits without their D, and short names for
    // modifiers and punctuation.
    public static string Shown((string Name, string Key, string Default) row)
    {
        string k = Config.Get(row.Key, row.Default);
        return k switch
        {
            "Space" => "Spc", "LeftShift" => "LSft", "RightShift" => "RSft", "LeftControl" => "LCtl", "RightControl" => "RCtl",
            "LeftAlt" => "LAlt", "RightAlt" => "RAlt", "OemSemicolon" => ";", "OemComma" => ",", "OemPeriod" => ".", "OemQuestion" => "/",
            "OemQuotes" => "'", "OemOpenBrackets" => "[", "OemCloseBrackets" => "]", "OemPipe" => "\\",
            { Length: 2 } when k[0] == 'D' && char.IsDigit(k[1]) => k[1..],
            _ => k.Replace("Oem", ""),
        };
    }
}
