﻿// Copyright (c) Aura development team - Licensed under GNU GPL
// For more information, see license file in the main folder

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Aura.Channel.Database;
using Aura.Channel.Network.Sending;
using Aura.Channel.Scripting.Scripts;
using Aura.Channel.Skills;
using Aura.Channel.World;
using Aura.Channel.World.Entities;
using Aura.Channel.World.Quests;
using Aura.Data;
using Aura.Data.Database;
using Aura.Mabi;
using Aura.Mabi.Const;
using Aura.Shared.Util;

namespace Aura.Channel.Scripting
{
	public class ScriptManager : Aura.Shared.Scripting.ScriptManager
	{
		private const string SystemIndexRoot = "system/scripts/";
		private const string UserIndexRoot = "user/scripts/";
		private const string IndexPath = SystemIndexRoot + "scripts.txt";

		private Dictionary<int, ItemScript> _itemScripts;
		private Dictionary<string, Type> _aiScripts;
		private Dictionary<string, NpcShopScript> _shops;
		private Dictionary<int, QuestScript> _questScripts;
		private Dictionary<long, Dictionary<SignalType, Action<Creature, EventData>>> _clientEventHandlers;

		private Dictionary<string, Dictionary<string, List<ScriptHook>>> _hooks;

		private Dictionary<int, CreatureSpawn> _creatureSpawns;

		public ScriptVariables GlobalVars { get; protected set; }

		public ScriptManager()
		{
			_itemScripts = new Dictionary<int, ItemScript>();
			_aiScripts = new Dictionary<string, Type>();
			_shops = new Dictionary<string, NpcShopScript>();
			_questScripts = new Dictionary<int, QuestScript>();
			_clientEventHandlers = new Dictionary<long, Dictionary<SignalType, Action<Creature, EventData>>>();

			_hooks = new Dictionary<string, Dictionary<string, List<ScriptHook>>>();

			_creatureSpawns = new Dictionary<int, CreatureSpawn>();

			this.GlobalVars = new ScriptVariables();
		}

		public void Init()
		{
			this.GlobalVars.Perm = ChannelServer.Instance.Database.LoadVars("Aura System", 0);
			ChannelServer.Instance.Events.MabiTick += OnMabiTick;
		}

		/// <summary>
		/// Loads all scripts.
		/// </summary>
		public void Load()
		{
			this.LoadAiScripts();
			this.LoadItemScripts();
			this.LoadScripts(IndexPath);

			this.LoadSpawns();
		}

		/// <summary>
		/// Removes all NPCs, props, etc and loads them again.
		/// </summary>
		public void Reload()
		{
			foreach (var script in _scriptsToDispose)
				script.Dispose();
			_scriptsToDispose.Clear();

			ChannelServer.Instance.World.RemoveScriptedEntities();

			this.Load();
		}

		/// <summary>
		/// Returns path for the compiled version of the script.
		/// Creates directory structure if it doesn't exist.
		/// </summary>
		/// <param name="path"></param>
		/// <returns></returns>
		protected override string GetCachePath(string path)
		{
			path = path.Replace(Path.GetFullPath(SystemIndexRoot).Replace("\\", "/").TrimStart('/'), "");
			path = path.Replace(Path.GetFullPath(UserIndexRoot).Replace("\\", "/").TrimStart('/'), "");

			return base.GetCachePath(path);
		}

		/// <summary>
		/// Returns list of script files loaded from scripts.txt.
		/// </summary>
		/// <param name="rootList"></param>
		/// <returns></returns>
		protected override ICollection<string> ReadScriptList(string scriptListFile)
		{
			var result = new List<string>();

			using (var fr = new FileReader(scriptListFile))
			{
				foreach (var line in fr)
				{
					// Get path to the current list's directory
					var listPath = Path.GetFullPath(line.File);
					listPath = listPath.Replace(Path.GetFullPath(UserIndexRoot), "");
					listPath = listPath.Replace(Path.GetFullPath(SystemIndexRoot), "");
					listPath = Path.GetDirectoryName(listPath);

					var fileName = Path.Combine(listPath, line.Value).Replace("\\", "/");

					// Get script path for either user or system
					var scriptPath = Path.Combine(UserIndexRoot, fileName);
					if (!File.Exists(scriptPath))
						scriptPath = Path.Combine(SystemIndexRoot, fileName);

					if (!result.Contains(scriptPath))
						result.Add(scriptPath);
				}
			}

			return result;
		}

		/// <summary>
		/// Generates script for all items and loads it.
		/// </summary>
		private void LoadItemScripts()
		{
			Log.Info("Loading item scripts...");

			_itemScripts.Clear();

			// Place generated script in the cache folder
			var tmpPath = this.GetCachePath(Path.Combine(SystemIndexRoot, "items", "inline.generated.cs")).Replace(".compiled", "");

			// We go over all items only once, inline scripts are added
			// to the generated script if the inline script needs updating.
			var updateInline =
				(File.GetLastWriteTime(Path.Combine("system", "db", "items.txt")) >= File.GetLastWriteTime(tmpPath)) ||
				(File.GetLastWriteTime(Path.Combine("user", "db", "items.txt")) >= File.GetLastWriteTime(tmpPath));

			var sb = new StringBuilder();

			// Default usings
			sb.AppendLine("// Automatically generated from inline scripts in the item database");
			sb.AppendLine();
			sb.AppendLine("using Aura.Channel.World.Entities;");
			sb.AppendLine("using Aura.Channel.Scripting.Scripts;");
			sb.AppendLine();

			// Go through all items
			foreach (var entry in AuraData.ItemDb.Entries.Values)
			{
				var empty = (string.IsNullOrWhiteSpace(entry.OnUse) && string.IsNullOrWhiteSpace(entry.OnEquip) && string.IsNullOrWhiteSpace(entry.OnUnequip) && string.IsNullOrWhiteSpace(entry.OnCreation));
				var match = Regex.Match(entry.OnUse, @"^\s*use\s*\(\s*""([^"")]+)""\s*\)\s*;?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
				var use = match.Success;

				// Inline script
				if (!empty && !use)
				{
					if (updateInline)
					{
						// Add inline scripts to the collection,
						// wrapped in an ItemScript class.
						sb.AppendFormat("public class ItemScript{0} : ItemScript {{" + Environment.NewLine, entry.Id);
						{
							if (!string.IsNullOrWhiteSpace(entry.OnUse))
								sb.AppendFormat("	public override void OnUse(Creature cr, Item i)     {{ {0} }}" + Environment.NewLine, entry.OnUse.Trim());
							if (!string.IsNullOrWhiteSpace(entry.OnEquip))
								sb.AppendFormat("	public override void OnEquip(Creature cr, Item i)   {{ {0} }}" + Environment.NewLine, entry.OnEquip.Trim());
							if (!string.IsNullOrWhiteSpace(entry.OnUnequip))
								sb.AppendFormat("	public override void OnUnequip(Creature cr, Item i) {{ {0} }}" + Environment.NewLine, entry.OnUnequip.Trim());
							if (!string.IsNullOrWhiteSpace(entry.OnCreation))
								sb.AppendFormat("	public override void OnCreation(Item i) {{ {0} }}" + Environment.NewLine, entry.OnCreation.Trim());
						}
						sb.AppendFormat("}}" + Environment.NewLine + Environment.NewLine);
					}

					continue;
				}

				// Not inline or empty

				// Get file name from use command or by id.
				var scriptFile = (use ? (match.Groups[1].Value) : (entry.Id + ".cs"));

				var scriptPath = Path.Combine(UserIndexRoot, "items", scriptFile);
				if (!File.Exists(scriptPath))
					scriptPath = Path.Combine(SystemIndexRoot, "items", scriptFile);
				if (!File.Exists(scriptPath))
				{
					// Only show error if the use was explicit
					if (use)
						Log.Error("Item script not found: {0}", "items/" + scriptFile);
					continue;
				}

				var asm = this.GetAssembly(scriptPath);
				if (asm != null)
					this.LoadItemScriptAssembly(asm, entry.Id);

				Log.Debug(_itemScripts[entry.Id].GetType().Name);
			}

			// Update inline script
			if (updateInline)
			{
				File.WriteAllText(tmpPath, sb.ToString());
			}

			// Compile will update assembly if generated script was updated
			//foreach (string filePath in )
			var inlineAsm = this.GetAssembly(tmpPath);
			if (inlineAsm != null)
				this.LoadItemScriptAssembly(inlineAsm);

			Log.Info("Done loading item scripts.");
		}

		/// <summary>
		/// Loads AI scripts
		/// </summary>
		private void LoadAiScripts()
		{
			Log.Info("Loading AI scripts...");

			_aiScripts.Clear();

			foreach (var folder in new string[] { Path.Combine(SystemIndexRoot, "ai"), Path.Combine(UserIndexRoot, "ai") })
			{
				if (!Directory.Exists(folder))
					continue;

				foreach (var filePath in Directory.GetFiles(folder))
				{
					var fileName = Path.GetFileNameWithoutExtension(filePath);

					var asm = this.GetAssembly(filePath);
					if (asm != null)
					{
						var types = GetJITtedTypes(asm, filePath);

						// Get first AiScript class and save the type
						foreach (var type in types.Where(a => a.IsSubclassOf(typeof(AiScript))))
						{
							_aiScripts[fileName] = type;
							break;
						}
					}
				}
			}

			Log.Info("Done loading AI scripts.");
		}


		/// <summary>
		/// Returns shop or null.
		/// </summary>
		/// <param name="typeName"></param>
		/// <returns></returns>
		public NpcShopScript GetShop(string typeName)
		{
			NpcShopScript result;
			_shops.TryGetValue(typeName, out result);
			return result;
		}

		/// <summary>
		/// Returns true if shop of type exists.
		/// </summary>
		/// <param name="typeName"></param>
		/// <returns></returns>
		public bool ShopExists(string typeName)
		{
			return _shops.ContainsKey(typeName);
		}

		/// <summary>
		/// Adds shop.
		/// </summary>
		/// <param name="shop"></param>
		public void AddShop(NpcShopScript shop)
		{
			_shops[shop.GetType().Name] = shop;
		}

		/// <summary>
		/// Returns new AI script by name for creature, or null.
		/// </summary>
		/// <param name="name"></param>
		/// <param name="creature"></param>
		/// <returns></returns>
		public AiScript GetAi(string name, Creature creature)
		{
			Type type;
			_aiScripts.TryGetValue(name, out type);
			if (type == null)
				return null;

			var script = Activator.CreateInstance(type) as AiScript;
			script.Attach(creature);

			return script;
		}

		/// <summary>
		/// Loads item script classes from assembly.
		/// </summary>
		/// <param name="asm"></param>
		/// <param name="itemId">Only loads first class for itemId, if this is not 0.</param>
		/// <remarks>
		/// Item scripts have some special needs, like needing an item id.
		/// Hard to handle inside the normal loading.
		/// If itemId is 0 it's retreived from the class name, ItemScript(Id).
		/// </remarks>
		private void LoadItemScriptAssembly(Assembly asm, int itemId = 0)
		{
			foreach (var type in asm.GetTypes().Where(a => a.IsSubclassOf(typeof(ItemScript)) && !a.IsAbstract && !a.Name.StartsWith("_")))
			{
				var itemScript = Activator.CreateInstance(type) as ItemScript;

				// Stop after first type if this loading was for a single item.
				if (itemId != 0)
				{
					_itemScripts[itemId] = itemScript;
					return;
				}

				// Parse item id from name
				if (!Regex.IsMatch(type.Name, "^ItemScript[0-9]+$") || !int.TryParse(type.Name.Substring(10), out itemId))
				{
					Log.Error("Unable to parse item id of item script '{0}'.", type.Name);
					continue;
				}

				_itemScripts[itemId] = itemScript;

				itemId = 0;
			}
		}

		/// <summary>
		/// Return the item script by itemId, or null.
		/// </summary>
		/// <param name="itemId"></param>
		/// <returns></returns>
		public ItemScript GetItemScript(int itemId)
		{
			ItemScript result;
			_itemScripts.TryGetValue(itemId, out result);
			return result;
		}

		/// <summary>
		/// Adds spawn.
		/// </summary>
		/// <param name="spawn"></param>
		public void AddCreatureSpawn(CreatureSpawn spawn)
		{
			_creatureSpawns[spawn.Id] = spawn;
		}

		/// <summary>
		/// Spawns creatures using creature spawn information.
		/// </summary>
		public void LoadSpawns()
		{
			Log.Info("Spawning creatures...");

			var spawned = 0;

			foreach (var spawn in _creatureSpawns.Values)
			{
				spawned += this.Spawn(spawn);
				continue;
			}

			Log.Info("Done spawning {0} creatures.", spawned);
		}

		/// <summary>
		/// Spawns all creatures for spawn, or amount.
		/// </summary>
		/// <param name="spawnId"></param>
		/// <param name="amount"></param>
		/// <returns></returns>
		public int Spawn(int spawnId, int amount = 0)
		{
			if (!_creatureSpawns.ContainsKey(spawnId))
			{
				Log.Warning("ScriptManager.Spawn: Failed, missing spawn '{0}'.", spawnId);
				return 0;
			}

			return this.Spawn(_creatureSpawns[spawnId], amount);
		}

		/// <summary>
		/// Spawns all creatures for spawn, or amount.
		/// </summary>
		/// <param name="spawn"></param>
		/// <param name="amount"></param>
		/// <returns></returns>
		public int Spawn(CreatureSpawn spawn, int amount = 0)
		{
			var result = 0;
			if (amount == 0)
				amount = spawn.Amount;

			for (int i = 0; i < amount; ++i)
			{
				var pos = spawn.GetRandomPosition();
				if (this.Spawn(spawn.RaceId, spawn.RegionId, pos.X, pos.Y, spawn.Id, false, false) == null)
					return result;

				result++;
			}

			return result;
		}

		/// <summary>
		/// Spawns a creature.
		/// </summary>
		/// <param name="raceId"></param>
		/// <param name="regionId"></param>
		/// <param name="x"></param>
		/// <param name="y"></param>
		/// <param name="spawnId"></param>
		/// <param name="active"></param>
		/// <param name="effect"></param>
		/// <returns></returns>
		public Creature Spawn(int raceId, int regionId, int x, int y, int spawnId, bool active, bool effect)
		{
			// Create NPC
			var creature = new NPC();
			creature.Race = raceId;
			creature.LoadDefault();
			creature.SpawnId = spawnId;
			creature.Name = creature.RaceData.Name;
			creature.Color1 = creature.RaceData.Color1;
			creature.Color2 = creature.RaceData.Color2;
			creature.Color3 = creature.RaceData.Color3;
			creature.Height = creature.RaceData.Size;
			creature.Life = creature.LifeMaxBase = creature.RaceData.Life;
			creature.Mana = creature.ManaMaxBase = creature.RaceData.Mana;
			creature.State = (CreatureStates)creature.RaceData.DefaultState;
			creature.Direction = (byte)RandomProvider.Get().Next(256);

			// Set drops
			creature.Drops.GoldMin = creature.RaceData.GoldMin;
			creature.Drops.GoldMax = creature.RaceData.GoldMax;
			creature.Drops.Add(creature.RaceData.Drops);

			// Give skills
			foreach (var skill in creature.RaceData.Skills)
				creature.Skills.Add((SkillId)skill.SkillId, (SkillRank)skill.Rank, creature.Race);

			// Set AI
			if (!string.IsNullOrWhiteSpace(creature.RaceData.AI) && creature.RaceData.AI != "none")
			{
				creature.AI = this.GetAi(creature.RaceData.AI, creature);
				if (creature.AI == null)
					Log.Warning("Spawn: Missing AI '{0}' for '{1}'.", creature.RaceData.AI, raceId);
			}

			// Warp to spawn point
			if (!creature.Warp(regionId, x, y))
			{
				Log.Error("Failed to spawn '{0}'s.", raceId);
				return null;
			}

			creature.SpawnLocation = new Location(regionId, x, y);

			// Activate AI at least once
			if (creature.AI != null && active)
				creature.AI.Activate(0);

			// Spawn effect
			if (effect)
				Send.SpawnEffect(SpawnEffect.Monster, creature.RegionId, x, y, creature, creature);

			return creature;
		}

		/// <summary>
		/// 5 min tick, global var saving.
		/// </summary>
		/// <param name="time"></param>
		public void OnMabiTick(ErinnTime time)
		{
			ChannelServer.Instance.Database.SaveVars("Aura System", 0, this.GlobalVars.Perm);
			Log.Info("Saved global script variables.");
		}

		/// <summary>
		/// Returs quest data or null.
		/// </summary>
		/// <param name="questId"></param>
		/// <returns></returns>
		public QuestScript GetQuestScript(int questId)
		{
			QuestScript script;
			_questScripts.TryGetValue(questId, out script);
			return script;
		}

		/// <summary>
		/// Returns true if quest with the given id exists.
		/// </summary>
		/// <param name="questId"></param>
		/// <returns></returns>
		public bool QuestScriptExists(int questId)
		{
			return _questScripts.ContainsKey(questId);
		}

		/// <summary>
		/// Adds quest script.
		/// </summary>
		/// <param name="script"></param>
		public void AddQuestScript(QuestScript script)
		{
			_questScripts[script.Id] = script;
		}

		/// <summary>
		/// Calls delegates for npc and hook.
		/// </summary>
		/// <param name="npcName"></param>
		/// <param name="hook"></param>
		/// <returns></returns>
		public IEnumerable<ScriptHook> GetHooks(string npcName, string hook)
		{
			Dictionary<string, List<ScriptHook>> hooks;
			_hooks.TryGetValue(npcName, out hooks);
			if (hooks == null)
				return Enumerable.Empty<ScriptHook>();

			List<ScriptHook> calls;
			hooks.TryGetValue(hook, out calls);
			if (calls == null)
				return Enumerable.Empty<ScriptHook>();

			return calls;
		}

		/// <summary>
		/// Registers hook delegate.
		/// </summary>
		/// <param name="npcName"></param>
		/// <param name="hook"></param>
		/// <param name="func"></param>
		public void AddHook(string npcName, string hook, ScriptHook func)
		{
			if (!_hooks.ContainsKey(npcName))
				_hooks[npcName] = new Dictionary<string, List<ScriptHook>>();

			if (!_hooks[npcName].ContainsKey(hook))
				_hooks[npcName][hook] = new List<ScriptHook>();

			_hooks[npcName][hook].Add(func);
		}

		/// <summary>
		/// Adds handler for client event.
		/// </summary>
		/// <param name="id"></param>
		/// <param name="signal"></param>
		/// <param name="onTriggered"></param>
		public void AddClientEventHandler(long id, SignalType signal, Action<Creature, EventData> onTriggered)
		{
			Dictionary<SignalType, Action<Creature, EventData>> clientEvent;
			if (!_clientEventHandlers.TryGetValue(id, out clientEvent))
				_clientEventHandlers[id] = new Dictionary<SignalType, Action<Creature, EventData>>();

			_clientEventHandlers[id][signal] = onTriggered;
		}

		/// <summary>
		/// Returns handler for client event.
		/// </summary>
		/// <param name="id"></param>
		/// <param name="signal"></param>
		public Action<Creature, EventData> GetClientEventHandler(long id, SignalType signal)
		{
			Dictionary<SignalType, Action<Creature, EventData>> clientEvent;
			if (!_clientEventHandlers.TryGetValue(id, out clientEvent))
				return null;

			Action<Creature, EventData> result;
			if (!clientEvent.TryGetValue(signal, out result))
				return null;

			return result;
		}
	}

	public delegate Task<HookResult> ScriptHook(NpcScript npc, params object[] args);
}
