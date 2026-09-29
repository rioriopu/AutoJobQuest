using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace AutoJobQuest;

/// <summary>Dalamud のサービスをまとめて受け取る場所。</summary>
public sealed class Svc
{
    [PluginService] public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] public static ICommandManager Commands { get; private set; } = null!;
    [PluginService] public static IPluginLog Log { get; private set; } = null!;
    [PluginService] public static IClientState ClientState { get; private set; } = null!;
    [PluginService] public static IFramework Framework { get; private set; } = null!;
    [PluginService] public static ICondition Condition { get; private set; } = null!;
    [PluginService] public static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] public static IChatGui Chat { get; private set; } = null!;
    [PluginService] public static IDataManager Data { get; private set; } = null!;
    [PluginService] public static IObjectTable Objects { get; private set; } = null!;
    [PluginService] public static ITargetManager Targets { get; private set; } = null!;
    [PluginService] public static IGameGui GameGui { get; private set; } = null!;
    [PluginService] public static IAetheryteList Aetherytes { get; private set; } = null!;
    [PluginService] public static IGameInteropProvider Hook { get; private set; } = null!;
    [PluginService] public static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] public static IMarketBoard MarketBoard { get; private set; } = null!;
}

/// <summary>自分の位置など、よく使う小物。</summary>
public static class Me
{
    /// <summary>自分のキャラクター。取れなければ null。</summary>
    public static Dalamud.Game.ClientState.Objects.Types.IGameObject? Object
        => Svc.Objects.LocalPlayer;

    /// <summary>自分の座標。取れなければ原点を返す。</summary>
    public static System.Numerics.Vector3 Position
        => Svc.Objects.LocalPlayer?.Position ?? System.Numerics.Vector3.Zero;

    /// <summary>ログインして操作できる状態か。</summary>
    public static bool Available
        => Svc.ClientState.IsLoggedIn && Svc.Objects.LocalPlayer != null;

    /// <summary>いまいるエリア（TerritoryType の行 ID）。</summary>
    public static uint Territory => Svc.ClientState.TerritoryType;
}
