using System;
using System.Collections.Generic;
using AutoJobQuest.Core;
using AutoJobQuest.Data;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoJobQuest.Automation;

/// <summary>
/// クエストの納品窓（Request）に、求められている条件に合う品を自動で入れて渡す
/// （HQ だろうが何だろうが自動で選んで納品する。確認は出さない）。
///
/// 操作は YesAlready の Features/Request.cs と同じ：
///   AgentNpcTrade.SelectTurnInSlot(欄) → ReceiveEvent([0, 候補の番号], 4, 1) で入れる → 最後に ReceiveEvent([0], 4, 0) で渡す。
/// 違いは候補の選び方だけ。YesAlready は常に 0 番目を入れるが、こちらは窓が求める条件
/// （UIState.NpcTrade.Requests の WantHQ・WantMateriaFilledSlots・WantCollectible/MinCollectibility）を、
/// 候補のカバンの品（AgentNpcTrade.SelectedTurnInSlotItemOptionValues）と照らして、合うものを選ぶ。
/// HQ 指定が無ければ NQ を先に使う（HQ を残すため）。
///
/// 窓1つにつき1回だけ試す（呼び出し側が窓のアドレスで覚える）。合う品が無いときは入れずに記録する。
/// </summary>
public static unsafe class RequestFill
{
    /// <summary>納品窓が開いていれば、条件に合う品を入れて渡す。試したら true（成否は detail に書く）。</summary>
    public static bool TryFill(out string detail)
    {
        detail = string.Empty;
        var ui = UIState.Instance();
        var agent = AgentNpcTrade.Instance();
        if (ui == null || agent == null || !agent->IsAgentActive())
            return false;

        var trade = &ui->NpcTrade;
        var count = trade->Requests.Count;
        if (count == 0)
            return false;

        // 他のプラグインか利用者が選んでいる途中なら触らない
        if (agent->SelectedTurnInSlot >= 0)
        {
            detail = "納品窓で別の操作が選択中だったので触りませんでした";
            return true;
        }

        var res = new AtkValue();
        var param = stackalloc AtkValue[4];
        for (var i = 0; i < 4; i++)
            param[i] = default;

        var picked = new List<string>();
        for (var slot = 0; slot < count && slot < trade->Requests.Items.Length; slot++)
        {
            var req = trade->Requests.Items[slot];
            agent->SelectTurnInSlot((ushort)slot);
            if (agent->SelectedTurnInSlot != slot || agent->SelectedTurnInSlotItemOptions <= 0)
            {
                detail = $"納品窓の {slot + 1} 番目（{CraftPlanner.ItemName(req.ItemId)}）に入れられる品を持っていません";
                return true;
            }

            var option = Choose(req, agent, out var why);
            if (option < 0)
            {
                detail = $"納品窓の {slot + 1} 番目（{CraftPlanner.ItemName(req.ItemId)}{(req.WantHQ ? " HQ" : string.Empty)}）の候補に、条件に合う品がありません（{why}）";
                return true;
            }

            param[0].SetInt(0);      // 入れる
            param[1].SetInt(option); // 候補の番号
            agent->ReceiveEvent(&res, param, 4, 1);
            picked.Add($"{CraftPlanner.ItemName(req.ItemId)}：{why}");
        }

        // 渡す
        param[0].SetInt(0);
        var addonId = agent->AddonId;
        agent->ReceiveEvent(&res, param, 4, 0);
        var addon = RaptureAtkUnitManager.Instance()->GetAddonById((ushort)addonId);
        if (addon != null && addon->IsVisible)
            addon->Close(false);

        detail = $"納品窓に入れて渡しました：{string.Join(" / ", picked)}";
        return true;
    }

    /// <summary>候補の中から条件に合う品の番号を選ぶ。無ければ -1。why に選んだ理由（無ければ候補の中身）。</summary>
    private static int Choose(NpcTrade.Item req, AgentNpcTrade* agent, out string why)
    {
        var fallback = -1;
        var seen = new List<string>();
        var n = Math.Min((int)agent->SelectedTurnInSlotItemOptions, agent->SelectedTurnInSlotItemOptionValues.Length);
        for (var j = 0; j < n; j++)
        {
            var it = agent->SelectedTurnInSlotItemOptionValues[j].Value;
            if (it == null || it->ItemId == 0)
                continue;

            var hq = (it->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
            var materia = it->GetMateriaCount();
            var coll = it->IsCollectable() ? it->GetCollectability() : (ushort)0;
            seen.Add($"{(hq ? "HQ" : "NQ")}・マテリア{materia}{(it->IsCollectable() ? $"・収集価値{coll}" : string.Empty)}");

            var ok = it->GetBaseItemId() == req.ItemId
                     && (!req.WantHQ || hq)
                     && materia >= req.WantMateriaFilledSlots
                     && (!req.WantCollectible || (it->IsCollectable() && coll >= req.MinCollectibility));
            if (!ok)
                continue;

            // HQ 指定が無いときは NQ を先に使う（HQ は後の HQ 指定のために残す）
            if (!req.WantHQ && hq)
            {
                if (fallback < 0)
                    fallback = j;
                continue;
            }

            why = $"{(hq ? "HQ" : "NQ")}の品（候補 {j + 1}/{n}）";
            return j;
        }

        if (fallback >= 0)
        {
            why = $"HQの品（NQ が無いため。候補 {fallback + 1}/{n}）";
            return fallback;
        }

        why = seen.Count == 0 ? "候補を読めませんでした" : "候補：" + string.Join("、", seen);
        return -1;
    }
}
