namespace ModShardLauncherTest;

public static class TestData
{
    public const string VanillaPath = @"E:\StoneShard_Mod_Data\data_files\vallina_v0.9.4.25.win";
    public const string GameDir = @"E:\SteamLibrary\steamapps\common\Stoneshard";
    public static string GameVanillaBackupPath => Path.Combine(GameDir, "vallina.win");
    public static string GameDataPath => Path.Combine(GameDir, "data.win");
}
