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
        .WithDescription("Tools for recovering an offline player's inventory.")
        .RequiresPrivilege(Privilege.controlserver)
        .RequiresPlayer()
        .BeginSubCommand("list")
        .WithDescription("List a player's inventory.")
        .WithArgs([
          new PlayersArgParser("player", api, true),
        ])
        .HandleWith((args) => HandleCommand(EOperation.List, args))
        .EndSubCommand()
        .BeginSubCommand("move")
        .WithDescription("Move a player's inventory to cursor position.")
        .WithArgs([
          new PlayersArgParser("player", api, true),
          new StringArgParser("filter", false)
        ])
        .HandleWith((args) => HandleCommand(EOperation.Move, args))
        .EndSubCommand()
        .BeginSubCommand("copy")
        .WithDescription("Copy a player's inventory to cursor position.")
        .WithArgs([
          new PlayersArgParser("player", api, true),
          new StringArgParser("filter", false)
        ])
        .HandleWith((args) => HandleCommand(EOperation.Copy, args))
        .EndSubCommand();
    }

    private TextCommandResult HandleCommand(EOperation op, TextCommandCallingArgs args)
    {
      StringBuilder report = new();
      IPlayer byPlayer = args.Caller.Player;
      PlayerUidName[] players = (PlayerUidName[])args[0];
      AssetLocation[]? filter = null;
      if (op != EOperation.List)
      {
        filter = ((string)args[1])?
          .Split(',')
          .Select(s => new AssetLocation(s))
          .ToArray(); 
      }
      Vec3d position = byPlayer.CurrentBlockSelection.FullPosition;
      
      RecoveryContext? context = CreateContext(_sapi);
      if (context == null)
      {
        return TextCommandResult.Error("Could not init context"); 
      }
      
      foreach (PlayerUidName player in players)
      {
        RecoverInventory(player, op, filter, report, position, context);
      }
      
      return TextCommandResult.Success(report.ToString());
    }

    private void RecoverInventory(PlayerUidName player, EOperation op, AssetLocation[]? filter, StringBuilder report,
      Vec3d position, RecoveryContext context)
    {
      try
      {
        report.Append("Recovering player ").Append(player.Name).Append(" (").Append(player.Uid).AppendLine(")");
        if (IsPlayerOnline(player))
        {
          report.AppendLine("WARNING: Cannot recover inventory from online player");
          return;
        }
        PendingRecovery? pending = context.World.PlayerDataManager.WorldDataByUID.ContainsKey(player.Uid) ? LoadInMemoryRecovery(context, player, filter) : LoadOfflineRecovery(context, player, filter);
        if (pending == null || pending.Stacks.Count == 0)
        {
          report.AppendLine("WARNING: Nothing to recover");
          return;
        }

        if (op != EOperation.List)
        {
          foreach (ItemStack stack in pending.Stacks)
          {
              SpawnStack(stack, position);
          }
          if (op == EOperation.Move)
          {
            DeleteFromInventory(context, pending, filter);
          }
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

    private static bool SatisfiesFilter(ItemStack stack, AssetLocation[]? filter)
    {
      return filter == null || filter.Any(a => a.Equals(stack.Collectible.Code));
    }

    private bool IsPlayerOnline(PlayerUidName player)
    {
      return _sapi.World.AllOnlinePlayers.Any(p => p.PlayerUID == player.Uid);
    }

    private void DeleteFromInventory(RecoveryContext context, PendingRecovery pending, AssetLocation[]? filter)
    {
      if (filter == null)
      {
        foreach (InventoryBase inventory in pending.Inventories)
        {
          inventory.Clear(); 
        } 
      }
      else
      {
        WalkInventories(pending.Inventories, context.World, (stack, parent, i) =>
        {
          if (!SatisfiesFilter(stack, filter))
          {
            return;
          }
          if (parent is ItemSlot itemSlot)
          {
            itemSlot.Itemstack = null;
          }
          else if (parent is ItemStack parentStack)
          {
            IHeldBag bag = parentStack.Collectible.GetCollectibleInterface<IHeldBag>();
            bag?.Store(parentStack, new ItemSlotBagContent(new DummyInventory(_sapi), 0, i, bag.GetStorageFlags(parentStack)));
          }
        });
      }

      pending.WorldData.BeforeSerialization();
      if (pending.EntityBytes != null)
      {
        context.EntityField.SetValue(pending.WorldData, pending.EntityBytes); 
      }
      context.GameDatabase.SetPlayerData(pending.Player.Uid, SerializerUtil.Serialize(pending.WorldData));
    }

    private void SpawnStack(ItemStack stack, Vec3d position)
    {
      _sapi.World.SpawnItemEntity(stack.Clone(), position);
    }

    private RecoveryContext? CreateContext(ICoreServerAPI? api)
    {
      if (api is not { World: ServerMain world })
      {
        Mod.Logger.Warning("Not executed from server. Or maybe forgot to init server api?");
        return null;
      }
      GameDatabase? gameDatabase = GetGameDatabase(world);
      FieldInfo? entityField = typeof (ServerWorldPlayerData).GetField("EntityPlayerSerialized", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
      FieldInfo? serializedInventoriesField = typeof (ServerWorldPlayerData).GetField("inventoriesSerialized", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
      FieldInfo? inventoriesField = typeof (ServerWorldPlayerData).GetField("inventories", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
      if (entityField == null || serializedInventoriesField == null || inventoriesField == null)
      {
        Mod.Logger.Warning("Could not access player data internals, skipping recovery");
        return null;
      }
      return new RecoveryContext(gameDatabase, entityField, serializedInventoriesField, inventoriesField, world);
    }

    private static void AppendToReport(
      StringBuilder report,
      PendingRecovery recovery)
    {
      foreach (ItemStack stack in recovery.Stacks)
      {
        string? itemizerName = stack.Attributes?.GetString("itemizerName");
        string log;
        if (itemizerName != null)
          log = $"  {stack.StackSize}x {stack.Collectible.Code} \"{itemizerName}\"";
        else
          log = $"  {stack.StackSize}x {stack.Collectible.Code}";
        report.AppendLine(log);
      }
    }

    private PendingRecovery? LoadOfflineRecovery(
      RecoveryContext context,
      PlayerUidName player,
      AssetLocation[]? filter)
    {
      byte[] playerData = context.GameDatabase.GetPlayerData(player.Uid);
      if (playerData == null || playerData.Length == 0)
      {
        return null;
      }
      
      ServerWorldPlayerData worldData;
      try
      {
        worldData = SerializerUtil.Deserialize<ServerWorldPlayerData>(playerData);
        if (worldData == null)
        {
          return null; 
        }
      }
      catch (Exception ex)
      {
        Mod.Logger.Warning("Could not deserialize player data of {0}, skipping: {1}", player.Name, ex.Message);
        return null;
      }
      
      byte[]? entityBytes = context.EntityField.GetValue(worldData) as byte[];
      int count = context.SerializedInventoriesField.GetValue(worldData) is Dictionary<string, byte[]> dictionary ? dictionary.Count : 0;
      if (entityBytes == null || entityBytes.Length == 0)
      {
        return null; 
      }
      
      worldData.Init(context.World);
      if (worldData.EntityPlayer?.Pos == null)
      {
        Mod.Logger.Warning("Player entity of {0} failed to load, skipping", player.Name);
        return null;
      }

      if (context.InventoriesField.GetValue(worldData) is not IEnumerable<KeyValuePair<string, InventoryBase>> inventoriesEnumerable)
      {
        return null; 
      }
      List<KeyValuePair<string, InventoryBase>> inventories = inventoriesEnumerable.ToList();
      if (inventories.Count == count)
      {
        return CreatePendingRecovery(context.World, worldData, player, entityBytes, inventories, filter); 
      }
      Mod.Logger.Warning("Some inventories of {0} failed to load, skipping", player.Name);
      return null;
    }

    private static PendingRecovery? LoadInMemoryRecovery(RecoveryContext context, PlayerUidName player, AssetLocation[]? filter)
    {
      if (!context.World.PlayerDataManager.WorldDataByUID.TryGetValue(player.Uid, out ServerWorldPlayerData? worldData) || worldData == null)
      {
        return null; 
      }
      if (worldData.EntityPlayer?.Pos == null)
      {
        return null;
      }
      if (context.InventoriesField.GetValue(worldData) is not IEnumerable<KeyValuePair<string, InventoryBase>> source)
      {
        return null;
      }
      return CreatePendingRecovery(context.World, worldData, player, null, source.ToList(), filter);
    }

    private static PendingRecovery? CreatePendingRecovery(
      ServerMain server,
      ServerWorldPlayerData worldData,
      PlayerUidName player,
      byte[]? entityBytes,
      List<KeyValuePair<string, InventoryBase>> loaded,
      AssetLocation[]? filter)
    {
      List<InventoryBase> list = loaded
        .Where((System.Func<KeyValuePair<string, InventoryBase>, bool>) (kv => TargetInventoryClassNames.Contains(kv.Key.Split('-')[0])))
        .Select<KeyValuePair<string, InventoryBase>, InventoryBase>(kv => kv.Value)
        .ToList();
      List<ItemStack> stacks = [];
      WalkInventories(list, server, (stack, parent, i) =>
      {
        if (SatisfiesFilter(stack, filter))
        {
          stacks.Add(stack); 
        }
      });
      if (stacks.Count == 0)
      {
        return null; 
      }
      return new PendingRecovery(worldData, player, entityBytes, list, stacks);
    }

    private static void WalkInventories(List<InventoryBase> inventories, IWorldAccessor world, Action<ItemStack, object, int> action)
    {
      foreach (InventoryBase inventory in inventories)
      {
        foreach (ItemSlot? itemSlot in inventory)
        {
          if (itemSlot != null && itemSlot is not ItemSlotBagContent)
          {
            WalkStack(itemSlot.Itemstack, world, action, itemSlot, 0); 
          }
        }
      }
    }

    private static void WalkStack(ItemStack? stack, IWorldAccessor world, Action<ItemStack, object, int> action, object parent, int index)
    {
      if (stack == null)
      {
        return; 
      }
      stack.ResolveBlockOrItem(world);
      if (stack.Collectible?.Code == null)
      {
        return; 
      }
      string code = stack.Collectible.Code.ToString();
      if (code is "game:item" or "game:block")
      {
        return;
      }
      action.Invoke(stack, parent, index);
      ItemStack[]? contents = stack.Collectible.GetCollectibleInterface<IHeldBag>()?.GetContents(stack, world);
      if (contents == null)
      {
        return;
      }
      for (int i = 0; i < contents.Length; i++)
      {
        ItemStack stack1 = contents[i];
        WalkStack(stack1, world, action, stack, i);
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
        FieldInfo InventoriesField,
        ServerMain World)
    ;

    private sealed record PendingRecovery(
      ServerWorldPlayerData WorldData,
      PlayerUidName Player,
      byte[]? EntityBytes,
      List<InventoryBase> Inventories,
      List<ItemStack> Stacks)
    ;

    private enum EOperation
    {
      List, Move, Copy
    }
}
