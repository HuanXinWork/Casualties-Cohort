using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using KrokV5Optimization;

namespace Together;

/// <summary>
/// 缓存 <see cref="Body.Update"/> 每帧查询的 <see cref="global::Painkillers"/> 与 <see cref="Body.HandlePeriodicChecks"/> 半秒查询的 <see cref="MindwipeScript"/>。
/// 必须声明为 <c>public</c> 以保证跨程序集 Harmony DMD 调用可见性；<see cref="Swap"/> 识别已挂载指令以兼容 <c>Plugin.DoHarmonyPatchAll</c> 与 <c>OptimizerMain</c> 双重扫描幂等性。
/// </summary>
[HarmonyPatch]
public static class BodyCache
{
	const int RetryFrames = 45;

	sealed class Slot
	{
		internal Painkillers Pain;
		internal MindwipeScript Wipe;
		internal int PainFrame = -999;
		internal int WipeFrame = -999;
	}

	static readonly Dictionary<int, Slot> Slots = new Dictionary<int, Slot>(16);
	static int _prunePendingAt = StaleIds.FirstThreshold;

	public static Painkillers Painkillers(Body body)
	{
		Slot slot = SlotFor(body);
		if (slot.Pain != null)
			return slot.Pain;
		if (Time.frameCount - slot.PainFrame < RetryFrames)
			return null;
		slot.PainFrame = Time.frameCount;
		slot.Pain = body.GetComponent<Painkillers>();
		return slot.Pain;
	}

	public static MindwipeScript Mindwipe(Body body)
	{
		Slot slot = SlotFor(body);
		if (slot.Wipe != null)
			return slot.Wipe;
		if (Time.frameCount - slot.WipeFrame < RetryFrames)
			return null;
		slot.WipeFrame = Time.frameCount;
		slot.Wipe = body.GetComponent<MindwipeScript>();
		return slot.Wipe;
	}

	[HarmonyPatch(typeof(Body), nameof(Body.Update))]
	[HarmonyTranspiler]
	static IEnumerable<CodeInstruction> Update(IEnumerable<CodeInstruction> instructions)
	{
		return Swap(instructions, typeof(Painkillers), nameof(Painkillers));
	}

	[HarmonyPatch(typeof(Body), nameof(Body.HandlePeriodicChecks))]
	[HarmonyTranspiler]
	static IEnumerable<CodeInstruction> Periodic(IEnumerable<CodeInstruction> instructions)
	{
		return Swap(instructions, typeof(MindwipeScript), nameof(Mindwipe));
	}

	static IEnumerable<CodeInstruction> Swap(IEnumerable<CodeInstruction> instructions, System.Type component, string method)
	{
		MethodInfo call = AccessTools.Method(typeof(BodyCache), method);
		bool found = false;
		foreach (CodeInstruction code in instructions)
		{
			if (!found)
			{
				if (code.opcode == OpCodes.Call && code.operand is MethodInfo existing
					&& existing.DeclaringType == typeof(BodyCache) && existing.Name == method)
				{
					found = true;
				}
				else if (code.opcode == OpCodes.Callvirt && code.operand is MethodInfo site
					&& site.IsGenericMethod && site.GetGenericArguments()[0] == component)
				{
					code.opcode = OpCodes.Call;
					code.operand = call;
					found = true;
				}
			}
			yield return code;
		}
		if (!found && OptimizerMain.Log != null)
			OptimizerMain.Log.LogWarning("[KrokV5Opt] body cache missed " + component.Name);
	}

	static Slot SlotFor(Body body)
	{
		int id = body.GetInstanceID();
		if (!Slots.TryGetValue(id, out Slot slot))
		{
			slot = new Slot();
			StaleIds.Prune<Body, Slot>(Slots, ref _prunePendingAt);
			Slots[id] = slot;
		}
		return slot;
	}
}
