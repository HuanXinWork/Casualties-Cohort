using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Together;

namespace KrokV5Optimization;

/// <summary>
/// 复用 <see cref="WorldChunkSync.Server_ChunkHashes"/> 二维哈希网格，避免服务端每帧重新分配 <c>uint[,]</c> 数组（0 值天然代表未计算的空槽）。
/// <para>
/// <c>WorldChunkSync.LateUpdate</c> 已在源码层直接内联调用 <see cref="Rent"/>；
/// 此处补齐显式目标声明并在 <see cref="Prepare"/> 中检测当已无待替换 <c>uint[,]</c> 构造时安全跳过，彻底消除空目标方法异常与冗余 DMD 生成。
/// </para>
/// </summary>
[HarmonyPatch(typeof(WorldChunkSync), "LateUpdate")]
public static class FixChunkHashes
{
	public static long Reused;

	/// <summary>周期性优化统计报告计数器。</summary>
	internal static string Evidence() => "chunkHash=" + Reused;

	static bool Prepare()
	{
		MethodInfo target = AccessTools.Method(typeof(WorldChunkSync), "LateUpdate");
		if (target == null)
			return false;

		foreach (CodeInstruction inst in PatchProcessor.GetCurrentInstructions(target))
		{
			if (inst.opcode == OpCodes.Newobj && IsGridCtor(inst.operand))
				return true;
		}
		return false;
	}

	/// <summary>将 <c>LateUpdate</c> 内针对二维 <c>uint[,]</c> 网格的 <c>newobj</c> 替换为 <see cref="Rent"/>，其余分配保持原样。</summary>
	static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
	{
		MethodInfo rent = AccessTools.Method(typeof(FixChunkHashes), nameof(Rent));
		foreach (CodeInstruction instruction in instructions)
		{
			if (instruction.opcode == OpCodes.Newobj && IsGridCtor(instruction.operand))
			{
				yield return new CodeInstruction(OpCodes.Call, rent);
				continue;
			}
			yield return instruction;
		}
	}

	static bool IsGridCtor(object operand)
	{
		if (!(operand is ConstructorInfo ctor))
			return false;
		Type type = ctor.DeclaringType;
		return type != null
		       && type.IsArray
		       && type.GetArrayRank() == 2
		       && type.GetElementType() == typeof(uint);
	}

	public static uint[,] Rent(int width, int height)
	{
		uint[,] grid = WorldChunkSync.Server_ChunkHashes;
		if (grid == null || grid.GetLength(0) != width || grid.GetLength(1) != height)
			return new uint[width, height];
		Array.Clear(grid, 0, grid.Length);
		Reused++;
		return grid;
	}
}
