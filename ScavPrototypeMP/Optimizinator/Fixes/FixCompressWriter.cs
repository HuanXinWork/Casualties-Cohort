using System;
using System.Threading;
using LiteNetLib.Utils;

namespace KrokV5Optimization;

/// <summary>
/// CompressWriter copied the gzip result into a new byte[].
/// write it into the NetDataWriter that already exists. callers that keep their own copy are unchanged.
/// </summary>
internal static class FixCompressWriter
{
	internal static long Hits;

	// this file's counters on the 10s report.
	internal static string Evidence() => "writer=" + Hits;

	// 强类型直接操作 NetDataWriter，消除原先每包 4 次动态反射查找、装箱数组分配与双重中间缓冲区拷贝
	internal static bool Apply(NetDataWriter w2, int start_to_ignore) => Try(w2, start_to_ignore);

	// gzip bytes after `start`, then Put them back over that same range. the header before `start` stays.
	private static bool Try(NetDataWriter writer, int start)
	{
		if (!CompressPool.On || writer == null || start < 0 || CompressPool.GzipBusy)
			return false;
		int length = writer.Length - start;
		byte[] data = writer.Data;
		if (length < 0 || data == null || start + length > data.Length || !Monitor.TryEnter(CompressPool.GzipGate))
			return false;
		try
		{
			if (CompressPool.GzipBusy)
				return false;
			CompressPool.GzipBusy = true;
			try
			{
				// WriteGzip 先把完整压缩结果输出到独立的 CompressPool.GzipOut，压缩完毕后再覆写回 writer，无需额外暂存拷贝
				ResettableDeflate.WriteGzip(data, start, length, CompressPool.GzipOut);
				int outLen = (int)CompressPool.GzipOut.Length;
				if (outLen > ushort.MaxValue)
					return false;
				writer.SetPosition(start);
				writer.PutBytesWithLength(CompressPool.GzipOut.GetBuffer(), 0, (ushort)outLen);
				Hits++;
				return true;
			}
			catch (Exception ex)
			{
				CompressPool.Note(ex);
				return false;
			}
			finally
			{
				CompressPool.GzipBusy = false;
			}
		}
		finally
		{
			Monitor.Exit(CompressPool.GzipGate);
		}
	}

	// 在 NetDataReader.RawData 切片上直接原位解压，避免 r2.GetBytesWithLength() 每次分配中间压缩段 byte[]
	internal static bool TryDecompressSlice(byte[] rawData, int offset, int length, out byte[] result)
	{
		result = null;
		if (!CompressPool.On || rawData == null || CompressPool.GzipBusy || !Monitor.TryEnter(CompressPool.GzipGate))
			return false;
		try
		{
			if (CompressPool.GzipBusy)
				return false;
			CompressPool.GzipBusy = true;
			try
			{
				if (!ResettableDeflate.TryInflateGzip(rawData, offset, length, CompressPool.GzipDecOut))
				{
					CompressPool.InflateDecline++;
					return false;
				}
				result = CompressPool.CopyExact(CompressPool.GzipDecOut);
				CompressPool.GunzipHits++;
				return true;
			}
			catch (Exception ex)
			{
				CompressPool.Note(ex);
				return false;
			}
			finally
			{
				CompressPool.GzipBusy = false;
			}
		}
		finally
		{
			Monitor.Exit(CompressPool.GzipGate);
		}
	}
}
