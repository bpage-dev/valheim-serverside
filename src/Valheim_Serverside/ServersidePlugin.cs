﻿using BepInEx;
using BepInEx.Logging;
using FeaturesLib;
using HarmonyLib;
using PatchingLib;
using PluginConfiguration;
using Requirements;

namespace Valheim_Serverside
{

	[Harmony]
	[BepInPlugin("MVP.Valheim_Serverside_Simulations", "Serverside Simulations", "1.1.5")]
	[BepInDependency(ValheimPlusPluginId, BepInDependency.DependencyFlags.SoftDependency)]

	public class ServersidePlugin : BaseUnityPlugin
	{

		private static ServersidePlugin context;

		public static Configuration configuration;

		public static Harmony harmony;

		public const string ValheimPlusPluginId = "org.bepinex.plugins.valheim_plus";

		public static ManualLogSource logger;

		private void Awake()
		{
			context = this;
			logger = Logger;

			Configuration.Load(Config);

			if (!ModIsEnabled())
			{
				Logger.LogInfo("Serverside Simulations is disabled. (configuration)");
				return;
			}
			else if (!IsDedicated())
			{
				Logger.LogInfo("Serverside Simulations is disabled. (not a dedicated server)");
				return;
			}
			Logger.LogInfo("Installing Serverside Simulations");

			harmony = new Harmony("MVP.Valheim_Serverside_Simulations");

			AvailableFeatures availableFeatures = new AvailableFeatures();
			availableFeatures.AddFeature(new Features.Core());
			availableFeatures.AddFeature(new Features.MaxObjectsPerFrame());
			availableFeatures.AddFeature(new Features.Debugging());
			availableFeatures.AddFeature(new Features.Compat_ValheimPlus());

			PatchRequirements patchRequirements = new PatchRequirements();
			patchRequirements.AddRequirement(new PatchRequirement.DebugBuild());

			new HarmonyFeaturesPatcher(patchRequirements).PatchAll(availableFeatures.GetAllNestedTypes(), harmony);

			Logger.LogInfo("Serverside Simulations installed");
		}

		public bool ModIsEnabled()
		{
			return Configuration.modEnabled.Value;
		}

		public static bool IsDedicated()
		{
			return new ZNet().IsDedicated();
		}

		public static void PrintLog(string text)
		{
			System.Diagnostics.Trace.WriteLine(text);
		}

		public static void PrintLog(object[] obj)
		{
			System.Diagnostics.Trace.WriteLine(string.Concat(obj));
		}

		#if DEBUG
		[HarmonyPatch(typeof(Chat), "RPC_ChatMessage")]
		static class Chat_RPC_ChatMessage_Patch
		{
			static void Prefix(ref long sender, ref string text)
			{
				ZNetPeer peer = ZNet.instance.GetPeer(sender);
				if (peer == null)
				{
					return;
				}
				if (text == "startevent")
				{
					RandEventSystem.instance.SetRandomEventByName("army_theelder", peer.GetRefPos());
				}
				else if (text == "stopevent")
				{
					RandEventSystem.instance.ResetRandomEvent();
				}
			}
		}
		#endif


		[HarmonyPatch(typeof(ZNetScene), "CreateDestroyObjects")]
		private class CreateDestroyObjects_Patch
		/*
			The bread and butter of the mod, this patch facilitates spawning objects on the server.

			Creates and destroys ZDOs by finding all objects in each peer area.

			Some object overlap can happen if peers are close to each other, the objects are
			deduplicated by using a HashSet, see `List.Distinct`.

			This method originally works only with objects surrounding `ZNet.GetReferencePosition()` which returns some
			made-up nonsense on a dedicated server.

			DistantObjects: Are objects that have `m_distant` set to `true`, set (probably) in the prefab data;
			Distant objects are not affected by draw distance.

			CreateObjects: Makes no distinction between objects and nearby-objects except in the order
						   they are created.
		
			RemoveObjects: Marks all ZDOs for deletion by setting the current frame number on the ZDO,
						   and then checks if any of the ZDOs marked for deletion have an older/different
						   frame number.
		*/
		{
			private static bool Prefix(ZNetScene __instance)
			{
				List<ZDO> m_tempCurrentObjects = new List<ZDO>();
				List<ZDO> m_tempCurrentDistantObjects = new List<ZDO>();
				foreach (ZNetPeer znetPeer in ZNet.instance.GetConnectedPeers())
				{
					Vector2i zone = ZoneSystem.instance.GetZone(znetPeer.GetRefPos());
					ZDOMan.instance.FindSectorObjects(zone, ZoneSystem.instance.m_activeArea, ZoneSystem.instance.m_activeDistantArea, m_tempCurrentObjects, m_tempCurrentDistantObjects);
				}

				m_tempCurrentDistantObjects = m_tempCurrentDistantObjects.Distinct().ToList();
				m_tempCurrentObjects = m_tempCurrentObjects.Distinct().ToList();
				Traverse.Create(__instance).Method("CreateObjects", m_tempCurrentObjects, m_tempCurrentDistantObjects).GetValue();
				Traverse.Create(__instance).Method("RemoveObjects", m_tempCurrentObjects, m_tempCurrentDistantObjects).GetValue();
				return false;
			}
		}

		[HarmonyPatch(typeof(ZoneSystem), "Update")]
		static class ZoneSystem_Update_Patch
		/*
			Creates Local-Zones for each peer position. Enabling simulation to be handled by the server.

			Original method: tries to create a Local-Zone for the position the player is standing in,
			if this is a server then a Ghost-Zone is created for the current reference position as well
			as for each peer's position.

			Local-Zone: Created on every player's client, container for things like terrain and vegetation.
			Ghost-Zone: Created only on the server, unsimulated (associated GameObjects are destroyed), used
						only to send associated information to clients.
		*/
		{
			static bool Prefix(ZoneSystem __instance, ref float ___m_updateTimer)
			{
				if (ZNet.GetConnectionStatus() != ZNet.ConnectionStatus.Connected)
				{
					return false;
				}

				___m_updateTimer += Time.deltaTime;
				if (___m_updateTimer > 0.1f)
				{
					___m_updateTimer = 0f;
					// original flag line removed, as well as the check for it as it always returns `false` on the server.
					//bool flag = Traverse.Create(__instance).Method("CreateLocalZones", ZNet.instance.GetReferencePosition()).GetValue<bool>();
					Traverse.Create(__instance).Method("UpdateTTL", 0.1f).GetValue();
					if (ZNet.instance.IsServer()) // && !flag)
					{
						//Traverse.Create(__instance).Method("CreateGhostZones", ZNet.instance.GetReferencePosition()).GetValue();
						//UnityEngine.Debug.Log(String.Concat(new object[] { "CreateLocalZones for", refPoint.x, " ", refPoint.y, " ", refPoint.z }));
						foreach (ZNetPeer znetPeer in ZNet.instance.GetPeers())
						{
							Traverse.Create(__instance).Method("CreateLocalZones", znetPeer.GetRefPos()).GetValue();
						}
					}
				}
				return false;
			}
		}

		[HarmonyPatch(typeof(ZDOMan), "ReleaseNearbyZDOS")]
		static class ZDOMan_ReleaseNearbyZDOS_Patch
		/*
			Releases nearby ZDOs for a player if no other peers are nearby that player.
			If instead the nearby ZDO has no owner, set owner to server so that it simulates on the server.

			Original method:
			If ZDO is no longer near the peer, release ownership. If no owner set, change ownership to said peer.
		*/
		{
			static bool Prefix(ZDOMan __instance, ref Vector3 refPosition, ref long uid)
			{
				Vector2i zone = ZoneSystem.instance.GetZone(refPosition);
				List<ZDO> m_tempNearObjects = Traverse.Create(__instance).Field("m_tempNearObjects").GetValue<List<ZDO>>();
				m_tempNearObjects.Clear();

				__instance.FindSectorObjects(zone, ZoneSystem.instance.m_activeArea, 0, m_tempNearObjects, null);
				foreach (ZDO zdo in m_tempNearObjects)
				{
					if (zdo.m_persistent)
					{
						bool anyPlayerInArea = false;
						foreach (ZNetPeer peer in ZNet.instance.GetPeers())
						{
							if (ZNetScene.instance.InActiveArea(zdo.GetSector(), ZoneSystem.instance.GetZone(peer.GetRefPos())))
							{
								anyPlayerInArea = true;
								break;
							}
						}

						if (zdo.m_owner == uid || zdo.m_owner == ZNet.instance.GetUID())
						{
							if (!anyPlayerInArea)
							{
								zdo.SetOwner(0L);
							}
						}
						else if (
							(zdo.m_owner == 0L 
							|| !new Traverse(__instance).Method("IsInPeerActiveArea", new object[] { zdo.GetSector(), zdo.m_owner }).GetValue<bool>()
							)
							&& anyPlayerInArea
						)
						{
							zdo.SetOwner(ZNet.instance.GetUID());
						}
					}
				}
				return false;
			}
		}

		[HarmonyPatch(typeof(RandEventSystem), "FixedUpdate")]
		static class RandEventSystem_FixedUpdate_Patch
		/*
			Patches out m_localPlayer == null check by reversing the boolean check
			and instead of:

				if (this.IsInsideRandomEventArea(this.m_randomEvent, Player.m_localPlayer.transform.position))

			reuses the previously-assigned playerInArea boolean.

			Fixes monsters not spawning during events with this mod active.
		*/
		{
			static Dictionary<OpCode, OpCode> StlocToLdloc = new Dictionary<OpCode, OpCode> {
				{OpCodes.Stloc_0, OpCodes.Ldloc_0},
				{OpCodes.Stloc_1, OpCodes.Ldloc_1},
				{OpCodes.Stloc_2, OpCodes.Ldloc_2},
				{OpCodes.Stloc_3, OpCodes.Ldloc_3},
				{OpCodes.Stloc_S, OpCodes.Ldloc_S},
				{OpCodes.Stloc, OpCodes.Ldloc}
			};

			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> _instructions)
			{
				//var codes = new List<CodeInstruction>(instructions);
				MethodInfo isAnyPlayerInfo = AccessTools.Method(typeof(RandEventSystem), "IsAnyPlayerInEventArea");
				FieldInfo field_m_localPlayer = AccessTools.Field(typeof(Player), nameof(Player.m_localPlayer));
				MethodInfo opImplicitInfo = AccessTools.Method(typeof(UnityEngine.Object), "op_Implicit");

				bool foundIsAnyPlayer = false;
				CodeInstruction ldPlayerInArea = null;

				List<CodeInstruction> instructions = _instructions.ToList();
				List<CodeInstruction> new_instructions = _instructions.ToList();

				var insideRandomEventAreaCheck = new SequentialInstructions(new List<CodeInstruction>(new CodeInstruction[]
				{
					new CodeInstruction(OpCodes.Ldarg_0),
					new CodeInstruction(OpCodes.Ldarg_0),
					new CodeInstruction(OpCodes.Ldfld),
					new CodeInstruction(OpCodes.Ldsfld),
					new CodeInstruction(OpCodes.Callvirt),
					new CodeInstruction(OpCodes.Callvirt),
					new CodeInstruction(OpCodes.Call)
				}));
				for (int i = 0; i < instructions.Count; i++)
				{
					CodeInstruction instruction = instructions[i];

					if (instruction.OperandIs(isAnyPlayerInfo))
					{
						//ZLog.Log("isAnyPlayerInfo");
						foundIsAnyPlayer = true;
					}
					else if (foundIsAnyPlayer && instruction.IsStloc())
					{
						//ZLog.Log("foundIsAnyPlayer && IsStloc");
						ldPlayerInArea = instruction.Clone();
						ldPlayerInArea.opcode = StlocToLdloc[instruction.opcode];
						foundIsAnyPlayer = false;
					}
					else if (ldPlayerInArea != null && insideRandomEventAreaCheck.Check(instruction))
					{
						//ZLog.Log("Removing a lot and inserting ldPlayerInArea");
						int count = insideRandomEventAreaCheck.Sequential.Count;
						int startIdx = i - (count - 1);
						new_instructions.RemoveRange(startIdx, count);
						new_instructions.Insert(startIdx, ldPlayerInArea);
						break;
					}
				}

				var localPlayerCheck = new SequentialInstructions(new List<CodeInstruction>(new CodeInstruction[]
				{
					new CodeInstruction(OpCodes.Ldsfld, field_m_localPlayer),
					new CodeInstruction(OpCodes.Call, opImplicitInfo),
					new CodeInstruction(OpCodes.Brfalse)
				}));
				for (int i = 0; i < new_instructions.Count; i++)
				{
					CodeInstruction instruction = new_instructions[i];
					if (localPlayerCheck.Check(instruction))
					{
						yield return new CodeInstruction(OpCodes.Brtrue, instruction.operand);
						continue;
					}
					yield return instruction;
				}
			}
		}

		public static List<SpawnSystem.SpawnData> GetCurrentSpawners(RandEventSystem instance, SpawnSystem spawnSystem)
		/*
			Return spawners if there are nearby players in the event area.
		*/
		{
			if (Traverse.Create(instance).Field("m_activeEvent").GetValue<RandomEvent>() == null)
			{
				return null;
			}

			ZNetView spawnSystem_m_nview = Traverse.Create(spawnSystem).Field("m_nview").GetValue<ZNetView>();
			RandomEvent randomEvent = Traverse.Create(instance).Field("m_randomEvent").GetValue<RandomEvent>();

			foreach (Player player in Player.GetAllPlayers())
			{
				if (ZNetScene.instance.InActiveArea(spawnSystem_m_nview.GetZDO().GetSector(), player.transform.position))
				{
					if (Traverse.Create(instance).Method("IsInsideRandomEventArea", new Type[] { typeof(RandomEvent), typeof(Vector3) }, new object[] { randomEvent, player.transform.position }).GetValue<bool>())
					{
						return instance.GetCurrentSpawners();
					}
				}
			}
			return null;
		}

		[HarmonyPatch(typeof(SpawnSystem), "UpdateSpawning")]
		static class SpawnSystem_UpdateSpawning_Patch
		/*
			Patches out m_localPlayer == null check in SpawnSystem.UpdateSpawning
			by reversing the boolean check.

			Fixes enemies not spawning during random events.
		*/
		{
			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> _instructions)
			{
				FieldInfo field_m_localPlayer = AccessTools.Field(typeof(Player), nameof(Player.m_localPlayer));
				var localPlayerCheck = new SequentialInstructions(new List<CodeInstruction>(new CodeInstruction[]
				{
					new CodeInstruction(OpCodes.Ldsfld, field_m_localPlayer),
					new CodeInstruction(OpCodes.Ldnull),
					new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(UnityEngine.Object), "op_Equality")),
					new CodeInstruction(OpCodes.Brfalse)
				}));
				var loadRandEventSystemInstance = new SequentialInstructions(new List<CodeInstruction>(new CodeInstruction[]
				{
					new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SpawnSystem), "UpdateSpawnList")),
					new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(RandEventSystem), "get_instance"))
				}));
				var getCurrentSpawners = new SequentialInstructions(new List<CodeInstruction>(new CodeInstruction[]
				{
					new CodeInstruction(OpCodes.Callvirt, AccessTools.Method(typeof(RandEventSystem), nameof(RandEventSystem.GetCurrentSpawners))),
				}));

				foreach (CodeInstruction instruction in _instructions)
				{
					// Add SpawnSystem instance to stack after RandEventSystem instance.
					if (loadRandEventSystemInstance.Check(instruction))
					{
						yield return instruction;
						yield return new CodeInstruction(OpCodes.Ldarg_0);
						continue;
					}
					// replace GetCurrentSpawners with call to our method.
					if (getCurrentSpawners.Check(instruction))
					{
						yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ServersidePlugin), "GetCurrentSpawners"));
						continue;
					}
					if (localPlayerCheck.Check(instruction))
					{
						yield return new CodeInstruction(OpCodes.Brtrue, instruction.operand);
						continue;
					}
					yield return instruction;
				}
			}
		}

		[HarmonyPatch(typeof(ZNetScene), "OutsideActiveArea", new Type[] { typeof(Vector3) })]
		private class ZNetScene_OutsideActiveArea_Patch
		/*
			Originally uses `ZNet.GetReferencePosition` to determine active area but with the server 
			handling all areas, it must check if the `Vector3` is within any of the peers' active areas.

			Returns `false` if the point is within *any* of the peers' active areas and `false` otherwise.

			SpawnArea (e.g BonePileSpawner) uses `OutsideActiveArea` to determine if it should be simulated.
		*/
		{
			static bool Prefix(ref bool __result, ZNetScene __instance, Vector3 point)
			{
				__result = true;
				foreach (ZNetPeer znetPeer in ZNet.instance.GetPeers())
				{
					if (!__instance.OutsideActiveArea(point, znetPeer.GetRefPos()))
					{
						__result = false;
					}
				}
				return false;
			}
		}

		[HarmonyPatch(typeof(ZDOMan), "RPC_ZDOData")]
		public class ZDOMan_RPC_ZDODataPatch
		{
			static void CheckShouldOwn(ZDO zdo, bool isNew)
			{
				long myid = ZNet.instance.GetUID();
				long owner = zdo.m_owner;
				if (isNew && owner != 0L && owner != myid)
				{
					int prefabHash = zdo.GetPrefab();
					GameObject prefab = ZNetScene.instance.GetPrefab(prefabHash);
					// Only take control of building pieces for now
					// TODO: See if this should be expanded
					if (prefab != null && prefab.GetComponent<Piece>() != null)
					{
						#if DEBUG
						context.Logger.LogInfo($"Taking ownership of new ZDO (player id: {owner} id: {zdo.m_uid} name: {prefab.name}");
						#endif
						zdo.SetOwner(myid);
						return;
					}
				}
			}

			public static void ILManipulator(ILContext il)
			{
				int idx_stZDO = 0;
				int idx_stZDOIsNew = 0;

				new ILCursor(il)
					.GotoNext(
						i => i.MatchCall<ZDOMan>("CreateNewZDO"),
						i => i.MatchStloc(out idx_stZDO),
						i => i.MatchLdcI4(out _),
						i => i.MatchStloc(out idx_stZDOIsNew)
					)
					.GotoNext(MoveType.After,
						i => i.MatchLdloc(out _),
						i => i.MatchLdloc(out _),
						i => i.MatchCallvirt<ZDO>("Deserialize")
					)
					.Emit(OC.Ldloc, idx_stZDO)
					.Emit(OC.Ldloc, idx_stZDOIsNew)
					.Emit(OC.Call, AccessTools.Method(typeof(ZDOMan_RPC_ZDODataPatch),
													  nameof(ZDOMan_RPC_ZDODataPatch.CheckShouldOwn)))
				;
			}
		}

		[HarmonyPatch(typeof(ZRoutedRpc), "RouteRPC")]
		static class ZRoutedRpc_RouteRPC_Patch
		/*
			When a client requests to be the "user" (driver) of a ship this RPC method
			is sent from the current ship owner when they accept the request.
			We set the owner of the ship to the new ship driver.

			Allows players to drive ships with no roundtrip latency.
		*/
		{
			static void Prefix(ZRoutedRpc.RoutedRPCData rpcData)
			{
				if (rpcData.m_methodHash == "RequestRespons".GetStableHashCode())
				{
					bool granted = rpcData.m_parameters.ReadBool();
					ZDO zdo = ZDOMan.instance.GetZDO(rpcData.m_targetZDO);
					if (zdo != null && granted)
					{
						zdo.SetOwner(rpcData.m_targetPeerID);
					}
				}
			}
		}

		[HarmonyPatch(typeof(Ship), "UpdateOwner")]
		static class Ship_UpdateOwner_Patch
		/*
			If the ship has no valid user, set the owner to the server
			to ensure simulations are updated correctly.
		*/
		{ 
			static bool Prefix(ref Ship __instance) {
				if (!__instance.m_shipControlls.HaveValidUser())
				{
					new Traverse(__instance).Field("m_nview").GetValue<ZNetView>().GetZDO().SetOwner(ZNet.instance.GetUID());
				}
				return true;
			}
		}
	}

}
