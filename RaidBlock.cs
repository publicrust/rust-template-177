using System;
using System.Collections.Generic;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using Rust;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("RaidBlock", "RustGPT", "1.0.7")]
    [Description("Adds a raid block system with a UI component to show the block duration, applying to all players in the raid zone.")]
    public class RaidBlock : RustPlugin
    {
        [PluginReference]
        private Plugin CombatBlock;

        private Dictionary<ulong, Dictionary<Vector3, Timer>> raidTimers = new Dictionary<ulong, Dictionary<Vector3, Timer>>();
        private Dictionary<ulong, HashSet<Vector3>> playerZones = new Dictionary<ulong, HashSet<Vector3>>();
        private List<RaidZone> activeRaidZones = new List<RaidZone>();
        private List<SphereEntity> activeDomes = new List<SphereEntity>();
        private Dictionary<ulong, RaidBlockUIManager> uiManagers = new Dictionary<ulong, RaidBlockUIManager>();
        private Dictionary<ulong, Dictionary<Vector3, float>> savedBlockTimes = new Dictionary<ulong, Dictionary<Vector3, float>>();
        private Dictionary<ulong, Dictionary<Vector3, float>> remainingTimes = new Dictionary<ulong, Dictionary<Vector3, float>>();

        private class PluginConfig
        {
            public float BlockDuration { get; set; }
            public bool BlockOnReceiveRaidDamage { get; set; }
            public bool RemoveBlockOnDeath { get; set; }
            public List<string> BlockedCommands { get; set; }
            public float RaidZoneRadius { get; set; }

            public bool IsSphereEnabled { get; set; } = true;
            public int SphereType { get; set; } = 0;
            public int DomeTransparencyLevel { get; set; } = 3;
            public float VisualMultiplier { get; set; } = 1.0f;
        }

        private PluginConfig config;

        private class RaidZone
        {
            public Vector3 Position { get; set; }
            public float ExpirationTime { get; set; }
        }

        private class RaidBlockUIManager
        {
            private const string UIPanel = "RaidBlock.UI";
            private const string UILabel = "RaidBlock.UI.Label";
            private const string UIProgress = "RaidBlock.UI.Progress";

            private readonly RaidBlock plugin;
            private readonly BasePlayer player;
            private readonly float maxDuration;

            public RaidBlockUIManager(RaidBlock plugin, BasePlayer player, float maxDuration)
            {
                this.plugin = plugin;
                this.player = player;
                this.maxDuration = maxDuration;
            }

            public void Create(float duration)
            {
                if (player == null || !player.IsConnected) 
                {
                    return;
                }

                Destroy();

                var container = new CuiElementContainer();
                try
                {
                    // Background panel
                    container.Add(new CuiPanel
                    {
                        Image = { Color = "0.97 0.92 0.88 0.16" },
                        RectTransform = { AnchorMin = "0.3447913 0.1135", AnchorMax = "0.640625 0.1435" },
                        CursorEnabled = false
                    }, "Hud", UIPanel);

                    AddLabel(container, duration);
                    AddProgressBar(container, duration);

                    CuiHelper.AddUi(player, container);
                }
                catch (Exception ex)
                {
                    plugin.Puts($"Error creating UI: {ex.Message}");
                }
            }

            public void Update(float duration)
            {
                if (player == null || !player.IsConnected)
                {
                    return;
                }

                try
                {
                    var container = new CuiElementContainer();
                    AddLabel(container, duration);
                    AddProgressBar(container, duration);

                    CuiHelper.DestroyUi(player, UIProgress);
                    CuiHelper.DestroyUi(player, UILabel);
                    CuiHelper.AddUi(player, container);
                }
                catch (Exception ex)
                {
                    plugin.Puts($"Error updating UI: {ex.Message}");
                }
            }

            public void Destroy()
            {
                if (player == null || !player.IsConnected) return;

                CuiHelper.DestroyUi(player, UIProgress);
                CuiHelper.DestroyUi(player, UILabel);
                CuiHelper.DestroyUi(player, UIPanel);
            }

            private void AddLabel(CuiElementContainer container, float duration)
            {
                try
                {
                    var message = plugin.GetMessage("RaidBlock.Active", player, (int)duration);
                    container.Add(new CuiElement
                    {
                        Name = UILabel,
                        Parent = UIPanel,
                        Components =
                        {
                            new CuiTextComponent 
                            { 
                                Text = message,
                                FontSize = 15, 
                                Align = TextAnchor.MiddleCenter,
                                Color = "1 1 1 0.5"
                            },
                            new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" }
                        }
                    });
                }
                catch (Exception ex)
                {
                    plugin.Puts($"Error adding label: {ex.Message}");
                }
            }

            private void AddProgressBar(CuiElementContainer container, float duration)
            {
                float progress = Mathf.Clamp01(duration / maxDuration);
                container.Add(new CuiElement
                {
                    Name = UIProgress,
                    Parent = UIPanel,
                    Components =
                    {
                        new CuiImageComponent { Color = "0.60 0.80 0.20 0.5" },
                        new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = $"{progress} 0.1" }
                    }
                });
            }
        }

        private RaidBlockUIManager GetOrCreateUIManager(BasePlayer player)
        {
            if (!uiManagers.TryGetValue(player.userID, out var ui))
            {
                ui = new RaidBlockUIManager(this, player, config.BlockDuration);
                uiManagers[player.userID] = ui;
            }
            return ui;
        }

        private void CreateRaidBlockUI(BasePlayer player, float duration)
        {
            if (player == null || !player.IsConnected) return;

            var ui = GetOrCreateUIManager(player);
            ui.Create(duration);
        }

        private void UpdateRaidBlockUI(BasePlayer player, float duration)
        {
            if (player == null || !player.IsConnected) return;

            var ui = GetOrCreateUIManager(player);
            ui.Update(duration);
        }

        private void DestroyRaidBlockUI(BasePlayer player)
        {
            if (player == null || !player.IsConnected) return;

            if (uiManagers.TryGetValue(player.userID, out var ui))
            {
                ui.Destroy();
                uiManagers.Remove(player.userID);
            }
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null) return;

            RemoveAllRaidBlocks(player);
            uiManagers.Remove(player.userID);
        }

        protected override void LoadDefaultConfig()
        {
            config = new PluginConfig
            {
                BlockDuration = 300.0f,
                BlockOnReceiveRaidDamage = true,
                RemoveBlockOnDeath = true,
                BlockedCommands = new List<string> { "/tpr", "/tpa", "/home" },
                RaidZoneRadius = 50.0f,
                IsSphereEnabled = true,
                SphereType = 0,
                DomeTransparencyLevel = 3,
                VisualMultiplier = 1.0f
            };
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            config = Config.ReadObject<PluginConfig>();
            if (config == null)
            {
                LoadDefaultConfig();
            }
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(config);
        }

        private void Init()
        {
            ClearAllRaidBlockUI();
        }

        private void Unload()
        {
            ClearAllRaidZonesAndDomes();
        }

        protected override void LoadDefaultMessages()
        {
            try
            {
                var ruMessages = new Dictionary<string, string>
                {
                    ["RaidBlock.Active"] = "Блокировка рейда: {0} сек",
                    ["RaidBlock.BlockedCommand"] = "Вы не можете использовать эту команду во время блокировки рейда",
                    ["RaidBlock.UIMessage"] = "Вы не можете использовать эту команду во время блокировки рейда",
                    ["RaidBlock.NoBuild"] = "Вы не можете строить в зоне рейда"
                };

                var enMessages = new Dictionary<string, string>
                {
                    ["RaidBlock.Active"] = "Raid Block: {0} sec",
                    ["RaidBlock.BlockedCommand"] = "You cannot use this command while in raid block",
                    ["RaidBlock.UIMessage"] = "You cannot use this command while in raid block",
                    ["RaidBlock.NoBuild"] = "You cannot build in the raid zone"
                };

                lang.RegisterMessages(ruMessages, this, "ru");
                lang.RegisterMessages(enMessages, this, "en");
            }
            catch (Exception ex)
            {
                Puts($"Error loading messages: {ex.Message}");
            }
        }

        private string GetMessage(string key, BasePlayer player = null, params object[] args)
        {
            return string.Format(lang.GetMessage(key, this, player?.UserIDString), args);
        }

        private void ClearAllRaidBlockUI()
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                DestroyRaidBlockUI(player);
            }
        }

        private void ClearAllRaidZonesAndDomes()
        {
            foreach (var dome in activeDomes)
            {
                if (dome != null && !dome.IsDestroyed)
                {
                    dome.Kill();
                }
            }
            activeDomes.Clear();
            activeRaidZones.Clear();
        }

        private void RemoveRaidBlock(BasePlayer player, Vector3 zonePosition, bool saveTime = false)
        {
            if (player == null) return;
            
            ulong playerId = player.userID;

            if (saveTime && raidTimers.ContainsKey(playerId) && raidTimers[playerId].ContainsKey(zonePosition))
            {
                if (!savedBlockTimes.ContainsKey(playerId))
                    savedBlockTimes[playerId] = new Dictionary<Vector3, float>();
                    
                if (remainingTimes.ContainsKey(playerId) && remainingTimes[playerId].ContainsKey(zonePosition))
                {
                    savedBlockTimes[playerId][zonePosition] = remainingTimes[playerId][zonePosition];
                }
            }
            
            if (raidTimers.ContainsKey(playerId) && raidTimers[playerId].ContainsKey(zonePosition))
            {
                raidTimers[playerId][zonePosition].Destroy();
                raidTimers[playerId].Remove(zonePosition);
                
                if (raidTimers[playerId].Count == 0)
                raidTimers.Remove(playerId);
            }
            
            if (playerZones.ContainsKey(playerId))
            {
                playerZones[playerId].Remove(zonePosition);
                if (playerZones[playerId].Count == 0)
                {
                    playerZones.Remove(playerId);
                    DestroyRaidBlockUI(player);
                }
                else
                {
                    // Обновляем UI с максимальным оставшимся временем
                    float maxTime = 0f;
                    Vector3 maxTimeZone = Vector3.zero;
                    foreach (var zone in playerZones[playerId])
                    {
                        if (remainingTimes.ContainsKey(playerId) && remainingTimes[playerId].ContainsKey(zone))
                        {
                            if (remainingTimes[playerId][zone] > maxTime)
                            {
                                maxTime = remainingTimes[playerId][zone];
                                maxTimeZone = zone;
                            }
                        }
                    }
                    if (maxTime > 0)
                        UpdateRaidBlockUI(player, maxTime);
                }
            }
            
            if (remainingTimes.ContainsKey(playerId))
            {
                remainingTimes[playerId].Remove(zonePosition);
                if (remainingTimes[playerId].Count == 0)
                    remainingTimes.Remove(playerId);
            }
        }

        private void AddRaidBlock(BasePlayer player, Vector3 zonePosition, float duration, bool checkSaved = true)
        {
            if (player == null)
            {
                return;
            }

            ulong playerId = player.userID;
            
            // Всегда используем новую длительность при взрыве
            if (!checkSaved)
            {
                duration = config.BlockDuration;
            }
            // Проверяем сохраненное время только если это не новый взрыв
            else if (checkSaved && savedBlockTimes.ContainsKey(playerId) && savedBlockTimes[playerId].ContainsKey(zonePosition))
            {
                duration = savedBlockTimes[playerId][zonePosition];
                savedBlockTimes[playerId].Remove(zonePosition);
                if (savedBlockTimes[playerId].Count == 0)
                    savedBlockTimes.Remove(playerId);
            }
            else
            {
                duration = config.BlockDuration;
            }

            try
            {
                if (!raidTimers.ContainsKey(playerId))
                    raidTimers[playerId] = new Dictionary<Vector3, Timer>();
                    
                if (!playerZones.ContainsKey(playerId))
                    playerZones[playerId] = new HashSet<Vector3>();
                    
                if (!remainingTimes.ContainsKey(playerId))
                    remainingTimes[playerId] = new Dictionary<Vector3, float>();

                // Уничтожаем существующий таймер
                if (raidTimers[playerId].ContainsKey(zonePosition))
                {
                    raidTimers[playerId][zonePosition].Destroy();
                    raidTimers[playerId].Remove(zonePosition);
                }

                playerZones[playerId].Add(zonePosition);
                remainingTimes[playerId][zonePosition] = duration;
                
                // Обновляем UI с максимальным временем среди всех зон
                float maxTime = duration;
                foreach (var time in remainingTimes[playerId].Values)
                {
                    if (time > maxTime)
                        maxTime = time;
                }
                
                if (playerZones[playerId].Count == 1)
                    CreateRaidBlockUI(player, maxTime);
                else
                    UpdateRaidBlockUI(player, maxTime);

                raidTimers[playerId][zonePosition] = timer.Repeat(1f, (int)duration, () =>
                {
                    if (remainingTimes[playerId][zonePosition] > 0)
                    {
                        remainingTimes[playerId][zonePosition]--;
                        
                        // Находим максимальное оставшееся время среди всех зон
                        float maxRemainingTime = 0f;
                        foreach (var time in remainingTimes[playerId].Values)
                        {
                            if (time > maxRemainingTime)
                                maxRemainingTime = time;
                        }
                        
                        UpdateRaidBlockUI(player, maxRemainingTime);
                    }
                    else
                    {
                        RemoveRaidBlock(player, zonePosition);
                    }
                });
            }
            catch (Exception ex)
            {
                Puts($"Error adding block to {player.displayName} for zone {zonePosition}: {ex.Message}");
            }
        }

        private bool IsPlayerInRaidZone(BasePlayer player, out List<Vector3> activeZones)
        {
            activeZones = new List<Vector3>();
            if (player == null) return false;

            bool inAnyZone = false;
            for (int i = activeRaidZones.Count - 1; i >= 0; i--)
            {
                var raidZone = activeRaidZones[i];
                if (Time.realtimeSinceStartup > raidZone.ExpirationTime)
                {
                    continue;
                }

                float distance = Vector3.Distance(player.transform.position, raidZone.Position);
                
                if (distance <= config.RaidZoneRadius/2)
                {
                    activeZones.Add(raidZone.Position);
                    inAnyZone = true;
                }
            }

            return inAnyZone;
        }

        private void CheckPlayerInZone(BasePlayer player)
        {
            if (player == null) return;
            
            List<Vector3> activeZones;
            bool inZone = IsPlayerInRaidZone(player, out activeZones);
            
            ulong playerId = player.userID;
            
            if (!playerZones.ContainsKey(playerId))
                playerZones[playerId] = new HashSet<Vector3>();
                
            // Проверяем каждую активную зону
            foreach (var zonePos in activeZones)
            {
                bool hasBlockInRange = false;
                foreach (var playerZonePos in playerZones[playerId])
                {
                    if (Vector3.Distance(playerZonePos, zonePos) <= config.RaidZoneRadius/2)
                    {
                        hasBlockInRange = true;
                        break;
                    }
                }

                if (!hasBlockInRange)
                {
                    AddRaidBlock(player, zonePos, config.BlockDuration);
                }
            }
            
            // Проверяем зоны, в которых игрок уже заблокирован
            var zonesToRemove = new List<Vector3>();
            if (playerZones.ContainsKey(playerId))
            {
                foreach (var playerZonePos in playerZones[playerId])
                {
                    bool isInAnyActiveZone = false;
                    foreach (var activeZonePos in activeZones)
                    {
                        if (Vector3.Distance(playerZonePos, activeZonePos) <= config.RaidZoneRadius/2)
                        {
                            isInAnyActiveZone = true;
                            break;
                        }
                    }

                    if (!isInAnyActiveZone)
                    {
                        zonesToRemove.Add(playerZonePos);
                    }
                }
            }
            
            // Удаляем блоки для зон, в которых игрока больше нет
            foreach (var zonePos in zonesToRemove)
            {
                RemoveRaidBlock(player, zonePos, true);
            }
        }

        private void CreateRaidZone(Vector3 position)
        {
            // Проверяем, находится ли новая точка в радиусе существующей зоны
            RaidZone existingZone = null;
            foreach (var zone in activeRaidZones)
            {
                if (Vector3.Distance(zone.Position, position) <= config.RaidZoneRadius/2)
                {
                    existingZone = zone;
                    break;
                }
            }

            if (existingZone != null)
            {
                // Обновляем время существующей зоны
                existingZone.ExpirationTime = Time.realtimeSinceStartup + config.BlockDuration;
                return;
            }

            var raidZone = new RaidZone
            {
                Position = position,
                ExpirationTime = Time.realtimeSinceStartup + config.BlockDuration
            };

            activeRaidZones.Add(raidZone);

            if (config.IsSphereEnabled)
            {
                bool isInExistingDome = false;
                foreach (var dome in activeDomes)
                {
                    if (dome != null && !dome.IsDestroyed && Vector3.Distance(dome.transform.position, position) <= config.RaidZoneRadius/2)
                    {
                        isInExistingDome = true;
                        break;
                    }
                }

                if (!isInExistingDome)
            {
                CreateDome(position);
                }
            }
        }

        private void CreateDome(Vector3 position)
        {
            // Проверяем, находится ли новая точка в радиусе существующего купола
            foreach (var dome in activeDomes)
            {
                if (dome != null && !dome.IsDestroyed)
                {
                    float distance = Vector3.Distance(dome.transform.position, position);
                    if (distance <= config.RaidZoneRadius/2)
                        return;
                }
            }

            string spherePrefab = config.SphereType == 0 ? "assets/prefabs/visualization/sphere.prefab" : "assets/prefabs/visualization/sphere_battleroyale.prefab";

            SphereEntity sphere = GameManager.server.CreateEntity(spherePrefab, position) as SphereEntity;
            if (sphere != null)
            {
                sphere.enableSaving = false;
                sphere.currentRadius = config.RaidZoneRadius;
                sphere.lerpRadius = config.RaidZoneRadius;
                sphere.lerpSpeed = 1f;
                
                // Устанавливаем размер сферы равным диаметру зоны
                float visualScale = config.RaidZoneRadius * 2f * config.VisualMultiplier;
                sphere.transform.localScale = Vector3.one * visualScale;
                sphere.UpdateScale();

                sphere.Spawn();
                activeDomes.Add(sphere);
            }
        }

        private void OnPlayerDeath(BasePlayer player, HitInfo info)
        {
            if (player == null || player.userID == 0 || string.IsNullOrEmpty(player.displayName)) return;
            
            if (config.RemoveBlockOnDeath)
            {
                RemoveAllRaidBlocks(player);
            }
        }

        private void RemoveAllRaidBlocks(BasePlayer player)
        {
            if (player == null) return;
            
            ulong playerId = player.userID;
            
            if (playerZones.ContainsKey(playerId))
            {
                var zones = new List<Vector3>(playerZones[playerId]);
                foreach (var zonePos in zones)
                {
                    RemoveRaidBlock(player, zonePos, false);
                }
            }
            
            DestroyRaidBlockUI(player);
        }

        private object OnPlayerChat(BasePlayer player, string message, ConVar.Chat.ChatChannel channel)
        {
            if (player == null || string.IsNullOrEmpty(message)) return null;

            if (remainingTimes.ContainsKey(player.userID) && remainingTimes[player.userID].Count > 0)
            {
                if (config.BlockedCommands.Exists(cmd => cmd != null && message.StartsWith(cmd, StringComparison.OrdinalIgnoreCase)))
                {
                    player.ChatMessage(GetMessage("RaidBlock.BlockedCommand", player));
                    return false;
                }
            }

            return null;
        }

        private object OnUserCommand(IPlayer player, string command, string[] args)
        {
            if (player?.Object == null || string.IsNullOrEmpty(command)) return null;
            
            var basePlayer = player.Object as BasePlayer;
            if (basePlayer == null) return null;

            if (remainingTimes.ContainsKey(basePlayer.userID) && remainingTimes[basePlayer.userID].Count > 0)
            {
                command = "/" + command.ToLower();
                if (config.BlockedCommands.Contains(command))
                {
                    basePlayer.ChatMessage(GetMessage("RaidBlock.UIMessage", basePlayer));
                    return false;
                }
            }

            return null;
        }

        [HookMethod("HasRaidBlock")]
        public bool HasRaidBlock(ulong playerID)
        {
            return remainingTimes.ContainsKey(playerID);
        }

        private void OnServerInitialized(bool initial)
        {
            LoadConfig();
            if (config.RaidZoneRadius <= 0)
            {
                config.RaidZoneRadius = 50.0f;
                SaveConfig();
            }
            if (config.BlockDuration <= 0)
            {
                config.BlockDuration = 300.0f;
                SaveConfig();
            }
            if (config.VisualMultiplier <= 0)
            {
                config.VisualMultiplier = 1.0f;
                SaveConfig();
            }
            
            timer.Every(1f, () =>
            {
                var expiredZones = activeRaidZones.RemoveAll(zone => Time.realtimeSinceStartup > zone.ExpirationTime);

                foreach (var player in BasePlayer.activePlayerList)
                {
                    if (player == null) continue;
                    CheckPlayerInZone(player);
                }
            });
        }

        private void OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (info?.damageTypes?.GetMajorityDamageType() == DamageType.Decay)
            {
                return;
            }

            if (entity == null || info == null || info.Initiator == null || info.damageTypes == null || entity.transform == null)
            {
                return;
            }

            BasePlayer attacker = info.Initiator as BasePlayer;
            if (attacker == null)
            {
                return;
            }

            // Проверяем, что у объекта есть владелец и что он будет разрушен этим уроном
            if (entity.OwnerID != 0 && (entity.Health() <= info.damageTypes.Total()))
            {
                BasePlayer victim = entity as BasePlayer;

                if (info.damageTypes.Has(DamageType.Explosion) || info.damageTypes.Has(DamageType.Bullet))
                {
                    Vector3 damagePosition = entity.transform.position;
                    RaidZone existingZone = null;

                    // Проверяем, находится ли точка урона в существующей зоне
                    foreach (var zone in activeRaidZones)
                    {
                        float distance = Vector3.Distance(zone.Position, damagePosition);
                        
                        if (distance <= config.RaidZoneRadius/2)
                        {
                            existingZone = zone;
                            zone.ExpirationTime = Time.realtimeSinceStartup + config.BlockDuration;

                            // Обновляем блоки всех игроков в этой зоне
                            foreach (var playerEntry in playerZones)
                            {
                                var player = BasePlayer.FindByID(playerEntry.Key);
                                if (player != null)
                                {
                                    foreach (var pos in playerEntry.Value)
                                    {
                                        float playerZoneDistance = Vector3.Distance(pos, zone.Position);
                                        
                                        if (playerZoneDistance <= config.RaidZoneRadius/2)
                                        {
                                            // Обновляем блок с полным временем
                                            AddRaidBlock(player, zone.Position, config.BlockDuration, false);
                                            break;
                                        }
                                    }
                                }
                            }
                            break;
                        }
                    }

                    // Создаем новую зону только если точка урона не в существующей зоне
                    if (existingZone == null)
                    {
                        CreateRaidZone(damagePosition);
                        existingZone = activeRaidZones[activeRaidZones.Count - 1];
                    }

                    // Добавляем блок атакующему
                    AddRaidBlock(attacker, existingZone.Position, config.BlockDuration, false);

                    // Добавляем блок жертве, если это игрок
                    if (victim != null && config.BlockOnReceiveRaidDamage)
                    {
                        AddRaidBlock(victim, existingZone.Position, config.BlockDuration, false);
                    }
                }
            }
        }

        private object CanBuild(Planner planner, Construction prefab, Construction.Target target)
        {
            if (planner == null || prefab == null) return null;

            BasePlayer player = planner.GetOwnerPlayer();
            if (player == null) return null;

            // Получаем позицию строительства
            Vector3 buildPosition;
            
            // Если есть привязка к существующему объекту
            if (target.entity != null)
            {
                buildPosition = target.entity.transform.position;
            }
            else
            {
                buildPosition = target.position;
            }
            
            foreach (var raidZone in activeRaidZones)
            {
                if (Time.realtimeSinceStartup > raidZone.ExpirationTime)
                    continue;

                float distance = Vector3.Distance(buildPosition, raidZone.Position);
                
                if (distance <= config.RaidZoneRadius/2)
                {
                    player.ChatMessage(GetMessage("RaidBlock.NoBuild", player));
                    return false;
                }
            }

            return null;
        }

        private object OnStructureUpgrade(BuildingBlock block, BasePlayer player, BuildingGrade.Enum grade, ulong skinID)
        {
            if (block == null || player == null) return null;

            Vector3 upgradePosition = block.transform.position;
            
            foreach (var raidZone in activeRaidZones)
            {
                if (Time.realtimeSinceStartup > raidZone.ExpirationTime)
                    continue;

                float distance = Vector3.Distance(upgradePosition, raidZone.Position);
                
                if (distance <= config.RaidZoneRadius/2)
                {
                    player.ChatMessage(GetMessage("RaidBlock.NoBuild", player));
                    return false;
                }
            }

            return null;
        }
    }
}