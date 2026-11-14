namespace OniCoop.Net
{
    using System.Collections.Generic;

    /// <summary>Join-time check: can this guest load the host's world and play with it?
    /// Errors refuse the join; warnings are shown but allowed.</summary>
    public static class Compat
    {
        public const string Expansion1 = "EXPANSION1_ID";

        public static string DlcName(string id)
        {
            switch (id)
            {
                case "EXPANSION1_ID": return "眼冒金星 Spaced Out!";
                case "DLC2_ID": return "The Frosty Planet Pack";
                case "DLC3_ID": return "The Bionic Booster Pack";
                case "DLC4_ID": return "The Prehistoric Planet Pack";
                default: return id;
            }
        }

        /// <param name="hostGameDlcs">DLC ids of the save the host has loaded, or null while the host is not in a game
        /// (the DLC part of the check then waits until it is).</param>
        public static void Check(Manifest host, List<string> hostGameDlcs, Manifest guest, List<string> errors, List<string> warnings)
        {
            if (host.build != guest.build)
                errors.Add($"游戏版本不同：主机 {host.build}，你 {guest.build}。请把游戏更新到同一版本。");
            if (host.coopVersion != guest.coopVersion)
                errors.Add($"联机 mod 版本不同：主机 {host.coopVersion}，你 {guest.coopVersion}。");

            if (hostGameDlcs != null)
            {
                // Same rule as SaveGame.GameInfo.IsCompatableWithCurrentDlcConfiguration.
                foreach (string d in hostGameDlcs)
                    if (!string.IsNullOrEmpty(d) && !guest.dlcs.Contains(d)) // "" = the base game itself
                        errors.Add($"主机的存档需要 DLC「{DlcName(d)}」：请确认拥有并在主菜单启用它。");
                if (!hostGameDlcs.Contains(Expansion1) && guest.dlcs.Contains(Expansion1))
                    errors.Add("主机玩的是原版存档：请在主菜单关闭「眼冒金星」后再加入。");
            }

            var hostMods = new Dictionary<string, ModEntry>();
            foreach (var m in host.mods) hostMods[m.staticId] = m;
            var guestMods = new Dictionary<string, ModEntry>();
            foreach (var m in guest.mods) guestMods[m.staticId] = m;

            foreach (var m in host.mods)
            {
                if (!guestMods.TryGetValue(m.staticId, out var g))
                    errors.Add($"缺少 mod「{m.title}」（{m.staticId}）：主机启用了它。");
                else if (g.version != m.version)
                    warnings.Add($"mod「{m.title}」版本不同（主机 {m.version}，你 {g.version}）。");
            }
            foreach (var m in guest.mods)
                if (!hostMods.ContainsKey(m.staticId))
                    warnings.Add($"你多启用了 mod「{m.title}」（{m.staticId}），主机没有。");
        }
    }
}
