using AutoJobQuest.Core;
using FFXIVClientStructs.FFXIV.Client.Game;
using ConditionFlag = Dalamud.Game.ClientState.Conditions.ConditionFlag;
using PlayerState = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState;

namespace AutoJobQuest.Automation;

/// <summary>釣りの餌まわりのゲームの読み書き（検証の仕組みでは偽物に差し替える）。</summary>
public interface IBaitGame
{
    /// <summary>今付けている餌（無ければ 0）。</summary>
    uint Equipped { get; }

    /// <summary>餌を付け替えられない状態か（糸を垂らしている・詠唱中・戦闘中・エリア移動中・会話中）。</summary>
    bool Busy { get; }

    /// <summary>その餌の所持数。</summary>
    int Owned(uint itemId);

    /// <summary>餌を付ける命令を送る（付いたかは <see cref="Equipped"/> で確かめる）。</summary>
    void Equip(uint itemId);
}

/// <summary>ゲームの餌（本番）。</summary>
public sealed unsafe class GameBait : IBaitGame
{
    public static readonly GameBait Instance = new();

    /// <summary>検証の仕組み用：設定すると、ゲームの代わりにこれを使う。本番では null のまま。</summary>
    public static IBaitGame? TestBait { get; set; }

    /// <summary>いま使う餌の読み書き（本番はゲーム）。</summary>
    public static IBaitGame Current => TestBait ?? Instance;

    /// <summary>
    /// 餌を付ける命令（GBR の CurrentBait.ChangeBait・AutoHook の FishingManager.ChangeBait と同じ：ExecuteCommand(701, 4, 餌の品番)）。
    /// GBR は釣りの構えに入る前（テレポで着いて着替えた直後）にもこの命令で付けている。
    /// </summary>
    private const int BaitCommand = 701;
    private const int BaitCommandSetBait = 4;

    /// <summary>今付けている餌（GBR・AutoHook と同じく PlayerState.FishingBait）。</summary>
    public uint Equipped
    {
        get
        {
            var ps = PlayerState.Instance();
            return ps == null ? 0 : ps->FishingBait;
        }
    }

    public bool Busy
    {
        get
        {
            // 糸を垂らしている間は Fishing が立つ（投げ直しの合間は Gathering だけ：実機の記録）。
            // 飛んでいる間も送らない（断られると「飛行中のため、その操作はできません」が出て、QuestTask が着地の合図と取り違えるため）
            var c = Svc.Condition;
            return c[ConditionFlag.Fishing] || c[ConditionFlag.Casting] || c[ConditionFlag.InCombat] || c[ConditionFlag.InFlight]
                   || c[ConditionFlag.BetweenAreas] || c[ConditionFlag.BetweenAreas51]
                   || c[ConditionFlag.OccupiedInEvent] || c[ConditionFlag.OccupiedInQuestEvent] || c[ConditionFlag.OccupiedInCutSceneEvent];
        }
    }

    public int Owned(uint itemId)
    {
        var im = InventoryManager.Instance();
        return im == null ? 0 : im->GetInventoryItemCount(itemId, false, false, false);
    }

    public void Equip(uint itemId) => GameMain.ExecuteCommand(BaitCommand, BaitCommandSetBait, (int)itemId, 0, 0);
}
