using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Buffs;
using StardewValley.GameData.Buffs;
using StardewValley.Monsters;
using StardewValley.Objects;
using StardewValley.Tools;
using xTile.Layers;

namespace TerrariaInStardew;

internal sealed record TerrariaItemInfo(
    string Id,
    string Name,
    int Stack,
    int UseTime,
    bool AutoReuse,
    int Pick,
    int Axe,
    int Hammer,
    int Defense,
    int Damage,
    bool Melee,
    bool Projectile,
    bool Thrown,
    bool Magic,
    bool Summon,
    bool UsesAmmo,
    bool AmmoItem,
    string AmmoId,
    int AmmoSlot,
    int Mana,
    int HealLife,
    int HealMana,
    string BuffId,
    int BuffTime,
    int CreateTile,
    int CreateWall,
    int FishingPole,
    int Bait,
    bool Accessory,
    int MountType,
    bool Consumable,
    string FishingBaitId,
    int FishingBaitSlot,
    bool GrapplingHook
);

internal sealed record SceneItemUseResult(bool Success, bool Consume, bool ConsumeAmmo, string Message, bool ConsumeBait = false);

internal static class StardewItemInteraction
{
    private const int TileSize = 64;
    private const float MaxReach = TileSize * 6f;
    private const float GrappleRange = TileSize * 12f;
    private const float GrapplePullDistance = TileSize * 3f;
    private const string MinedTilePrefix = "Codex.TerrariaInStardew.MinedMapTile.";
    private static ITranslationHelper? translations;
    private sealed record MappedBuff(string Id, string Name, string Description, int Icon, BuffAttributesData Effects);

    public static void SetTranslations(ITranslationHelper helper) => translations = helper;

    private static bool IsMeleeWeapon(TerrariaItemInfo item) =>
        item.Melee || (item.Damage > 0 && !item.Projectile && !item.Thrown && !item.Magic && !item.Summon);

    public static bool HasMapping(TerrariaItemInfo? item)
    {
        if (item is null || item.Stack <= 0)
            return false;
        if (IsHomeTeleport(item) || item.HealLife > 0 || item.HealMana > 0 || item.Pick > 0 || item.Axe > 0 || item.Hammer > 0 || item.Defense > 0 ||
            item.FishingPole > 0 || item.Bait > 0 || item.MountType >= 0 || item.GrapplingHook || GetMappedAccessoryBuff(item) is not null ||
            item.AmmoItem ||
            (item.Damage > 0 && (IsMeleeWeapon(item) || item.Projectile || item.Thrown || item.Summon)) ||
            GetMappedBuff(item) is not null || item.BuffTime > 0 && !string.IsNullOrWhiteSpace(item.BuffId) ||
            Normalize(item.BuffId) == "regeneration")
            return true;
        return GetPlaceableKind(item) is not null;
    }

    public static SceneItemUseResult Use(TerrariaItemInfo? item, Vector2 worldPosition)
    {
        if (item is null || item.Stack <= 0 || string.IsNullOrWhiteSpace(item.Id))
            return new(false, false, false, "No selected Terraria item data is available yet.");

        try
        {
            return UseMappedItem(item, worldPosition);
        }
        catch (Exception ex)
        {
            return new(false, false, false, $"Stardew could not use Terraria item '{item.Name}': {ex.GetBaseException().Message}");
        }
    }

    private static SceneItemUseResult UseMappedItem(TerrariaItemInfo item, Vector2 worldPosition)
    {
        Farmer farmer = Game1.player;
        GameLocation location = Game1.currentLocation;
        int tileX = (int)MathF.Floor(worldPosition.X / TileSize);
        int tileY = (int)MathF.Floor(worldPosition.Y / TileSize);

        if (IsHomeTeleport(item))
            return WarpHome(item, farmer);

        if (item.HealLife > 0 || item.HealMana > 0)
            return UsePotion(item, farmer);

        if (item.Defense > 0 && !item.Accessory)
        {
            int defense = Math.Clamp((item.Defense + 2) / 3, 1, 8);
            return ApplyBuff(item, farmer, new MappedBuff(
                "Codex_TerrariaInStardew_Armor",
                "Terraria Armor",
                "Defense adapted from Terraria armor.",
                Buff.defense,
                new BuffAttributesData { Defense = defense }));
        }

        if (Normalize(item.BuffId) == "regeneration")
        {
            if (farmer.health >= farmer.maxHealth)
                return new(false, false, false, $"'{item.Name}' had no effect because Stardew health is already full.");
            int restored = Math.Min(Math.Max(1, farmer.maxHealth / 10), farmer.maxHealth - farmer.health);
            farmer.health += restored;
            return new(true, item.Consumable, false, $"'{item.Name}' restored {restored} Stardew health.");
        }

        if (IsRegenerationAccessory(item))
        {
            if (farmer.health >= farmer.maxHealth)
                return new(false, false, false, $"'{item.Name}' had no effect because Stardew health is already full.");
            int healed = Math.Min(10, farmer.maxHealth - farmer.health);
            farmer.health += healed;
            return new(true, false, false, $"'{item.Name}' restored {healed} Stardew health.");
        }

        if (GetMappedAccessoryBuff(item) is MappedBuff accessoryBuff)
            return ApplyBuff(item, farmer, accessoryBuff);

        if (GetMappedBuff(item) is MappedBuff mappedBuff)
            return ApplyBuff(item, farmer, mappedBuff);

        if (item.BuffTime > 0 && !string.IsNullOrWhiteSpace(item.BuffId))
            return ApplyBuff(item, farmer, new MappedBuff(
                "Codex_TerrariaInStardew_GenericBuff", "Terraria: " + item.Name,
                "A Terraria buff adapted for Stardew Valley.", Buff.defense,
                new BuffAttributesData { Defense = 1 }));

        if (item.MountType >= 0)
        {
            return ApplyBuff(item, farmer, new MappedBuff(
                "Codex_TerrariaInStardew_Mount", "Terraria Mount", "Movement speed increased.", Buff.speed,
                new BuffAttributesData { Speed = 1 }));
        }

        if (item.GrapplingHook)
            return UseGrapplingHook(item, location, farmer, worldPosition);

        Rectangle farmerBounds = farmer.GetBoundingBox();
        Vector2 origin = new(farmerBounds.Center.X, farmerBounds.Center.Y);
        if (Vector2.DistanceSquared(origin, worldPosition) > MaxReach * MaxReach)
            return new(false, false, false, Translate("scene-item-out-of-reach", null, "Target is out of reach."));

        if (item.FishingPole > 0 || item.Bait > 0)
            return FishAt(item, location, farmer, tileX, tileY);

        string? placeableKind = GetPlaceableKind(item);
        if (placeableKind is "bomb" or "grenade" or "dynamite")
            return PlaceItem(item, location, farmer, tileX, tileY);

        if (item.Pick > 0 || item.Axe > 0 || item.Hammer > 0)
            return UseTool(item, location, farmer, tileX, tileY);

        if (item.AmmoItem || (item.Damage > 0 && (IsMeleeWeapon(item) || item.Projectile || item.Thrown || item.Summon)))
            return HitMonster(item, location, farmer, worldPosition);

        if (item.CreateTile >= 0 || item.CreateWall >= 0 || placeableKind is not null)
            return PlaceItem(item, location, farmer, tileX, tileY);

        return new(false, false, false, $"Terraria item '{item.Name}' has no Stardew interaction mapping yet.");
    }

    private static SceneItemUseResult ApplyBuff(TerrariaItemInfo item, Farmer farmer, MappedBuff mappedBuff)
    {
        int duration = item.BuffTime > 0
            ? (int)Math.Clamp((long)item.BuffTime * 1000 / 60, 1000L, 86_400_000L)
            : 120_000;
        var buff = new Buff(
            mappedBuff.Id,
            "terraria",
            item.Name,
            duration,
            Game1.buffsIcons,
            mappedBuff.Icon,
            new BuffEffects(mappedBuff.Effects),
            false,
            mappedBuff.Name,
            mappedBuff.Description);
        farmer.applyBuff(buff);
        return new(true, item.Consumable, false, $"Used '{item.Name}' and applied its Stardew buff.");
    }

    private static SceneItemUseResult FishAt(TerrariaItemInfo item, GameLocation location, Farmer farmer, int tileX, int tileY)
    {
        if (!location.isWaterTile(tileX, tileY))
            return new(false, false, false, $"'{item.Name}' needs a Stardew water tile under the cursor.");

        MethodInfo? getFish = FindMethod(location.GetType(), "getFish", new[]
        {
            typeof(float), typeof(string), typeof(int), typeof(Farmer), typeof(double), typeof(Vector2), typeof(string)
        });
        if (getFish is null)
            return new(false, false, false, "Stardew's location fishing method was not found.");

        object? fish = getFish.Invoke(location, new object[]
        {
            0f,
            item.Bait > 0 || item.FishingBaitSlot >= 0 ? "(O)685" : "",
            1,
            farmer,
            0d,
            new Vector2(tileX, tileY),
            location.NameOrUniqueName
        });
        if (fish is not Item caughtFish)
            return new(false, false, false, $"No fish can be caught at {tileX},{tileY} right now.");

        MethodInfo? addToInventory = FindMethod(farmer.GetType(), "addItemToInventoryBool", new[] { typeof(Item), typeof(bool) });
        if (addToInventory?.Invoke(farmer, new object[] { caughtFish, false }) is not true)
            return new(false, false, false, "Your Stardew inventory is full; the fishing item was not consumed.");

        bool consumeBait = item.FishingPole > 0 && item.FishingBaitSlot >= 0 &&
            !string.IsNullOrWhiteSpace(item.FishingBaitId);
        return new(true, item.FishingPole <= 0 && item.Consumable, false,
            $"'{item.Name}' caught {caughtFish.DisplayName} in Stardew Valley.", consumeBait);
    }

    private static SceneItemUseResult UsePotion(TerrariaItemInfo item, Farmer farmer)
    {
        bool changed = false;
        if (item.HealLife > 0 && farmer.health < farmer.maxHealth)
        {
            int heal = Math.Max(1, (int)Math.Round(item.HealLife * 0.25));
            farmer.health = Math.Min(farmer.maxHealth, farmer.health + heal);
            changed = true;
        }

        if (item.HealMana > 0 && farmer.Stamina < farmer.MaxStamina)
        {
            int stamina = Math.Max(1, (int)Math.Round(item.HealMana * 0.5));
            farmer.Stamina = Math.Min(farmer.MaxStamina, farmer.Stamina + stamina);
            changed = true;
        }

        return changed
            ? new(true, item.Consumable, false, $"Used Terraria potion '{item.Name}' in Stardew Valley.")
            : new(false, false, false, $"'{item.Name}' had no effect because health and stamina are already full.");
    }

    private static SceneItemUseResult WarpHome(TerrariaItemInfo item, Farmer farmer)
    {
        MethodInfo? getHome = FindMethod(farmer.GetType(), "getHome", Type.EmptyTypes);
        GameLocation? home = getHome?.Invoke(farmer, null) as GameLocation;
        home ??= Game1.getLocationFromName("FarmHouse");
        if (home is null)
            return new(false, false, false, $"Could not find a Stardew home for '{item.Name}'.");

        object? entry = FindMethod(home.GetType(), "getEntryLocation", Type.EmptyTypes)?.Invoke(home, null);
        entry ??= FindMethod(home.GetType(), "getEntryLocation", new[] { typeof(Farmer) })?.Invoke(home, new object[] { farmer });
        Vector2 tile = entry is Vector2 position ? position : new Vector2(7, 5);
        Game1.warpFarmer(home.NameOrUniqueName, (int)tile.X, (int)tile.Y, false);
        return new(true, item.Consumable, false, $"Used '{item.Name}' to return to {home.DisplayName}.");
    }

    private static SceneItemUseResult UseTool(TerrariaItemInfo item, GameLocation location, Farmer farmer, int tileX, int tileY)
    {
        Type type = item.Axe > 0 ? typeof(Axe) : typeof(Pickaxe);
        object? tool = Activator.CreateInstance(type);
        if (tool is null)
            return new(false, false, false, $"Could not create a Stardew {type.Name} for '{item.Name}'.");

        int power = item.Axe > 0 ? item.Axe * 5 : Math.Max(item.Pick, item.Hammer);
        int tier = power >= 65 ? 4 : power >= 50 ? 3 : power >= 40 ? 2 : power >= 35 ? 1 : 0;
        if (!SetUpgradeLevel(tool, tier))
            return new(false, false, false, $"Could not set the Stardew {type.Name} upgrade level for '{item.Name}'.");

        MethodInfo? doFunction = FindMethod(type, "DoFunction", new[]
        {
            typeof(GameLocation), typeof(int), typeof(int), typeof(int), typeof(Farmer)
        });
        if (doFunction is null)
            return new(false, false, false, $"Stardew {type.Name}.DoFunction was not found.");

        string before = CaptureTileState(location, tileX, tileY);
        doFunction.Invoke(tool, new object[] { location, tileX * TileSize, tileY * TileSize, 0, farmer });
        if (before == CaptureTileState(location, tileX, tileY))
        {
            if (item.Axe <= 0 && TryRemoveFurniture(location, farmer, tileX, tileY, out SceneItemUseResult furnitureResult))
                return furnitureResult;

            if (item.Axe <= 0 && TryMineMapTile(item, location, farmer, tileX, tileY, out SceneItemUseResult tileResult))
                return tileResult;

            bool isAxe = item.Axe > 0;
            string target = Translate(isAxe ? "scene-item-axe-target" : "scene-item-pick-target", null,
                isAxe ? "trees or stumps" : "breakable map tiles, rocks, ore nodes, or mine boulders");
            string messageKey = isAxe ? "scene-item-axe-no-effect" : "scene-item-tool-no-effect";
            string fallback = isAxe
                ? $"'{item.Name}' had no effect at {tileX},{tileY}. Aim at Stardew {target} within six tiles; axes do not mine map tiles."
                : $"'{item.Name}' did not mine a map tile or affect a Stardew target at {tileX},{tileY}. Aim at Stardew {target} within six tiles.";
            return new(false, false, false,
                Translate(messageKey, new { item = item.Name, x = tileX, y = tileY, target }, fallback));
        }

        return new(true, false, false, $"'{item.Name}' changed Stardew tile {tileX},{tileY}.");
    }

    public static int RestoreMinedMapTiles(GameLocation location)
    {
        int restored = 0;
        foreach ((string key, string value) in location.modData.Pairs.ToArray())
        {
            if (!key.StartsWith(MinedTilePrefix, StringComparison.Ordinal))
                continue;

            string[] coordinates = key[MinedTilePrefix.Length..].Split('.', 3);
            string[] tileInfo = value.Split('|', 3);
            if (coordinates.Length != 3 || tileInfo.Length != 2 ||
                !int.TryParse(coordinates[0], out int tileX) || !int.TryParse(coordinates[1], out int tileY) ||
                !int.TryParse(tileInfo[1], out int tileIndex))
                continue;

            Layer? layer = location.Map?.GetLayer(coordinates[2]);
            if (layer is null || tileX < 0 || tileY < 0 || tileX >= layer.LayerWidth || tileY >= layer.LayerHeight)
                continue;

            xTile.Tiles.Tile? tile = layer.Tiles[tileX, tileY];
            if (tile is not null && tile.TileIndex == tileIndex &&
                string.Equals(tile.TileSheet.Id, tileInfo[0], StringComparison.Ordinal))
            {
                location.removeMapTile(tileX, tileY, coordinates[2]);
                restored++;
            }
        }
        return restored;
    }

    private static SceneItemUseResult UseGrapplingHook(TerrariaItemInfo item, GameLocation location, Farmer farmer, Vector2 target)
    {
        Rectangle bounds = farmer.GetBoundingBox();
        Vector2 origin = new(bounds.Center.X, bounds.Center.Y);
        Vector2 direction = target - origin;
        float distance = direction.Length();
        if (distance < 1f)
            return new(false, false, false, $"'{item.Name}' needs a target away from the farmer.");
        if (distance > GrappleRange)
            return new(false, false, false, Translate("scene-item-grapple-out-of-range", null, "Grapple target is out of range."));

        direction /= distance;
        float pullDistance = Math.Min(GrapplePullDistance, Math.Max(0f, distance - TileSize * 0.75f));
        if (pullDistance < 8f)
            return new(false, false, false, Translate("scene-item-grapple-too-close", null, "The grapple target is too close to pull toward."));

        Vector2 start = farmer.Position;
        Vector2 lastValid = start;
        for (float moved = 8f; moved <= pullDistance; moved += 8f)
        {
            Vector2 candidate = start + direction * moved;
            if (!CanFarmerOccupyPosition(location, farmer, candidate))
                break;
            lastValid = candidate;
        }

        if (Vector2.DistanceSquared(start, lastValid) < 64f)
            return new(false, false, false, Translate("scene-item-grapple-blocked", null, "The grapple path is blocked."));

        farmer.Position = lastValid;
        location.playSound("hammer");
        return new(true, false, false,
            Translate("scene-item-grapple-pulled", new { item = item.Name }, $"'{item.Name}' pulled the farmer toward the target."));
    }

    private static bool CanFarmerOccupyPosition(GameLocation location, Farmer farmer, Vector2 position)
    {
        Rectangle current = farmer.GetBoundingBox();
        Rectangle candidate = new((int)MathF.Round(position.X), (int)MathF.Round(position.Y), current.Width, current.Height);
        int left = candidate.Left + 2;
        int right = candidate.Right - 2;
        int top = candidate.Top + 2;
        int bottom = candidate.Bottom - 2;
        Vector2[] samplePoints =
        {
            new(left, top), new(right, top), new(left, bottom), new(right, bottom),
            new(candidate.Center.X, candidate.Center.Y)
        };
        foreach (Vector2 point in samplePoints)
        {
            Vector2 tile = new(MathF.Floor(point.X / TileSize), MathF.Floor(point.Y / TileSize));
            if (!location.isTileLocationOpen(tile) || location.objects.ContainsKey(tile) || location.terrainFeatures.ContainsKey(tile) ||
                location.characters.Any(character => !ReferenceEquals(character, farmer) && character.GetBoundingBox().Intersects(candidate)))
                return false;
        }
        return true;
    }

    private static bool TryMineMapTile(TerrariaItemInfo item, GameLocation location, Farmer farmer, int tileX, int tileY,
        out SceneItemUseResult result)
    {
        result = default!;
        var layers = location.Map?.Layers;
        if (layers is null)
            return false;

        Layer? backLayer = layers.Cast<Layer>().FirstOrDefault(layer => layer.Id == "Back");
        bool inMapBounds = backLayer is not null
            ? tileX >= 0 && tileY >= 0 && tileX < backLayer.LayerWidth && tileY < backLayer.LayerHeight
            : layers.Cast<Layer>().Any(layer => tileX >= 0 && tileY >= 0 && tileX < layer.LayerWidth && tileY < layer.LayerHeight);
        if (!inMapBounds)
            return false;

        Rectangle tileBounds = new(tileX * TileSize, tileY * TileSize, TileSize, TileSize);
        if (farmer.GetBoundingBox().Intersects(tileBounds) ||
            location.characters.Any(character => character.GetBoundingBox().Intersects(tileBounds)) ||
            location.animals.Values.Any(animal => animal.GetBoundingBox().Intersects(tileBounds)) ||
            location.objects.ContainsKey(new Vector2(tileX, tileY)) ||
            location.terrainFeatures.ContainsKey(new Vector2(tileX, tileY)) ||
            location.resourceClumps.Any(clump => clump.getBoundingBox().Intersects(tileBounds)) ||
            location.buildings.Any(building => building.occupiesTile(new Vector2(tileX, tileY))) ||
            location.warps.Any(warp => warp.X == tileX && warp.Y == tileY) ||
            HasNearbyEntityOverlapping(location, "furniture", tileBounds) ||
            HasNearbyEntityOverlapping(location, "largeTerrainFeatures", tileBounds))
        {
            result = new(false, false, false,
                Translate("scene-item-tile-protected", new { x = tileX, y = tileY }, "A character, animal, object, building, warp, or crop occupies this tile."));
            return true;
        }

        Layer[] targetLayers = layers.Cast<Layer>()
            .Where(layer => tileX >= 0 && tileY >= 0 && tileX < layer.LayerWidth && tileY < layer.LayerHeight)
            .OrderByDescending(GetLayerMiningPriority)
            .ToArray();
        xTile.Tiles.Tile?[] tilesAtTarget = targetLayers.Select(layer => layer.Tiles[tileX, tileY]).ToArray();
        if (tilesAtTarget.Any(tile => tile is not null && HasProtectedMapAction(tile)))
        {
            result = new(false, false, false,
                Translate("scene-item-map-tile-protected", new { x = tileX, y = tileY }, "This tile contains a map action, warp, or protected property."));
            return true;
        }

        for (int index = 0; index < targetLayers.Length; index++)
        {
            Layer layer = targetLayers[index];
            xTile.Tiles.Tile? tile = tilesAtTarget[index];
            if (tile is null)
                continue;

            location.removeMapTile(tileX, tileY, layer.Id);
            if (layer.Tiles[tileX, tileY] is not null)
            {
                result = new(false, false, false,
                    Translate("scene-item-map-tile-protected", new { x = tileX, y = tileY }, "Stardew did not remove this map tile."));
                return true;
            }

            string recordKey = $"{MinedTilePrefix}{tileX}.{tileY}.{layer.Id}";
            location.modData[recordKey] = $"{tile.TileSheet.Id}|{tile.TileIndex}";

            Vector2 dropPosition = new(tileX * TileSize + TileSize / 2f, tileY * TileSize + TileSize / 2f);
            location.debris.Add(new Debris(ItemRegistry.Create("(O)390"), dropPosition));
            location.playSound("hammer");
            result = new(true, false, false,
                Translate("scene-item-map-tile-mined", new { item = item.Name, x = tileX, y = tileY },
                    $"'{item.Name}' mined a map tile at {tileX},{tileY} and dropped Stone."));
            return true;
        }

        return false;
    }

    private static int GetLayerMiningPriority(Layer layer) => layer.Id switch
    {
        "AlwaysFront" => 50,
        "Front" => 40,
        "Buildings" => 30,
        "Back" => 10,
        _ => 20
    };

    private static bool HasProtectedMapAction(xTile.Tiles.Tile tile)
    {
        bool HasProperty(string key) => tile.Properties.ContainsKey(key) || tile.TileIndexProperties.ContainsKey(key);
        return HasProperty("Action") || HasProperty("TouchAction") || HasProperty("Warp") ||
            HasProperty("TreeStump") || HasProperty("NoMine") || HasProperty("NoMining");
    }

    private static bool HasFurnitureContents(Furniture furniture) =>
        furniture.heldObject.Value is not null ||
        furniture is StorageFurniture storage && storage.heldItems.Any(storedItem => storedItem is not null);

    private static bool TryRemoveFurniture(GameLocation location, Farmer farmer, int tileX, int tileY, out SceneItemUseResult result)
    {
        result = default!;
        int pixelX = tileX * TileSize + TileSize / 2;
        int pixelY = tileY * TileSize + TileSize / 2;
        Furniture? furniture = location.furniture.Reverse()
            .OrderBy(candidate => candidate.isPassable())
            .FirstOrDefault(candidate => candidate.GetBoundingBox().Contains(pixelX, pixelY));
        if (furniture is null)
            return false;

        string furnitureName = furniture.DisplayName;
        if (HasFurnitureContents(furniture))
        {
            result = new(false, false, false,
                Translate("scene-item-furniture-not-empty", new { furniture = furnitureName },
                    $"Remove the items on or inside '{furnitureName}' before picking it up."));
            return true;
        }

        if (!furniture.canBeRemoved(farmer))
        {
            result = new(false, false, false,
                Translate("scene-item-furniture-protected", new { furniture = furnitureName },
                    $"'{furnitureName}' is occupied or protected and can't be removed."));
            return true;
        }

        if (!farmer.couldInventoryAcceptThisItem(furniture))
        {
            result = new(false, false, false,
                Translate("scene-item-furniture-inventory-full", new { furniture = furnitureName },
                    $"Your Stardew inventory is full; '{furnitureName}' was not removed."));
            return true;
        }

        bool removalRequestReturned = false;
        bool removalCompleted = false;
        bool removalSucceeded = false;
        string completionMessage = "";
        furniture.AttemptRemoval(candidate =>
        {
            if (ReferenceEquals(candidate, furniture) && !HasFurnitureContents(candidate) && candidate.canBeRemoved(farmer) &&
                farmer.couldInventoryAcceptThisItem(candidate) && location.furniture.Contains(candidate))
            {
                candidate.performRemoveAction();
                if (location.furniture.Remove(candidate))
                {
                    removalSucceeded = true;
                    if (farmer.addItemToInventoryBool(candidate))
                    {
                        completionMessage = Translate("scene-item-furniture-removed", new { furniture = furnitureName },
                            $"Picked up '{furnitureName}'.");
                    }
                    else
                    {
                        Vector2 dropPosition = new(candidate.GetBoundingBox().Center.X, candidate.GetBoundingBox().Center.Y);
                        location.debris.Add(new Debris(candidate, dropPosition));
                        completionMessage = Translate("scene-item-furniture-dropped", new { furniture = furnitureName },
                            $"'{furnitureName}' was dropped nearby because the Stardew inventory filled up.");
                    }
                }
            }

            if (!removalSucceeded)
            {
                completionMessage = Translate("scene-item-furniture-protected", new { furniture = furnitureName },
                    $"'{furnitureName}' is occupied or protected and can't be removed.");
            }

            removalCompleted = true;
            if (removalRequestReturned)
                Game1.addHUDMessage(new HUDMessage(completionMessage, removalSucceeded ? HUDMessage.newQuest_type : HUDMessage.error_type));
        });
        removalRequestReturned = true;

        if (removalCompleted)
        {
            result = new(removalSucceeded, false, false, completionMessage);
            return true;
        }

        result = new(true, false, false,
            Translate("scene-item-furniture-removal-pending", new { furniture = furnitureName },
                $"Stardew is checking whether '{furnitureName}' can be safely removed."));
        return true;
    }

    private static bool HasNearbyEntityOverlapping(GameLocation location, string collectionName, Rectangle bounds)
    {
        if (GetMemberValue(location, collectionName) is not IEnumerable entities)
            return false;

        foreach (object? entity in entities)
        {
            if (entity is null)
                continue;

            object? boundingBox = GetMemberValue(entity, "BoundingBox", "boundingBox");
            if (boundingBox is Rectangle rectangle && rectangle.Intersects(bounds))
                return true;

            MethodInfo? getBoundingBox = FindMethod(entity.GetType(), "GetBoundingBox", Type.EmptyTypes);
            if (getBoundingBox?.Invoke(entity, null) is Rectangle methodRectangle && methodRectangle.Intersects(bounds))
                return true;
        }

        return false;
    }

    private static string CaptureTileState(GameLocation location, int tileX, int tileY)
    {
        var state = new StringBuilder();
        for (int x = tileX - 1; x <= tileX + 1; x++)
        {
            for (int y = tileY - 1; y <= tileY + 1; y++)
            {
                Vector2 tile = new(x, y);
                if (location.objects.TryGetValue(tile, out StardewValley.Object? worldObject))
                {
                    state.Append("object@").Append(x).Append(',').Append(y).Append(':')
                        .Append(worldObject.QualifiedItemId).Append(':').Append(worldObject.Stack);
                    AppendStateMembers(state, worldObject);
                }
                if (location.terrainFeatures.TryGetValue(tile, out var terrainFeature))
                {
                    state.Append("feature@").Append(x).Append(',').Append(y).Append(':')
                        .Append(terrainFeature.GetType().FullName);
                    AppendStateMembers(state, terrainFeature);
                }

                if (location.Map?.Layers is { } layers)
                {
                    foreach (Layer layer in layers)
                    {
                        if (x < 0 || y < 0 || x >= layer.LayerWidth || y >= layer.LayerHeight ||
                            layer.Tiles[x, y] is not xTile.Tiles.Tile mapTile)
                            continue;

                        state.Append("map@").Append(layer.Id).Append(':').Append(x).Append(',').Append(y)
                            .Append(':').Append(mapTile.TileSheet.Id).Append(':').Append(mapTile.TileIndex);
                    }
                }
            }
        }

        AppendNearbyResourceClumps(state, location, tileX, tileY);
        AppendNearbyEntityState(state, location, "furniture", tileX, tileY);
        AppendNearbyEntityState(state, location, "largeTerrainFeatures", tileX, tileY);
        return state.ToString();
    }

    private static void AppendNearbyEntityState(StringBuilder state, GameLocation location, string collectionName, int tileX, int tileY)
    {
        if (GetMemberValue(location, collectionName) is not IEnumerable entities)
            return;

        foreach (object? entity in entities)
        {
            if (entity is null)
                continue;

            object? boundingBox = GetMemberValue(entity, "BoundingBox", "boundingBox");
            if (boundingBox is not Rectangle rectangle)
            {
                MethodInfo? getBoundingBox = FindMethod(entity.GetType(), "GetBoundingBox", Type.EmptyTypes);
                boundingBox = getBoundingBox?.Invoke(entity, null);
                if (boundingBox is Rectangle methodRectangle)
                    rectangle = methodRectangle;
                else
                    rectangle = default;
            }

            Rectangle neighborhood = new((tileX - 4) * TileSize, (tileY - 4) * TileSize, TileSize * 9, TileSize * 9);
            if (rectangle != default && !rectangle.Intersects(neighborhood))
                continue;

            state.Append(collectionName).Append('@').Append(entity.GetType().FullName);
            object? name = GetMemberValue(entity, "QualifiedItemId", "Name", "Tile", "TileLocation");
            if (name is not null)
                state.Append(':').Append(name);
            AppendStateMembers(state, entity);
        }
    }

    private static object? GetMemberValue(object target, params string[] names)
    {
        for (Type? type = target.GetType(); type is not null; type = type.BaseType)
        {
            foreach (string name in names)
            {
                FieldInfo? field = AccessTools.Field(type, name);
                if (field is not null)
                {
                    try { return field.GetValue(target); }
                    catch (Exception) { }
                }

                PropertyInfo? property = AccessTools.Property(type, name);
                if (property?.CanRead == true)
                {
                    try { return property.GetValue(target); }
                    catch (Exception) { }
                }
            }
        }

        return null;
    }

    private static void AppendNearbyResourceClumps(StringBuilder state, GameLocation location, int tileX, int tileY)
    {
        FieldInfo? clumpsField = AccessTools.Field(location.GetType(), "resourceClumps");
        if (clumpsField?.GetValue(location) is not IEnumerable clumps)
            return;

        foreach (object? clump in clumps)
        {
            if (clump is null)
                continue;

            object? tileValue = AccessTools.Property(clump.GetType(), "Tile")?.GetValue(clump);
            if (tileValue is Vector2 tile &&
                (Math.Abs(tile.X - tileX) > 4 || Math.Abs(tile.Y - tileY) > 4))
                continue;

            state.Append("clump@").Append(tileValue ?? "unknown").Append(':')
                .Append(clump.GetType().FullName);
            AppendStateMembers(state, clump);
        }
    }

    private static void AppendStateMembers(StringBuilder state, object target)
    {
        string[] trackedNames = { "health", "stump", "growthstage", "daysuntilmature", "treetype", "state", "minutesuntilready", "readyforharvest", "ison", "stack" };
        for (Type? type = target.GetType(); type is not null; type = type.BaseType)
        {
            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                string normalizedName = Normalize(field.Name);
                if (!trackedNames.Any(name => normalizedName.Contains(name, StringComparison.Ordinal)))
                    continue;

                try
                {
                    object? value = field.GetValue(target);
                    if (value is not null && (value.GetType().IsPrimitive || value is string || value is Enum))
                        state.Append(':').Append(normalizedName).Append('=').Append(value);
                    else if (value is not null)
                    {
                        PropertyInfo? valueProperty = value.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        object? innerValue = valueProperty?.GetValue(value);
                        if (innerValue is not null && (innerValue.GetType().IsPrimitive || innerValue is string || innerValue is Enum))
                            state.Append(':').Append(normalizedName).Append('=').Append(innerValue);
                    }
                }
                catch (Exception)
                {
                    // A diagnostic field should not interrupt the native tool action.
                }
            }
        }
    }

    private static bool SetUpgradeLevel(object tool, int level)
    {
        for (Type? type = tool.GetType(); type is not null; type = type.BaseType)
        {
            FieldInfo? field = AccessTools.Field(type, "upgradeLevel");
            if (field is not null)
            {
                if (field.FieldType == typeof(int))
                {
                    field.SetValue(tool, level);
                    return true;
                }

                object? netValue = field.GetValue(tool);
                PropertyInfo? valueProperty = netValue is null
                    ? null
                    : AccessTools.Property(netValue.GetType(), "Value");
                if (valueProperty?.CanWrite == true && valueProperty.PropertyType == typeof(int))
                {
                    valueProperty.SetValue(netValue, level);
                    return true;
                }

                return false;
            }

            PropertyInfo? property = AccessTools.Property(type, "UpgradeLevel");
            if (property?.CanWrite == true && property.PropertyType == typeof(int))
            {
                property.SetValue(tool, level);
                return true;
            }
        }

        return false;
    }

    private static SceneItemUseResult HitMonster(TerrariaItemInfo item, GameLocation location, Farmer farmer, Vector2 target)
    {
        Monster? monster = location.characters.OfType<Monster>()
            .Where(candidate => candidate.Health > 0)
            .Where(candidate => candidate.GetBoundingBox().InflateCopy(32).Contains((int)target.X, (int)target.Y))
            .Where(candidate => Vector2.DistanceSquared(candidate.Position, farmer.Position) <= MaxReach * MaxReach)
            .OrderBy(candidate => Vector2.DistanceSquared(candidate.Position, target))
            .FirstOrDefault();
        if (monster is null)
            return new(false, false, false, Translate("scene-item-monster-not-hit", new { item = item.Name },
                $"'{item.Name}' did not hit a Stardew monster at the cursor. Aim at a living monster within six tiles."));

        bool projectileHit = (item.Projectile || item.Thrown || item.Summon || item.AmmoItem) && !item.Melee;
        bool consumeAmmo = item.Projectile && !item.Melee && !item.Summon && !item.Thrown && !item.AmmoItem && item.UsesAmmo;
        if (consumeAmmo && (item.AmmoSlot < 0 || string.IsNullOrWhiteSpace(item.AmmoId)))
            return new(false, false, false, $"'{item.Name}' needs ammunition, but no matching Terraria ammo is available.");
        int staminaCost = item.Magic || item.Summon ? Math.Max(0, item.Mana) : 0;
        if (staminaCost > 0 && farmer.Stamina < staminaCost)
            return new(false, false, false, $"Not enough Stardew stamina to use '{item.Name}'.");

        int damage = Math.Max(1, item.Damage / 3);
        int actualDamage;
        if (projectileHit)
        {
            int previousHealth = monster.Health;
            bool hit = location.damageMonster(
                new Rectangle((int)target.X - 16, (int)target.Y - 16, 32, 32),
                damage,
                damage,
                false,
                farmer,
                true);
            actualDamage = previousHealth - Math.Max(0, monster.Health);
            if (!hit || actualDamage <= 0)
                return new(false, false, false, $"'{item.Name}' did not damage {monster.Name}.");
        }
        else
        {
            MethodInfo? takeDamage = FindMethod(monster.GetType(), "takeDamage", new[]
            {
                typeof(int), typeof(int), typeof(int), typeof(bool), typeof(double), typeof(Farmer)
            });
            if (takeDamage is null)
                return new(false, false, false, $"No Stardew damage method was found for {monster.GetType().Name}.");

            object? result = takeDamage.Invoke(monster, new object[]
            {
                damage,
                Math.Sign(monster.Position.X - farmer.Position.X),
                0,
                false,
                0d,
                farmer
            });
            if (result is not int dealt || dealt <= 0)
                return new(false, false, false, $"'{item.Name}' did not damage {monster.Name}.");
            actualDamage = dealt;
        }

        if (staminaCost > 0)
            farmer.Stamina -= staminaCost;
        location.playSound("hitEnemy");
        string attackType = item.Summon && !item.Melee
            ? "summon"
            : item.Thrown && !item.Melee ? "thrown" : item.Projectile && !item.Melee ? "projectile" : "melee";
        return new(true, (item.Consumable || item.AmmoItem) && !consumeAmmo, consumeAmmo,
            $"'{item.Name}' landed a {attackType} hit on {monster.Name} for {actualDamage} Stardew damage.");
    }

    private static SceneItemUseResult PlaceItem(TerrariaItemInfo item, GameLocation location, Farmer farmer, int tileX, int tileY)
    {
        string? stardewId = GetPlaceableKind(item) switch
        {
            "torch" => "(O)93",
            "workbench" => "(BC)114",
            "chest" => "(BC)130",
            "campfire" => "(BC)146",
            "furnace" => "(BC)13",
            "woodfloor" => "(O)328",
            "stonefloor" => "(O)329",
            "genericblock" => "(O)329",
            "woodwall" => "(O)322",
            "hardwoodwall" => "(O)298",
            "stonewall" => "(O)323",
            "genericwall" => "(O)323",
            "grenade" or "molotov" => "(O)286",
            "bomb" when Normalize(item.Id).EndsWith("dynamite", StringComparison.Ordinal) => "(O)288",
            "dynamite" => "(O)288",
            "bomb" => "(O)287",
            _ => null
        };
        if (stardewId is null)
            return new(false, false, false, $"Terraria placeable '{item.Name}' has no Stardew placement mapping yet.");

        Item target = ItemRegistry.Create(stardewId);
        MethodInfo? place = FindMethod(target.GetType(), "placementAction", new[]
        {
            typeof(GameLocation), typeof(int), typeof(int), typeof(Farmer)
        });
        if (place is null)
            return new(false, false, false, $"Stardew placement action was not found for '{item.Name}'.");

        bool placed = place.Invoke(target, new object[] { location, tileX * TileSize, tileY * TileSize, farmer }) is true;
        return placed
            ? new(true, item.Consumable, false, $"Placed Stardew equivalent of '{item.Name}' at {tileX},{tileY}.")
            : new(false, false, false, $"Could not place '{item.Name}' at {tileX},{tileY}.");
    }

    private static string Normalize(string value)
    {
        int separator = Math.Max(value.LastIndexOf('/'), Math.Max(value.LastIndexOf(':'), value.LastIndexOf('.')));
        string name = separator >= 0 && separator < value.Length - 1 ? value[(separator + 1)..] : value;
        return new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }

    private static string? GetPlaceableKind(TerrariaItemInfo item)
    {
        string[] supportedKinds =
        {
            "torch", "workbench", "chest", "bomb", "stickybomb", "bouncybomb", "grenade", "dynamite",
            "campfire", "furnace", "wood", "stone", "woodfloor", "stonefloor",
            "woodwall", "hardwoodwall", "stonewall"
        };
        string normalizedId = Normalize(item.Id);
        const string vanillaItemPrefix = "terraria";
        if (normalizedId.StartsWith(vanillaItemPrefix, StringComparison.Ordinal))
            normalizedId = normalizedId[vanillaItemPrefix.Length..];
        if (supportedKinds.Contains(normalizedId, StringComparer.Ordinal))
            return NormalizePlaceableKind(normalizedId);
        if (normalizedId.EndsWith("torch", StringComparison.Ordinal))
            return "torch";
        if (normalizedId.EndsWith("chest", StringComparison.Ordinal))
            return "chest";

        string normalizedName = Normalize(item.Name);
        if (supportedKinds.Contains(normalizedName, StringComparer.Ordinal))
            return NormalizePlaceableKind(normalizedName);
        if (normalizedName is "woodblock" or "woodplatform")
            return "woodfloor";
        if (normalizedName is "stoneblock" or "stoneplatform")
            return "stonefloor";
        if (item.CreateWall >= 0)
        {
            if (normalizedId.Contains("hardwood", StringComparison.Ordinal) || normalizedName.Contains("hardwood", StringComparison.Ordinal))
                return "hardwoodwall";
            if (normalizedId.Contains("wood", StringComparison.Ordinal) || normalizedName.Contains("wood", StringComparison.Ordinal))
                return "woodwall";
            if (normalizedId.Contains("stone", StringComparison.Ordinal) || normalizedName.Contains("stone", StringComparison.Ordinal))
                return "stonewall";
            return "genericwall";
        }
        if (item.CreateTile >= 0)
        {
            if (normalizedId.Contains("wood", StringComparison.Ordinal) || normalizedName.Contains("wood", StringComparison.Ordinal))
                return "woodfloor";
            if (normalizedId.Contains("stone", StringComparison.Ordinal) || normalizedName.Contains("stone", StringComparison.Ordinal))
                return "stonefloor";
            return "genericblock";
        }
        return null;
    }

    private static string NormalizePlaceableKind(string kind) => kind switch
    {
        "stickybomb" or "bouncybomb" => "bomb",
        "wood" => "woodfloor",
        "stone" => "stonefloor",
        _ => kind
    };

    private static bool IsRegenerationAccessory(TerrariaItemInfo item) =>
        item.Accessory && (
            Normalize(item.Id).Contains("regeneration", StringComparison.Ordinal) ||
            Normalize(item.Name).Contains("regeneration", StringComparison.Ordinal));

    private static bool IsHomeTeleport(TerrariaItemInfo item)
    {
        string id = Normalize(item.Id);
        return id.EndsWith("magicmirror", StringComparison.Ordinal) ||
            id.EndsWith("icemirror", StringComparison.Ordinal) ||
            id.EndsWith("recallpotion", StringComparison.Ordinal) ||
            id.EndsWith("potionofreturn", StringComparison.Ordinal) ||
            id.EndsWith("cellphone", StringComparison.Ordinal) ||
            id.EndsWith("shellphone", StringComparison.Ordinal);
    }

    private static MappedBuff? GetMappedBuff(TerrariaItemInfo item)
    {
        string buff = Normalize(item.BuffId);
        if (buff.StartsWith("terraria", StringComparison.Ordinal))
            buff = buff["terraria".Length..];
        return buff switch
        {
            "swiftness" => new("Codex_TerrariaInStardew_Swiftness", "Terraria: Swiftness", "Movement speed increased.", Buff.speed, new BuffAttributesData { Speed = 1 }),
            "ironskin" => new("Codex_TerrariaInStardew_Ironskin", "Terraria: Ironskin", "Defense increased.", Buff.defense, new BuffAttributesData { Defense = 2 }),
            "endurance" => new("Codex_TerrariaInStardew_Endurance", "Terraria: Endurance", "Defense increased.", Buff.defense, new BuffAttributesData { Defense = 1 }),
            "wrath" => new("Codex_TerrariaInStardew_Wrath", "Terraria: Wrath", "Attack increased.", Buff.attack, new BuffAttributesData { Attack = 3 }),
            "rage" => new("Codex_TerrariaInStardew_Rage", "Terraria: Rage", "Attack increased.", Buff.attack, new BuffAttributesData { Attack = 2 }),
            "mining" => new("Codex_TerrariaInStardew_Mining", "Terraria: Mining", "Mining skill increased.", Buff.mining, new BuffAttributesData { MiningLevel = 2 }),
            "shine" or "spelunker" => new("Codex_TerrariaInStardew_Exploration", "Terraria: Exploration", "Item pickup radius increased.", Buff.magneticRadius, new BuffAttributesData { MagneticRadius = 64 }),
            "wellfed" or "wellfed2" or "wellfed3" => new("Codex_TerrariaInStardew_WellFed", "Terraria: Well Fed", "Stamina increased.", Buff.maxStamina, new BuffAttributesData { MaxStamina = 20 }),
            "tipsy" => new("Codex_TerrariaInStardew_Tipsy", "Terraria: Tipsy", "Attack increased.", Buff.attack, new BuffAttributesData { Attack = 1 }),
            _ => null
        };
    }

    private static MappedBuff? GetMappedAccessoryBuff(TerrariaItemInfo item)
    {
        if (!item.Accessory)
            return null;

        string id = Normalize(item.Id);
        string name = Normalize(item.Name);
        string itemKey = id.StartsWith("terraria", StringComparison.Ordinal) ? id["terraria".Length..] : id;
        if (itemKey.EndsWith("boots", StringComparison.Ordinal) || name.EndsWith("boots", StringComparison.Ordinal))
            return new("Codex_TerrariaInStardew_Boots", "Terraria Boots", "Movement speed increased.", Buff.speed,
                new BuffAttributesData { Speed = 2 });
        if (itemKey.Contains("wings", StringComparison.Ordinal) || name.Contains("wings", StringComparison.Ordinal))
            return new("Codex_TerrariaInStardew_Wings", "Terraria Wings", "Movement speed increased.", Buff.speed,
                new BuffAttributesData { Speed = 2 });
        if (itemKey.Contains("cloudinabottle", StringComparison.Ordinal) || itemKey.Contains("balloon", StringComparison.Ordinal))
            return new("Codex_TerrariaInStardew_Balloon", "Terraria Balloon", "Stamina increased.", Buff.maxStamina,
                new BuffAttributesData { MaxStamina = 20 });
        if (itemKey.Contains("emblem", StringComparison.Ordinal))
            return new("Codex_TerrariaInStardew_Emblem", "Terraria Emblem", "Attack increased.", Buff.attack,
                new BuffAttributesData { Attack = 2 });
        if (itemKey.Contains("shield", StringComparison.Ordinal))
            return new("Codex_TerrariaInStardew_Shield", "Terraria Shield", "Defense increased.", Buff.defense,
                new BuffAttributesData { Defense = 2 });
        if (itemKey.Contains("magnet", StringComparison.Ordinal))
            return new("Codex_TerrariaInStardew_Magnet", "Terraria Magnet", "Item pickup radius increased.", Buff.magneticRadius,
                new BuffAttributesData { MagneticRadius = 128 });
        if (itemKey.Contains("lucky", StringComparison.Ordinal))
            return new("Codex_TerrariaInStardew_Luck", "Terraria Lucky Accessory", "Luck increased.", Buff.luck,
                new BuffAttributesData { LuckLevel = 1 });
        return new("Codex_TerrariaInStardew_Accessory", "Terraria Accessory", "Defense increased.", Buff.defense,
            new BuffAttributesData { Defense = 1 });
    }

    private static MethodInfo? FindMethod(Type type, string name, Type[]? parameterTypes = null)
    {
        for (Type? current = type; current is not null; current = current.BaseType)
        {
            MethodInfo? method = current.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase) &&
                    (parameterTypes is null || candidate.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(parameterTypes)));
            if (method is not null)
                return method;
        }
        return null;
    }

    private static string Translate(string key, object? tokens, string fallback) =>
        translations is null ? fallback : translations.Get(key, tokens).ToString();
}

internal static class RectangleExtensions
{
    public static Rectangle InflateCopy(this Rectangle rectangle, int amount) => new(
        rectangle.X - amount,
        rectangle.Y - amount,
        rectangle.Width + amount * 2,
        rectangle.Height + amount * 2);
}
