/// <summary>
/// CombatBlock Plugin
/// 
/// This plugin adds a combat block system to Rust servers, preventing players from using certain commands during combat.
/// It displays a UI component to show the remaining duration of the combat block.
/// 
/// Configuration:
/// - BlockDuration (float): Duration of the combat block in seconds.
/// - BlockOnPlayerHit (bool): Apply combat block when hitting another player.
/// - BlockOnReceiveDamage (bool): Apply combat block when receiving damage from another player.
/// - RemoveBlockOnDeath (bool): Remove combat block when the player dies.
/// - BlockedCommands (List<string>): List of chat commands that should be blocked during combat block.
/// 
/// API:
/// - HasCombatBlock(ulong playerID): Returns a boolean indicating whether the specified player has an active combat block.
/// 
/// For more information, visit the plugin's page: https://foxplugins.ru/resources/combatblock.105/
/// Discord: https://discord.gg/sv6nF3gNU3
/// RustGPT: https://chatgpt.com/g/g-xunzDbv9b-rustgpt
/// Image: https://i.imgur.com/VQB20VZ_d.webp?maxwidth=760&fidelity=grand
/// </summary>

using System;
using System.Collections.Generic;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("CombatBlock", "RustGPT", "1.0.0")]
    [Description("Adds a combat block system with a UI component to show the block duration.")]
    public class CombatBlock : RustPlugin
    {
        private const string CombatBlockUI = "CombatBlockUI";
        private const string CombatBlockProgress = "CombatBlockProgress";
        private Dictionary<ulong, Timer> combatTimers = new Dictionary<ulong, Timer>();
        private HashSet<ulong> blockedPlayers = new HashSet<ulong>();

        /// <summary>
        /// Configuration class for the CombatBlock plugin
        /// </summary>
        private class PluginConfig
        {
            /// <summary>
            /// Duration of the combat block in seconds
            /// </summary>
            public float BlockDuration { get; set; }

            /// <summary>
            /// Determines if combat block should be applied when hitting another player
            /// </summary>
            public bool BlockOnPlayerHit { get; set; }

            /// <summary>
            /// Determines if combat block should be applied when receiving damage from another player
            /// </summary>
            public bool BlockOnReceiveDamage { get; set; }

            /// <summary>
            /// Determines if combat block should be removed upon player death
            /// </summary>
            public bool RemoveBlockOnDeath { get; set; }

            /// <summary>
            /// List of chat commands to block during combat block
            /// </summary>
            public List<string> BlockedCommands { get; set; }
        }

        private PluginConfig config;

        /// <summary>
        /// Loads the default configuration settings
        /// </summary>
        protected override void LoadDefaultConfig()
        {
            config = new PluginConfig
            {
                BlockDuration = 10.0f,
                BlockOnPlayerHit = true,
                BlockOnReceiveDamage = true,
                RemoveBlockOnDeath = true,
                BlockedCommands = new List<string> { "/tpr", "/tpa", "/home" }
            };
            SaveConfig();
        }

        /// <summary>
        /// Loads the configuration from file
        /// </summary>
        protected override void LoadConfig()
        {
            base.LoadConfig();
            config = Config.ReadObject<PluginConfig>();
            if (config == null)
            {
                LoadDefaultConfig();
            }
        }

        /// <summary>
        /// Saves the current configuration to file
        /// </summary>
        protected override void SaveConfig()
        {
            Config.WriteObject(config);
        }

        /// <summary>
        /// Initializes the plugin, clearing all existing UI elements and registering commands
        /// </summary>
        private void Init()
        {
            ClearAllCombatBlockUI();
        }

        /// <summary>
        /// Loads default language messages for supported languages
        /// </summary>
        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["CombatBlock.Active"] = "Блокировка: {0} секунд",
                ["CombatBlock.BlockedCommand"] = "Вы не можете использовать эту команду во время боевой блокировки.",
                ["CombatBlock.UIMessage"] = "Вы не можете использовать эту команду, пока в боевой блокировке."
            }, this, "ru");

            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["CombatBlock.Active"] = "Combat Block: {0} seconds",
                ["CombatBlock.BlockedCommand"] = "You cannot use this command while in combat block.",
                ["CombatBlock.UIMessage"] = "You cannot use this command while in combat block."
            }, this, "en");
        }

        /// <summary>
        /// Retrieves a localized message for the specified player
        /// </summary>
        /// <param name="key">The key of the message</param>
        /// <param name="player">The player to retrieve the message for</param>
        /// <param name="args">Arguments to format into the message</param>
        /// <returns>The localized and formatted message</returns>
        private string GetMessage(string key, BasePlayer player = null, params object[] args)
        {
            return string.Format(lang.GetMessage(key, this, player?.UserIDString), args);
        }

        /// <summary>
        /// Clears all combat block UI elements from all active players
        /// </summary>
        private void ClearAllCombatBlockUI()
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                DestroyCombatBlockUI(player);
            }
        }

        /// <summary>
        /// Adds a combat block to a player for a specified duration
        /// </summary>
        /// <param name="player">The player to apply the combat block to</param>
        /// <param name="duration">The duration of the combat block in seconds</param>
        private void AddCombatBlock(BasePlayer player, float duration)
        {
            if (player == null || !player.IsConnected) return;
            
            ulong playerId = player.userID;

            // Если у игрока уже есть блокировка, обновляем только таймер
            if (blockedPlayers.Contains(playerId))
            {
                if (combatTimers.ContainsKey(playerId))
                {
                    combatTimers[playerId].Destroy();
                    combatTimers.Remove(playerId);
                }
            }
            else
            {
                // Только если игрок не был заблокирован, добавляем его в список и создаем UI
                blockedPlayers.Add(playerId);
                CreateCombatBlockUI(player, duration);
            }

            float remainingTime = duration;
            Timer uiUpdateTimer = null;

            // Создаем один таймер для обновления UI
            uiUpdateTimer = timer.Repeat(1f, (int)duration, () =>
            {
                if (player == null || !player.IsConnected) 
                {
                    uiUpdateTimer?.Destroy();
                    blockedPlayers.Remove(playerId);
                    combatTimers.Remove(playerId);
                    return;
                }

                remainingTime--;
                
                if (remainingTime <= 0)
                {
                    DestroyCombatBlockUI(player);
                    blockedPlayers.Remove(playerId);
                    combatTimers.Remove(playerId);
                    uiUpdateTimer?.Destroy();
                }
                else
                {
                    UpdateCombatBlockUI(player, remainingTime);
                }
            });

            combatTimers[playerId] = uiUpdateTimer;
            
            // Обновляем UI только если это новая блокировка
            if (!blockedPlayers.Contains(playerId))
            {
                UpdateCombatBlockUI(player, duration);
            }
        }

        /// <summary>
        /// Creates the combat block UI for a player
        /// </summary>
        /// <param name="player">The player to show the UI to</param>
        /// <param name="duration">The duration for the combat block</param>
        private void CreateCombatBlockUI(BasePlayer player, float duration)
        {
            if (player == null || !player.IsConnected) return;

            // Сначала уничтожаем старый UI если он есть
            DestroyCombatBlockUI(player);

            CuiElementContainer container = new CuiElementContainer();

            // Background panel
            container.Add(new CuiPanel
            {
                Image = { Color = "0.97 0.92 0.88 0.18" },
                RectTransform = { AnchorMin = "0.3447913 0.1135", AnchorMax = "0.640625 0.1435" },
                CursorEnabled = false
            }, "Hud", CombatBlockUI);

            // Text label
            container.Add(new CuiElement
            {
                Name = CombatBlockUI + ".Label",
                Parent = CombatBlockUI,
                Components =
                {
                    new CuiTextComponent 
                    { 
                        Text = GetMessage("CombatBlock.Active", player, duration), 
                        FontSize = 15, 
                        Align = TextAnchor.MiddleCenter,
                        Color = "1 1 1 1"
                    },
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" }
                }
            });

            // Progress bar
            float progress = Mathf.Clamp01(duration / config.BlockDuration);
            container.Add(new CuiElement
            {
                Name = CombatBlockProgress,
                Parent = CombatBlockUI,
                Components =
                {
                    new CuiImageComponent { Color = "0.60 0.80 0.20 0.8" },
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = $"{progress} 0.1" }
                }
            });

            CuiHelper.AddUi(player, container);
        }

        /// <summary>
        /// Updates the combat block UI for a player
        /// </summary>
        /// <param name="player">The player to update the UI for</param>
        /// <param name="duration">The remaining duration of the combat block</param>
        private void UpdateCombatBlockUI(BasePlayer player, float duration)
        {
            if (player == null || !player.IsConnected) return;
            
            CuiElementContainer container = new CuiElementContainer();

            // Обновляем только текст
            container.Add(new CuiElement
            {
                Name = CombatBlockUI + ".Label",
                Parent = CombatBlockUI,
                Components =
                {
                    new CuiTextComponent 
                    { 
                        Text = GetMessage("CombatBlock.Active", player, duration), 
                        FontSize = 15, 
                        Align = TextAnchor.MiddleCenter,
                        Color = "1 1 1 1"
                    },
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" }
                }
            });

            // Обновляем прогресс-бар
            float progress = Mathf.Clamp01(duration / config.BlockDuration);
            container.Add(new CuiElement
            {
                Name = CombatBlockProgress,
                Parent = CombatBlockUI,
                Components =
                {
                    new CuiImageComponent { Color = "0.60 0.80 0.20 0.8" },
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = $"{progress} 0.1" }
                }
            });

            // Уничтожаем только обновляемые элементы
            CuiHelper.DestroyUi(player, CombatBlockProgress);
            CuiHelper.DestroyUi(player, CombatBlockUI + ".Label");
            
            // Добавляем обновленные элементы
            CuiHelper.AddUi(player, container);
        }

        /// <summary>
        /// Destroys the combat block UI for a player
        /// </summary>
        /// <param name="player">The player whose UI should be destroyed</param>
        private void DestroyCombatBlockUI(BasePlayer player)
        {
            if (player == null || !player.IsConnected) return;
            
            // Уничтожаем элементы в обратном порядке их создания
            CuiHelper.DestroyUi(player, CombatBlockProgress);
            CuiHelper.DestroyUi(player, CombatBlockUI + ".Label");
            CuiHelper.DestroyUi(player, CombatBlockUI);
        }

        /// <summary>
        /// Handles the event when an entity takes damage, applying combat block if applicable
        /// </summary>
        /// <param name="entity">The entity taking damage</param>
        /// <param name="info">The hit information</param>
        private void OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || info?.Initiator == null) return;
            
            if (entity is BasePlayer victim && info.Initiator is BasePlayer attacker)
            {
                if (victim != attacker && config.BlockOnReceiveDamage)
                {
                    AddCombatBlock(victim, config.BlockDuration);
                }

                if (config.BlockOnPlayerHit)
                {
                    AddCombatBlock(attacker, config.BlockDuration);
                }
            }
        }

        /// <summary>
        /// Handles the event when a player dies, removing combat block if configured to do so
        /// </summary>
        /// <param name="player">The player who died</param>
        /// <param name="info">The hit information</param>
        private void OnPlayerDeath(BasePlayer player, HitInfo info)
        {
            if (player == null) return;
            
            if (config.RemoveBlockOnDeath)
            {
                DestroyCombatBlockUI(player);
                blockedPlayers.Remove(player.userID);
            }
        }

        /// <summary>
        /// Handles chat commands, blocking them if the player is under combat block
        /// </summary>
        /// <param name="player">The player executing the command</param>
        /// <param name="message">The command message</param>
        /// <param name="channel">The chat channel</param>
        /// <returns>Returns false to block the command, otherwise null</returns>
        private object OnPlayerChat(BasePlayer player, string message, ConVar.Chat.ChatChannel channel)
        {
            if (player == null || string.IsNullOrEmpty(message)) return null;

            if (blockedPlayers.Contains(player.userID))
            {
                if (message != null && config.BlockedCommands.Exists(cmd => cmd != null && message.StartsWith(cmd, StringComparison.OrdinalIgnoreCase)))
                {
                    player.ChatMessage(GetMessage("CombatBlock.BlockedCommand", player));
                    return false;
                }
            }

            return null;
        }

        /// <summary>
        /// Handles user commands, blocking them if the player is under combat block
        /// </summary>
        /// <param name="player">The player executing the command</param>
        /// <param name="command">The command name</param>
        /// <param name="args">The command arguments</param>
        /// <returns>Returns false to block the command, otherwise null</returns>
        private object OnUserCommand(IPlayer player, string command, string[] args)
        {
            if (player?.Object == null) return null;
            
            var basePlayer = player.Object as BasePlayer;
            if (basePlayer == null) return null;

            if (blockedPlayers.Contains(basePlayer.userID))
            {
                if (command != null)
                {
                    command = "/" + command.ToLowerInvariant();
                    if (config.BlockedCommands.Contains(command))
                    {
                        basePlayer.ChatMessage(GetMessage("CombatBlock.UIMessage", basePlayer));
                        return false;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Checks if a player has an active combat block
        /// </summary>
        /// <param name="playerID">The ID of the player to check</param>
        /// <returns>Returns true if the player has an active combat block, otherwise false</returns>
        [HookMethod("HasCombatBlock")]
        public bool HasCombatBlock(ulong playerID)
        {
            return blockedPlayers.Contains(playerID);
        }
    }
}