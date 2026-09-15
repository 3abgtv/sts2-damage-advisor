namespace DamageAdvisor;

/// <summary>
/// 面板设置：掉血上限 / 优先级模式 / 是否折叠。
/// 存在 mods\DamageAdvisor\DamageAdvisor.cfg，启动时读取，改完立即写回；运行中也会定期重载。
/// </summary>
internal static class AdvisorSettings
{
    /// <summary>愿意为输出承受的掉血上限（0 = 严格保命）。</summary>
    public static int HpLossBudget { get; private set; }

    public static bool Collapsed { get; private set; }

    /// <summary>
    /// 优先级模式：
    ///   false（默认）= 保命优先：不死 → 掉血 ≤ 预算 → 优先击杀 → 最大伤害
    ///   true        = 输出优先：不死 → 优先击杀 → 最大伤害 → 掉血最少
    /// </summary>
    public static bool DamageFirst { get; private set; }

    private static readonly int[] BudgetCycle = { 0, 1, 2, 3, 5 };

    public static string ConfigPathPublic => ConfigPath;

    private static string ConfigPath
    {
        get
        {
            string dir = Entry.GameModsDirPublic();
            return System.IO.Path.Combine(dir, "DamageAdvisor.cfg");
        }
    }

    public static void Load()
    {
        try
        {
            string path = ConfigPath;
            if (!System.IO.File.Exists(path))
            {
                Save();
                return;
            }

            foreach (string line in System.IO.File.ReadAllLines(path))
            {
                string[] parts = line.Split('=', 2);
                if (parts.Length != 2)
                    continue;

                string key = parts[0].Trim();
                string value = parts[1].Trim();

                if (key.Equals("hpLossBudget", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out int budget))
                    HpLossBudget = Math.Clamp(budget, 0, 99);
                else if (key.Equals("collapsed", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out bool collapsed))
                    Collapsed = collapsed;
                else if (key.Equals("priority", StringComparison.OrdinalIgnoreCase))
                    DamageFirst = value.Equals("damage", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // 读不到就用默认值
        }
    }

    public static void Save()
    {
        try
        {
            string path = ConfigPath;
            string? dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                System.IO.Directory.CreateDirectory(dir);

            System.IO.File.WriteAllLines(path, new[]
            {
                "# 伤害顾问配置（游戏运行时也会定期重载）",
                "# hpLossBudget: 愿意承受的掉血上限，0 = 严格保命（无伤优先）",
                "# priority: survival = 优先保命 / damage = 优先打伤害（默认 survival）",
                $"hpLossBudget={HpLossBudget}",
                $"priority={(DamageFirst ? "damage" : "survival")}",
                $"collapsed={Collapsed}",
            });
        }
        catch
        {
            // 写不进去就算了
        }
    }

    /// <summary>F7：在 0/1/2/3/5 之间循环掉血上限。</summary>
    public static void CycleBudget()
    {
        int index = Array.IndexOf(BudgetCycle, HpLossBudget);
        HpLossBudget = BudgetCycle[(index + 1) % BudgetCycle.Length];
        Save();
    }

    /// <summary>F6：优先保命 &lt;-&gt; 优先打伤害。</summary>
    public static void TogglePriority()
    {
        DamageFirst = !DamageFirst;
        Save();
    }

    public static void ToggleCollapsed()
    {
        Collapsed = !Collapsed;
        Save();
    }
}
