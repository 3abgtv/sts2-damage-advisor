using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;

namespace DamageAdvisor;

/// <summary>
/// Mod 入口。游戏通过 [ModInitializer("Initialize")] 调用这里。
///
/// 挂载策略（踩过的坑）：
///   1. 把 UI 挂到 SceneTree.Root 上不行——延迟调用会落空，节点进不了树，_Ready 永远不执行。
///   2. 游戏自己的根节点是 NGame.Instance，CombatSolver 等 Mod 也是挂它。
///   3. Mod 初始化时 NGame 可能还没创建，所以再挂钩 NGame._Ready 兜底，并在战斗开始时补一次。
/// </summary>
[ModInitializer("Initialize")]
public static class Entry
{
    public const string Tag = "[DamageAdvisor]";
    public const string Version = "0.7.0";

    private static AdvisorRoot? _node;

    public static void Initialize()
    {
        try
        {
            Log($"初始化开始 v{Version}");
            InstallGameReadyHook();

            // 兜底：真正开打时再挂一次，确保一定能挂上
            if (CombatManager.Instance is { } manager)
                manager.CombatBegan += OnCombatBegan;
            else
                Log("CombatManager.Instance 为空，跳过战斗事件挂钩");
            Attach();
        }
        catch (Exception ex)
        {
            Log("初始化异常：" + ex);
        }
    }

    private static bool _poolDumped;

    /// <summary>第一场战斗时再转储（初始化时 ModelDb 还没建好）。</summary>
    public static void DumpSilentPoolOnce()
    {
        if (_poolDumped)
            return;
        _poolDumped = true;
        DumpSilentPool();
    }

    /// <summary>一次性把猎人卡池的机制变量打到日志，用来做覆盖清单。</summary>
    private static void DumpSilentPool()
    {
        try
        {
            CardPoolModel? pool = null;
            try
            {
                pool = ModelDb.CardPool<SilentCardPool>();
            }
            catch
            {
                // ModelDb 分类索引可能还没建好，退回遍历所有卡池
                pool = ModelDb.AllCardPools.FirstOrDefault(p => p.Id.ToString().Contains("SILENT", StringComparison.OrdinalIgnoreCase));
            }
            if (pool is null)
            {
                Log("找不到猎人卡池");
                return;
            }
            int count = 0;
            foreach (CardModel card in pool.AllCards)
            {
                count++;
                var vars = new List<string>();
                string desc = "";
                try
                {
                    desc = card.Description.GetFormattedText().Replace("\n", " ").Replace("\"", "'");
                }
                catch
                {
                    // 描述取不到就留空
                }
                string keywords = "";
                try
                {
                    keywords = string.Join("|", card.Keywords.Select(k => k.ToString()));
                }
                catch
                {
                    // 忽略
                }
                foreach (KeyValuePair<string, DynamicVar> pair in card.DynamicVars)
                    vars.Add($"{pair.Key}={pair.Value.BaseValue:0.#}");
                Log($"卡池 {card.GetType().Name} id={card.Id} name={SafeCardName(card)} type={card.Type} cost={card.EnergyCost?.Canonical} target={card.TargetType} kw=[{keywords}] desc=[{desc}] vars=[{string.Join(",", vars)}]");
            }
            Log($"猎人卡池转储完成，共 {count} 张");
            try
            {
                var tokenPool = ModelDb.AllCardPools.FirstOrDefault(p => p.Id.ToString().Contains("TOKEN", StringComparison.OrdinalIgnoreCase));
                if (tokenPool is not null)
                {
                    foreach (CardModel token in tokenPool.AllCards)
                    {
                        var tvars = new List<string>();
                        foreach (KeyValuePair<string, DynamicVar> pair in token.DynamicVars)
                            tvars.Add($"{pair.Key}={pair.Value.BaseValue:0.#}");
                        string tdesc = "";
                        try { tdesc = token.Description.GetFormattedText().Replace("\n", " "); } catch { }
                        Log($"Token {token.GetType().Name} name={SafeCardName(token)} cost={token.EnergyCost?.Canonical} desc=[{tdesc}] vars=[{string.Join(",", tvars)}]");
                    }
                }
            }
            catch (Exception tex)
            {
                Log("Token 池转储失败：" + tex.Message);
            }
        }
        catch (Exception ex)
        {
            Log("卡池转储失败：" + ex.Message);
        }
    }

    private static string SafeCardName(CardModel card)
    {
        try
        {
            string table = card.Description.LocTable;
            string key = card.Description.LocEntryKey;
            string baseKey = key.EndsWith(".description", StringComparison.Ordinal) ? key[..^".description".Length] : key;
            foreach (string candidate in new[] { baseKey + ".title", baseKey })
            {
                LocString? loc = LocString.Exists(table, candidate) ? LocString.GetIfExists(table, candidate) : null;
                if (loc is not null)
                {
                    string text = loc.GetFormattedText();
                    if (!string.IsNullOrWhiteSpace(text))
                        return text.Trim();
                }
            }
            return card.Id.ToString() ?? "?";
        }
        catch
        {
            return "?";
        }
    }

    private static void InstallGameReadyHook()
    {
        try
        {
            MethodInfo? ready = AccessTools.Method(typeof(NGame), "_Ready");
            if (ready is null)
            {
                Log("找不到 NGame._Ready，跳过挂钩");
                return;
            }

            var harmony = new Harmony("DamageAdvisor.attach");
            harmony.Patch(ready, postfix: new HarmonyMethod(typeof(Entry), nameof(OnGameReady)));
            Log("已挂钩 NGame._Ready");
        }
        catch (Exception ex)
        {
            Log("挂钩 NGame._Ready 失败：" + ex.Message);
        }
    }

    private static void OnGameReady() => Attach();

    private static void OnCombatBegan(CombatState state) => Attach();

    /// <summary>把覆盖层挂到游戏根节点上，幂等。</summary>
    private static void Attach()
    {
        try
        {
            if (_node is not null && GodotObject.IsInstanceValid(_node))
                return;

            NGame? host = NGame.Instance;
            if (host is null)
            {
                Log("NGame.Instance 尚未就绪，等待 _Ready 或战斗开始");
                return;
            }
            _node = new AdvisorRoot { Name = "DamageAdvisorRoot" };
            host.AddChild(_node);
            Log($"覆盖层已挂载到 NGame，子节点数={host.GetChildCount()}，在树内={_node.IsInsideTree()}");

            // 不依赖 Godot 虚函数派发：显式建 UI，并用 process_frame 信号驱动刷新
            _node.EnsureStarted();
            if (host.GetTree() is { } tree)
            {
                tree.ProcessFrame += _node.TickFromSignal;
                Log("已连接 SceneTree.ProcessFrame 信号");
            }
            else
            {
                Log("拿不到 SceneTree，刷新可能不工作");
            }
        }
        catch (Exception ex)
        {
            Log("挂载失败：" + ex);
        }
    }

    /// <summary>同时输出到控制台和日志文件，方便排查。</summary>
    public static void Log(string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss} {Tag} {message}";
        GD.Print(line);

        // 主日志写在游戏目录（方便查看），用户目录再放一份兜底
        TryAppend(System.IO.Path.Combine(GameModsDir(), "DamageAdvisor.log"), line);
        TryAppend(System.IO.Path.Combine(OS.GetUserDataDir(), "DamageAdvisor.log"), line);
    }

    public static string GameModsDirPublic() => GameModsDir();

    private static string GameModsDir()
    {
        try
        {
            string exe = OS.GetExecutablePath();
            string? dir = System.IO.Path.GetDirectoryName(exe);
            if (!string.IsNullOrEmpty(dir))
                return System.IO.Path.Combine(dir, "mods", "DamageAdvisor");
        }
        catch
        {
            // 忽略
        }
        return System.IO.Path.GetTempPath();
    }

    private static void TryAppend(string path, string line)
    {
        try
        {
            string? dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(path, line + System.Environment.NewLine);
        }
        catch
        {
            // 日志失败不影响游戏
        }
    }
}







