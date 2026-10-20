using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.DataStructures;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;

namespace TerrariaBridge;

internal static class SceneFusion
{
    internal const int Width = 640, ActorHeight = 360, Height = 408;
    internal const int AnchorX = 320, AnchorY = 220;
    internal static bool Enabled;
    internal static Vector2 HostFoot, HostAim, HostVelocity, HostOrigin, GuestOrigin;
    internal static string Location = "";
    internal static int Facing = 1;
    internal static long PoseSequence, Frames;
    internal static string LastError = "";
    private static RenderTarget2D? target;
    private static SpriteBatch? batch;
    private static Player? sessionPlayer;
    private static DrawData[] drawData = Array.Empty<DrawData>();
    private static Vector2 drawnFoot;
    internal static Vector2 GuestFoot => GuestOrigin + (HostFoot - HostOrigin) / 2f;

    internal static void ReadControls(JsonElement payload)
    {
        bool next = payload.TryGetProperty("scene", out var mode) && mode.GetBoolean();
        if (!next || Main.gameMenu || Main.dedServ) { End(); return; }
        var player = Main.LocalPlayer;
        var foot = new Vector2(Number(payload, "host_x"), Number(payload, "host_y"));
        string location = payload.TryGetProperty("host_location", out var name) ? name.GetString() ?? "" : "";
        if (!Enabled || Location != location || sessionPlayer != player)
        {
            if (!Enabled || sessionPlayer != player) sessionPlayer = player;
            GuestOrigin = player.Bottom; HostOrigin = foot;
        }
        Enabled = true; Location = location; HostFoot = foot;
        HostAim = new Vector2(Number(payload, "aim_x"), Number(payload, "aim_y"));
        HostVelocity = new Vector2(Number(payload, "host_vx"), Number(payload, "host_vy"));
        bool left = payload.TryGetProperty("left", out var leftValue) && leftValue.GetBoolean();
        bool right = payload.TryGetProperty("right", out var rightValue) && rightValue.GetBoolean();
        bool firing = payload.TryGetProperty("fire", out var fireValue) && fireValue.GetBoolean();
        // Use the held movement key first: host collision or a blocked step
        // can make position differencing report zero even while the user is
        // intentionally turning. Mouse aim remains useful while attacking or
        // standing still, and measured motion covers external host movement.
        if (left != right)
            Facing = left ? -1 : 1;
        else if (MathF.Abs(HostVelocity.X) > 0.05f)
            Facing = HostVelocity.X < 0 ? -1 : 1;
        else if (firing && MathF.Abs(HostAim.X) > 0.05f)
            Facing = HostAim.X < 0 ? -1 : 1;
        PoseSequence = payload.TryGetProperty("pose_sequence", out var pose) ? pose.GetInt64() : 0;
    }

    private static float Number(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.TryGetSingle(out var number) && float.IsFinite(number) ? number : 0;

    internal static void CacheDrawData(ref PlayerDrawSet info)
    {
        if (!Enabled || info.drawPlayer.whoAmI != Main.myPlayer || info.shadow != 0f) return;
        var player = info.drawPlayer;
        Texture2D? heldItemTexture = null;
        if (!player.HeldItem.IsAir)
        {
            Main.instance.LoadItem(player.HeldItem.type);
            heldItemTexture = TextureAssets.Item[player.HeldItem.type].Value;
        }
        drawData = info.DrawDataCache.Select(source =>
        {
            var data = source;
            if (heldItemTexture != null && ReferenceEquals(data.texture, heldItemTexture))
            {
                // Held items are drawn with world-light tint even when the
                // player layers use DrawEffects full-bright. Preserve the
                // sprite alpha and dye shader, while removing only that tint.
                data.color = new Color(255, 255, 255, data.color.A);
            }
            return data;
        }).ToArray();
        drawnFoot = info.Position + new Vector2(info.drawPlayer.width / 2f, info.drawPlayer.height) - Main.screenPosition;
    }

    internal static void End()
    {
        if (Enabled && sessionPlayer != null) { sessionPlayer.Bottom = GuestFoot; sessionPlayer.velocity = Vector2.Zero; }
        Enabled = false; Location = ""; sessionPlayer = null; drawData = Array.Empty<DrawData>();
    }
    internal static void Dispose()
    {
        End(); target?.Dispose(); target = null; batch?.Dispose(); batch = null;
    }

    internal static byte[]? Capture()
    {
        if (!Enabled || Main.gameMenu || Main.LocalPlayer.dead || drawData.Length == 0) return null;
        var graphics = Main.instance.GraphicsDevice;
        target ??= new RenderTarget2D(graphics, Width, Height, false, SurfaceFormat.Color, DepthFormat.None);
        batch ??= new SpriteBatch(graphics);
        var previousTargets = graphics.GetRenderTargets();
        var viewport = graphics.Viewport;
        bool begun = false;
        try
        {
            graphics.SetRenderTarget(target); graphics.Clear(Color.Transparent);
            batch.Begin(
                SpriteSortMode.Deferred,
                BlendState.AlphaBlend,
                SamplerState.PointClamp,
                DepthStencilState.None,
                RasterizerState.CullNone); begun = true;
            var offset = new Vector2(AnchorX, AnchorY) - drawnFoot;
            foreach (var source in drawData)
            {
                var data = source; data.position += offset;
                if (data.useDestinationRectangle)
                {
                    var rect = data.destinationRectangle; rect.Offset((int)offset.X, (int)offset.Y); data.destinationRectangle = rect;
                }
                data.Draw(batch);
            }
            var player = Main.LocalPlayer;
            // Actual guest projectile state and textures, in the same foot-centered mapping.
            foreach (var projectile in Main.projectile.Take(Main.maxProjectiles))
            {
                if (!projectile.active || projectile.owner != Main.myPlayer || projectile.hide ||
                    Vector2.DistanceSquared(projectile.Center, player.Bottom) > 350 * 350) continue;
                var texture = TextureAssets.Projectile[projectile.type].Value;
                int count = Math.Max(1, Main.projFrames[projectile.type]);
                var source = new Rectangle(0, texture.Height / count * projectile.frame, texture.Width, texture.Height / count);
                batch.Draw(texture, projectile.Center - player.Bottom + new Vector2(AnchorX, AnchorY), source,
                    projectile.GetAlpha(Color.White), projectile.rotation, new Vector2(source.Width, source.Height) / 2f,
                    projectile.scale, projectile.spriteDirection < 0 ? SpriteEffects.FlipHorizontally : SpriteEffects.None, 0);
            }
            var pixel = TextureAssets.MagicPixel.Value;
            for (int i = 0; i < 10; i++)
            {
                var slot = new Rectangle(i * 40 + 2, ActorHeight + 5, 36, 38);
                batch.Draw(pixel, slot, i == player.selectedItem ? new Color(230, 187, 91, 230) : new Color(28, 31, 38, 220));
                batch.Draw(pixel, new Rectangle(slot.X + 2, slot.Y + 2, slot.Width - 4, slot.Height - 4),
                    i == player.selectedItem ? new Color(67, 53, 41, 245) : new Color(12, 15, 21, 235));
                var item = player.inventory[i]; if (item.IsAir) continue;
                Main.instance.LoadItem(item.type); var texture = TextureAssets.Item[item.type].Value;
                var source = Main.itemAnimations[item.type]?.GetFrame(texture) ?? texture.Bounds;
                float scale = Math.Min(30f / source.Width, 30f / source.Height);
                batch.Draw(texture, new Vector2(i * 40 + 20, ActorHeight + 24), source, Color.White, 0,
                    new Vector2(source.Width, source.Height) / 2f, scale, SpriteEffects.None, 0);
            }
            batch.End(); begun = false; graphics.SetRenderTargets(previousTargets);
            var pixels = new Color[Width * Height]; target.GetData(pixels);
            var metadata = JsonSerializer.SerializeToUtf8Bytes(new
            {
                kind = "scene", width = Width, height = Height, actor_height = ActorHeight,
                anchor_x = AnchorX, anchor_y = AnchorY, pose_sequence = PoseSequence, location = Location,
                owner = player.whoAmI,
                host_x = HostFoot.X, host_y = HostFoot.Y, life = player.statLife, life_max = player.statLifeMax2,
                selected = player.selectedItem, item_animation = player.itemAnimation,
                selected_item = DescribeItem(player, player.HeldItem),
                hotbar = player.inventory.Take(10).Select(i => new { type = i.type, name = i.Name, stack = i.stack }).ToArray()
            });
            using var output = new MemoryStream(); using var writer = new BinaryWriter(output);
            writer.Write(new byte[] { 83, 68, 84, 76 }); // SDTL, then version and metadata length, little endian.
            writer.Write(1); writer.Write(metadata.Length); writer.Write(metadata);
            writer.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels.AsSpan()));
            Frames++; LastError = ""; return output.ToArray();
        }
        catch (Exception ex) { LastError = ex.GetType().Name + ": " + ex.Message; return null; }
        finally { if (begun) batch.End(); graphics.SetRenderTargets(previousTargets); graphics.Viewport = viewport; }
    }

    private static object DescribeItem(Player player, Item item)
    {
        string id = ContentSamples.ItemPersistentIdsByNetIds.TryGetValue(item.type, out string? persistentId)
            ? persistentId
            : item.type.ToString();
        Item? ammo = item.useAmmo == AmmoID.None ? null : player.ChooseAmmo(item);
        int ammoSlot = ammo is null || ammo.IsAir
            ? -1
            : Array.FindIndex(player.inventory, candidate => ReferenceEquals(candidate, ammo));
        string ammoId = ammo is not null && !ammo.IsAir && ContentSamples.ItemPersistentIdsByNetIds.TryGetValue(ammo.type, out string? ammoPersistentId)
            ? ammoPersistentId
            : "";
        Item? fishingBait = item.fishingPole > 0
            ? player.inventory.FirstOrDefault(candidate => !candidate.IsAir && candidate.bait > 0)
            : null;
        int fishingBaitSlot = fishingBait is null
            ? -1
            : Array.FindIndex(player.inventory, candidate => ReferenceEquals(candidate, fishingBait));
        string fishingBaitId = fishingBait is not null && ContentSamples.ItemPersistentIdsByNetIds.TryGetValue(fishingBait.type, out string? fishingBaitPersistentId)
            ? fishingBaitPersistentId
            : "";
        bool melee = item.CountsAsClass(DamageClass.Melee);
        bool magic = item.CountsAsClass(DamageClass.Magic);
        bool summon = item.CountsAsClass(DamageClass.Summon);
        bool thrown = item.consumable && item.damage > 0 && item.shoot != ProjectileID.None &&
            item.useAmmo == AmmoID.None && !melee && !magic && !summon;
        return new
        {
            id,
            type = item.type,
            name = item.Name,
            stack = item.stack,
            use_time = Math.Max(1, item.useTime),
            auto_reuse = item.autoReuse,
            pick = item.pick,
            axe = item.axe,
            hammer = item.hammer,
            defense = item.defense,
            damage = item.damage,
            // Shortswords and some other melee weapons use a projectile for
            // their hitbox, so noMelee must not hide their damage class.
            melee,
            projectile = item.shoot != ProjectileID.None &&
                (item.CountsAsClass(DamageClass.Ranged) || magic) && !summon && !thrown,
            thrown,
            magic,
            summon,
            uses_ammo = item.useAmmo != AmmoID.None,
            ammo_item = item.ammo != AmmoID.None,
            ammo_id = ammoId,
            ammo_slot = ammoSlot,
            mana = item.mana,
            heal_life = item.healLife,
            heal_mana = item.healMana,
            buff_id = item.buffType > 0 ? BuffID.Search.GetName(item.buffType) : "",
            buff_time = item.buffTime,
            create_tile = item.createTile,
            create_wall = item.createWall,
            fishing_pole = item.fishingPole,
            bait = item.bait,
            accessory = item.accessory,
            mount_type = item.mountType,
            consumable = item.consumable,
            fishing_bait_id = fishingBaitId,
            fishing_bait_slot = fishingBaitSlot,
            grappling_hook = IsGrapplingHookId(id, item.shoot)
        };
    }

    private static bool IsGrapplingHookId(string persistentId, int projectileType)
    {
        if (projectileType <= ProjectileID.None)
            return false;

        string id = new string(persistentId.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return id.EndsWith("hook", StringComparison.Ordinal) || id.EndsWith("skeletronhand", StringComparison.Ordinal);
    }
}

public sealed class SceneFusionPlayer : ModPlayer
{
    private const string ConsumedActionsKey = "sdtv-consumed-scene-item-actions";
    private readonly List<string> consumedActionOrder = new();
    private readonly HashSet<string> consumedActionIds = new(StringComparer.Ordinal);
    private bool displayModeSaved;
    private bool savedDisplayMode;
    private bool Active => !Main.dedServ && SceneFusion.Enabled && Player.whoAmI == Main.myPlayer;

    public override void LoadData(TagCompound tag)
    {
        consumedActionOrder.Clear();
        consumedActionIds.Clear();
        if (!tag.ContainsKey(ConsumedActionsKey))
            return;
        foreach (string actionId in tag.GetList<string>(ConsumedActionsKey))
        {
            if (consumedActionIds.Add(actionId))
                consumedActionOrder.Add(actionId);
        }
    }

    public override void SaveData(TagCompound tag) => tag[ConsumedActionsKey] = consumedActionOrder.ToList();

    internal bool TryConsumeSceneItem(string actionId, int slot, string expectedItemId, bool consumeItem, bool consumeAmmo, int ammoSlot, string expectedAmmoId, bool consumeBait, int baitSlot, string expectedBaitId)
    {
        if (string.IsNullOrWhiteSpace(actionId) || string.IsNullOrWhiteSpace(expectedItemId))
            return false;
        if (consumedActionIds.Contains(actionId))
            return true;
        if ((!consumeItem && !consumeAmmo && !consumeBait) || slot < 0 || slot >= Player.inventory.Length)
            return false;

        Item item = Player.inventory[slot];
        if (item.IsAir || item.stack <= 0 ||
            !ContentSamples.ItemPersistentIdsByNetIds.TryGetValue(item.type, out string? persistentId) ||
            !string.Equals(persistentId, expectedItemId, StringComparison.Ordinal))
            return false;

        Item? ammo = null;
        if (consumeAmmo)
        {
            if (item.useAmmo == AmmoID.None || ammoSlot < 0 || ammoSlot >= Player.inventory.Length || ammoSlot == slot)
                return false;
            ammo = Player.inventory[ammoSlot];
            if (ammo.IsAir || ammo.stack <= 0 ||
                !ContentSamples.ItemPersistentIdsByNetIds.TryGetValue(ammo.type, out string? ammoPersistentId) ||
                !string.Equals(ammoPersistentId, expectedAmmoId, StringComparison.Ordinal))
                return false;

            Item? selectedAmmo = Player.ChooseAmmo(item);
            if (selectedAmmo is null || selectedAmmo.IsAir || !ReferenceEquals(selectedAmmo, ammo))
                return false;
        }

        Item? bait = null;
        if (consumeBait)
        {
            if (item.fishingPole <= 0 || baitSlot < 0 || baitSlot >= Player.inventory.Length || baitSlot == slot)
                return false;
            bait = Player.inventory[baitSlot];
            if (bait.IsAir || bait.stack <= 0 || bait.bait <= 0 ||
                !ContentSamples.ItemPersistentIdsByNetIds.TryGetValue(bait.type, out string? baitPersistentId) ||
                !string.Equals(baitPersistentId, expectedBaitId, StringComparison.Ordinal))
                return false;
            Item? selectedBait = Player.inventory.FirstOrDefault(candidate => !candidate.IsAir && candidate.bait > 0);
            if (selectedBait is null || !ReferenceEquals(selectedBait, bait))
                return false;
        }

        if (consumeAmmo && !ConsumeSceneAmmo(item))
            return false;

        if (consumeBait && bait is not null)
        {
            if (bait.stack == 1)
                bait.TurnToAir();
            else
                bait.stack--;
        }

        if (consumeItem)
        {
            if (item.stack == 1)
                item.TurnToAir();
            else
                item.stack--;
        }
        consumedActionIds.Add(actionId);
        consumedActionOrder.Add(actionId);
        if (consumedActionOrder.Count > 2048)
        {
            consumedActionIds.Remove(consumedActionOrder[0]);
            consumedActionOrder.RemoveAt(0);
        }
        return true;
    }

    private bool ConsumeSceneAmmo(Item weapon)
    {
        int oldAnimation = Player.itemAnimation;
        int oldAnimationMax = Player.itemAnimationMax;
        int oldUseCount = Player.ItemUsesThisAnimation;
        FieldInfo? useCountField = typeof(Player).GetField("<ItemUsesThisAnimation>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
        try
        {
            int useTime = CombinedHooks.TotalUseTime(weapon.useTime, Player, weapon);
            Player.itemAnimation = Math.Max(weapon.useAnimation, useTime + 1);
            Player.itemAnimationMax = Player.itemAnimation;
            if (weapon.useLimitPerAnimation.HasValue)
                useCountField?.SetValue(Player, Math.Max(0, weapon.useLimitPerAnimation.Value - 1));

            int projectile;
            float speed;
            int damage;
            float knockback;
            int usedAmmoItemId;
            return Player.PickAmmo(weapon, out projectile, out speed, out damage, out knockback, out usedAmmoItemId, false);
        }
        finally
        {
            Player.itemAnimation = oldAnimation;
            Player.itemAnimationMax = oldAnimationMax;
            useCountField?.SetValue(Player, oldUseCount);
        }
    }

    public override void TransformDrawData(ref PlayerDrawSet drawInfo) => SceneFusion.CacheDrawData(ref drawInfo);
    public override void DrawEffects(PlayerDrawSet drawInfo, ref float r, ref float g, ref float b, ref float a, ref bool fullBright)
    {
        if (!Active || drawInfo.shadow != 0f)
            return;

        // DrawEffects runs before the player layers calculate their DrawData
        // colors. Request full brightness here so a backgrounded Terraria
        // client cannot turn the composited actor into a black silhouette.
        fullBright = true;
    }
    public override void PostUpdateEquips()
    {
        if (!Active)
        {
            if (displayModeSaved)
            {
                Player.isDisplayDollOrInanimate = savedDisplayMode;
                displayModeSaved = false;
            }
            return;
        }
        if (!displayModeSaved)
        {
            savedDisplayMode = Player.isDisplayDollOrInanimate;
            displayModeSaved = true;
        }
        // The actor is composited into Stardew rather than the Terraria world;
        // avoid copying Terraria's inactive-client/world lighting into it.
        Player.isDisplayDollOrInanimate = true;
        Player.gravity = 0; Player.noFallDmg = true;
    }
    public override void PreUpdateMovement()
    {
        if (!Active) return; Player.Bottom = SceneFusion.GuestFoot; Player.velocity = Vector2.Zero; Player.direction = SceneFusion.Facing;
    }
    public override void PostUpdate()
    {
        if (!Active) return; Player.Bottom = SceneFusion.GuestFoot; Player.velocity = Vector2.Zero; Player.direction = SceneFusion.Facing;
        Player.legFrame.Y = SceneFusion.HostVelocity.LengthSquared() > 0.1f
            ? (6 + (int)(Main.GameUpdateCount / 5 % 13)) * Player.legFrame.Height : 0;
    }
    public override bool CanBeHitByNPC(NPC npc, ref int cooldownSlot) => !Active;
    public override bool CanBeHitByProjectile(Projectile projectile) => !Active;
}
