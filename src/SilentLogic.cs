namespace DamageAdvisor;

/// <summary>某张牌的 Cards 变量到底代表什么（游戏里这个变量被重载了）。</summary>
internal enum CardsVarMeaning
{
    None,
    /// <summary>抽牌（杂技、后空翻、独门技术…）。</summary>
    Draw,
    /// <summary>生成小刀（刀刃之舞、斗篷与匕首、袖里乾坤…）。</summary>
    GenerateShivs,
}

/// <summary>
/// 猎人卡牌的逐张语义表。
/// 数据来源：游戏内 ModelDb 卡池转储 + 卡牌描述文本（见 docs/silent-pool.txt），
/// 以及 CombatSolver 的 mirror 实现交叉验证。
/// </summary>
internal static class SilentLogic
{
    /// <summary>Cards 变量含义。</summary>
    public static CardsVarMeaning CardsMeaning(string className) => className switch
    {
        "BladeDance" or "CloakAndDagger" or "UpMySleeve" or "BladeOfInk" => CardsVarMeaning.GenerateShivs,
        "Acrobatics" or "Adrenaline" or "Backflip" or "Expertise" or "ShadowStep" or "Prepared" or "Reflex"
            => CardsVarMeaning.Draw,
        _ => CardsVarMeaning.None,
    };

    /// <summary>固定弃牌数（描述里写死 1 张的）。</summary>
    public static int FixedDiscard(string className) => className switch
    {
        "Acrobatics" or "Survivor" or "DaggerThrow" => 1,
        _ => 0,
    };

    /// <summary>Cards 变量代表的弃牌数（早有准备、隐秘匕首）。</summary>
    public static bool DiscardsCardsVar(string className)
        => className is "Prepared" or "HiddenDaggers";

    /// <summary>小刀生成数来源为 Shivs 变量的牌。</summary>
    public static bool ShivsFromShivsVar(string className)
        => className is "FanOfKnives" or "LeadingStrike" or "HiddenDaggers";

    /// <summary>立即获得敏捷（影响本回合后续格挡）。</summary>
    public static bool GrantsDexterity(string className)
        => className is "Footwork" or "Anticipate" or "Abrasive" or "Fade";

    /// <summary>被丢弃时触发的牌。</summary>
    public static bool TriggersOnDiscard(string className)
        => className is "Tactician" or "Reflex";

    /// <summary>伤害随"本回合已打出的攻击牌数"增长的牌。</summary>
    public static bool ScalesWithAttacksPlayed(string className) => className is "Finisher";

    /// <summary>伤害随"手牌中技能牌数"增长的牌。</summary>
    public static bool ScalesWithSkillsInHand(string className) => className is "Flechettes";

    /// <summary>格挡等于所有敌人中毒层数之和。</summary>
    public static bool BlockFromEnemyPoison(string className) => className is "Mirage";

    /// <summary>只在抽牌堆为空时可打出。</summary>
    public static bool RequiresEmptyDrawPile(string className) => className is "GrandFinale";

    /// <summary>X 费牌（X = 剩余能量）。</summary>
    public static bool IsXCost(string className) => className is "Skewer" or "Malaise";

    /// <summary>打出后本回合手牌全部免费（子弹时间）。</summary>
    public static bool MakesHandFree(string className) => className is "BulletTime";

    /// <summary>弃掉整手牌、每张换一把小刀（钢铁风暴）。</summary>
    public static bool DiscardsHandForShivs(string className) => className is "StormOfSteel";

    /// <summary>弃掉整手牌、抽等量牌（计算下注）。</summary>
    public static bool DiscardsHandForDraw(string className) => className is "CalculatedGamble";

    /// <summary>本回合获得精准（小刀 +N 伤害）。</summary>
    public static bool GrantsShivBonus(string className) => className is "Accuracy";

    /// <summary>余像：每打出一张牌获得格挡。</summary>
    public static bool GrantsBlockPerCard(string className) => className is "Afterimage";

    /// <summary>暴露：施加易伤（变量名是 Power）。</summary>
    public static bool VulnerableFromPowerVar(string className) => className is "Expose";

    /// <summary>涂毒：攻击造成伤害时附加中毒。</summary>
    public static bool GrantsEnvenom(string className) => className is "Envenom";

    /// <summary>腐蚀波：本回合每抽一张牌给全体敌人上毒。</summary>
    public static bool GrantsPoisonPerDraw(string className) => className is "CorrosiveWave";

    /// <summary>群蛇形态：每打出一张牌随机造成伤害。</summary>
    public static bool GrantsDamagePerCardPlayed(string className) => className is "SerpentForm";

    /// <summary>速行者：每抽一张牌造成伤害。</summary>
    public static bool GrantsDamagePerDraw(string className) => className is "Speedster";

    /// <summary>融入暗影：本回合格挡翻倍。</summary>
    public static bool DoublesBlock(string className) => className is "Shadowmeld";

    /// <summary>幻影之刃：本回合第一张小刀额外伤害。</summary>
    public static bool GrantsFirstShivBonus(string className) => className is "PhantomBlades";

    /// <summary>能力牌在本回合的即时收益提示（用于面板说明）。</summary>
    public static string ImmediatePowerNote(string className) => className switch
    {
        "Accuracy" => "能力牌（本回合后续小刀 +4）",
        "Envenom" => "能力牌（本回合后续攻击附毒）",
        "Afterimage" => "能力牌（本回合每张牌 +1 格挡）",
        "CorrosiveWave" => "本回合后续每抽 1 张牌 → 全体敌人 +2 中毒",
        "SerpentForm" => "能力牌（本回合每张牌随机 4 伤）",
        "Speedster" => "能力牌（本回合每抽 1 张牌 → 全体 2 伤）",
        "Shadowmeld" => "本回合格挡翻倍",
        "PhantomBlades" => "本回合第一张小刀 +9",
        _ => "",
    };

    /// <summary>本回合完全没有建模的牌（会在面板里标出来，不参与推荐）。</summary>
    public static string UnsupportedReason(string className) => className switch
    {
        "KnifeTrap" => "刀刃陷阱：需要消耗堆里的小刀，未建模",
        "Burst" => "爆发：技能双触发，未建模",
        "Nightmare" => "夜魇：复制手牌，未建模",
        "Concoct" => "调制：给队友加毒，未建模",
        "Flanking" => "夹击：多人向效果，未建模",
        "Sneaky" => "鬼祟：能力牌，本回合无收益",
        "Tracking" => "跟踪：能力牌，本回合无收益",
        _ => "",
    };
}



