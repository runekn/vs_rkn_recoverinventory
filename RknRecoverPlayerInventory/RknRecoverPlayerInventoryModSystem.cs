using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.Common;
using Vintagestory.Server;

namespace RknRecoverPlayerInventory;

public class RknRecoverPlayerInventoryModSystem : ModSystem
{
    private static readonly string[] TargetInventoryClassNames =
    [
      "hotbar",
      "backpack",
      "craftinggrid",
      "character",
      "mouse"
    ];
    private ICoreServerAPI? _sapi;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;
    
    public override void StartServerSide(ICoreServerAPI api)
    {
      _sapi = api;
      api.ChatCommands.Create("recoverinv")
        .WithDescription("Recover offline player inventory.")
        .RequiresPrivilege(Privilege.controlserver)
        .RequiresPlayer()
        .WithArgs([
          new PlayersArgParser("player", api, true),
          new BoolArgParser("copy", "copy", false)
        ])
        .HandleWith((args) =>
        {
          StringBuilder report = new();
          IPlayer byPlayer = args.Caller.Player;
          PlayerUidName[] players = (PlayerUidName[]) args[0];
          bool copy = (bool) args[1];
          Vec3d position = byPlayer.CurrentBlockSelection.FullPosition;
          foreach (PlayerUidName player in players)
          {
            RecoverSinglePlayer(player, copy, report, position);
          }
          return TextCommandResult.Success(report.ToString());
        });
    }

    private void RecoverSinglePlayer(PlayerUidName player, bool copy, StringBuilder report, Vec3d position)
    {
      try
      {
        report.Append("Recovering player ").Append(player.Name).Append(" (").Append(player.Uid).AppendLine(")");
        if (_sapi is not { World: ServerMain world })
        {
          report.AppendLine("ERROR: This is not a server");
          return; 
        }
        if (IsPlayerOnline(player))
        {
          report.AppendLine("WARNING: Cannot recover inventory from online player");
          return;
        }
        RecoveryContext context = CreateContext(_sapi, world);
        if (context == null)
          return;
        PendingRecovery pending = world.PlayerDataManager.WorldDataByUID.ContainsKey(player.Uid) ? LoadInMemoryRecovery(world, context, player) : LoadOfflineRecovery(world, context, player);
        if (pending == null || pending.Stacks.Count == 0)
        {
          report.AppendLine("WARNING: Nothing to recover");
          return;
        }
        foreach (ItemStack stack in pending.Stacks)
        {
          SpawnStack(stack, position);
        }
        if (!copy)
        {
          DeleteFromInventory(context, report, pending);
        }
        AppendToReport(report, pending);
      }
      catch (Exception ex)
      {
        report.Append("ERROR: Failed to recover inventory of unwhitelisted player ").AppendLine(player.Name);
        Mod.Logger.Error("Failed to recover inventory of unwhitelisted player {0}:", player.Name);
        Mod.Logger.Error(ex);
      }
    }

    private bool IsPlayerOnline(PlayerUidName player)
    {
      return _sapi.World.AllOnlinePlayers.Any(p => p.PlayerUID == player.Uid);
    }

    private void DeleteFromInventory(RecoveryContext context, StringBuilder report, PendingRecovery pending)
    {
      foreach (InventoryBase inventory in pending.Inventories)
        inventory.Clear();
      pending.WorldData.BeforeSerialization();
      if (pending.EntityBytes != null)
        context.EntityField.SetValue(pending.WorldData, pending.EntityBytes);
      context.GameDatabase.SetPlayerData(pending.Player.Uid, SerializerUtil.Serialize(pending.WorldData));
    }

    private void SpawnStack(ItemStack stack, Vec3d position)
    {
      _sapi.World.SpawnItemEntity(stack, position);
    }

    private RecoveryContext? CreateContext(ICoreServerAPI api, ServerMain server)
    {
      GameDatabase gameDatabase = GetGameDatabase(server);
      FieldInfo field1 = typeof (ServerWorldPlayerData).GetField("EntityPlayerSerialized", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
      FieldInfo field2 = typeof (ServerWorldPlayerData).GetField("inventoriesSerialized", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
      FieldInfo field3 = typeof (ServerWorldPlayerData).GetField("inventories", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
      if (field1 == null || field2 == null || field3 == null)
      {
        Mod.Logger.Warning("Could not access player data internals, skipping recovery");
        return null;
      }
      return new RecoveryContext(gameDatabase, field1, field2, field3);
    }

    private static void AppendToReport(
      StringBuilder report,
      PendingRecovery recovery)
    {
      foreach (ItemStack stack in recovery.Stacks)
      {
        string str1 = stack.Attributes?.GetString("itemizerName");
        string str2;
        if (str1 != null)
          str2 = $"  {stack.StackSize}x {stack.Collectible.Code} \"{str1}\"";
        else
          str2 = $"  {stack.StackSize}x {stack.Collectible.Code}";
        report.AppendLine(str2);
      }
    }

    private PendingRecovery? LoadOfflineRecovery(
      ServerMain server,
      RecoveryContext context,
      PlayerUidName player)
    {
      byte[] playerData = context.GameDatabase.GetPlayerData(player.Uid);
      if (playerData != null)
      {
        if (playerData.Length != 0)
        {
          ServerWorldPlayerData worldData;
          try
          {
            worldData = SerializerUtil.Deserialize<ServerWorldPlayerData>(playerData);
          }
          catch (Exception ex)
          {
            Mod.Logger.Warning("Could not deserialize player data of {0}, skipping: {1}", player.Name, ex.Message);
            return null;
          }
          if (worldData == null)
            return null;
          byte[] entityBytes = context.EntityField.GetValue(worldData) as byte[];
          int count = context.SerializedInventoriesField.GetValue(worldData) is Dictionary<string, byte[]> dictionary ? dictionary.Count : 0;
          if (entityBytes == null || entityBytes.Length == 0)
            return null;
          worldData.Init(server);
          if (worldData.EntityPlayer?.Pos == null)
          {
            Mod.Logger.Warning("Player entity of {0} failed to load, skipping", player.Name);
            return null;
          }
          if (!(context.InventoriesField.GetValue(worldData) is IEnumerable<KeyValuePair<string, InventoryBase>> inventoriesEnumerable))
            return null;
          List<KeyValuePair<string, InventoryBase>> inventories = inventoriesEnumerable.ToList();
          if (inventories.Count == count)
            return CreatePendingRecovery(server, worldData, player, entityBytes, inventories);
          Mod.Logger.Warning("Some inventories of {0} failed to load, skipping", player.Name);
        }
      }
      return null;
    }

    private static PendingRecovery? LoadInMemoryRecovery(
      ServerMain server,
      RecoveryContext context,
      PlayerUidName player)
    {
      ServerWorldPlayerData worldData;
      if (!server.PlayerDataManager.WorldDataByUID.TryGetValue(player.Uid, out worldData) || worldData == null)
        return null;
      if (worldData.EntityPlayer?.Pos == null)
        return null;
      return !(context.InventoriesField.GetValue(worldData) is IEnumerable<KeyValuePair<string, InventoryBase>> source) ? null : CreatePendingRecovery(server, worldData, player, null, source.ToList());
    }

    private static PendingRecovery? CreatePendingRecovery(
      ServerMain server,
      ServerWorldPlayerData worldData,
      PlayerUidName player,
      byte[]? entityBytes,
      List<KeyValuePair<string, InventoryBase>> loaded)
    {
      List<InventoryBase> list = loaded
        .Where((System.Func<KeyValuePair<string, InventoryBase>, bool>) (kv => TargetInventoryClassNames.Contains(kv.Key.Split('-')[0])))
        .Select<KeyValuePair<string, InventoryBase>, InventoryBase>(kv => kv.Value)
        .ToList();
      List<ItemStack> stacks = ExtractStacks(list, server);
      if (stacks.Count == 0)
        return null;
      return new PendingRecovery(worldData, player, entityBytes, list, stacks, stacks.Sum(stack => stack.StackSize));
    }

    private static List<ItemStack> ExtractStacks(
      List<InventoryBase> inventories,
      IWorldAccessor world)
    {
      List<ItemStack> stacks = [];
      foreach (InventoryBase inventory in inventories)
      {
        for (int slotId = 0; slotId < inventory.Count; ++slotId)
        {
          ItemSlot itemSlot = inventory[slotId];
          if (!(itemSlot is ItemSlotBagContent))
            CollectStack(itemSlot?.Itemstack, world, stacks);
        }
      }
      return stacks;
    }

    private static void CollectStack(ItemStack? stack, IWorldAccessor world, List<ItemStack> stacks)
    {
      if (stack == null)
        return;
      stack.ResolveBlockOrItem(world);
      if (stack.Collectible?.Code == null)
        return;
      string code = stack.Collectible.Code.ToString();
      switch (code)
      {
        case "game:item":
        case "game:block":
          break;
        default:
          stacks.Add(stack);
          ItemStack[] contents = stack.Collectible.GetCollectibleInterface<IHeldBag>()?.GetContents(stack, world);
          if (contents == null)
            break;
          foreach (ItemStack stack1 in contents)
            CollectStack(stack1, world, stacks);
          break;
      }
    }

    private static GameDatabase? GetGameDatabase(ServerMain server)
    {
      object? obj = typeof (ServerMain).GetField("chunkThread", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(server);
      return obj?.GetType().GetField("gameDatabase", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(obj) as GameDatabase;
    }

    private sealed record RecoveryContext(
      GameDatabase GameDatabase,
      FieldInfo EntityField,
      FieldInfo SerializedInventoriesField,
      FieldInfo InventoriesField)
    ;

    private sealed record PendingRecovery(
      ServerWorldPlayerData WorldData,
      PlayerUidName Player,
      byte[]? EntityBytes,
      List<InventoryBase> Inventories,
      List<ItemStack> Stacks,
      int ItemCount)
    ;
}
