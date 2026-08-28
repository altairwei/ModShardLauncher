using System;
using UndertaleModLib;

namespace ModShardLauncher.HotReload;

public static class Gen8Guard
{
    public static (string Baseline, string Product) Check(UndertaleData baseline, UndertaleData product)
    {
        string b = VersionOf(baseline), p = VersionOf(product);
        if (b != p)
            throw new InvalidOperationException($"GEN8 mismatch: baseline {b} vs product {p} — refusing to diff");
        return (b, p);
    }

    public static string VersionOf(UndertaleData d) =>
        $"{d.GeneralInfo.Major}.{d.GeneralInfo.Minor}.{d.GeneralInfo.Release}.{d.GeneralInfo.Build} bc{d.GeneralInfo.BytecodeVersion}";
}
