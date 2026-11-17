using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Together;

namespace KrokV5Optimization;

/// <summary>
/// 复用 <see cref="WorldChunkSync.Server_ChunkHashes"/> 二维哈希网格，避免服务端每帧重新分配 <c>uint[,]</c> 数组（0 值天然代表未计算的空槽）。
/// </summary>
[HarmonyPatch(typeof(WorldChunkSync), "LateUpdate")]
internal static class FixChunkHashes
{
	internal static long Reused;

	/// <summary>周期性优化统计报告计数器。</summary>
	internal static string Evidence() => "chunkHash=" + Reused;

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

	internal static uint[,] Rent(int width, int height)
	{
		uint[,] grid = WorldChunkSync.Server_ChunkHashes;
		if (grid == null || grid.GetLength(0) != width || grid.GetLength(1) != height)
			return new uint[width, height];
		Array.Clear(grid, 0, grid.Length);
		Reused++;
		return grid;
	}
}
