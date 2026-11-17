using System;
using System.IO;
using System.Threading;

namespace KrokV5Optimization;

/// <summary>
/// 共享 Gzip / Raw Deflate 编解码缓冲池。由 <see cref="CasualtiesTogetherUtils.Util"/> 直接内联调用，不再通过 Harmony 补丁拦截。
/// </summary>
internal static class CompressPool
{
	internal static readonly MemoryStream GzipOut = new MemoryStream(4096);
	internal static readonly MemoryStream DeflateOut = new MemoryStream(4096);
	internal static readonly MemoryStream GzipDecOut = new MemoryStream(4096);
	internal static readonly MemoryStream DeflateDecOut = new MemoryStream(4096);
	internal static readonly object GzipGate = new object();
	internal static readonly object DeflateGate = new object();
	internal static bool GzipBusy;
	internal static bool DeflateBusy;

	internal static long GzipHits;
	internal static long GunzipHits;
	internal static long DeflateHits;
	internal static long InflateHits;
	internal static long InflateDecline;

	private static int _faults;

	internal static bool On => OptimizerMain.CompressPooling == null || OptimizerMain.CompressPooling.Value;

	internal static string Evidence() =>
		"gzip=" + GzipHits
		+ " " + FixCompressWriter.Evidence()
		+ " deflate=" + DeflateHits
		+ " gunzip=" + GunzipHits
		+ " inflate=" + InflateHits
		+ " inflateDecline=" + InflateDecline;

	internal static void Note(Exception ex)
	{
		_faults++;
		if (_faults <= 3)
			OptimizerMain.Log?.LogWarning($"[KrokV5Opt] compress fell back: {ex.GetType().Name}: {ex.Message}");
	}

	internal static bool TryCompressGzip(byte[] data, out byte[] result)
	{
		result = null;
		if (!On || data == null || GzipBusy || !Monitor.TryEnter(GzipGate))
			return false;
		try
		{
			if (GzipBusy)
				return false;
			GzipBusy = true;
			try
			{
				ResettableDeflate.WriteGzip(data, 0, data.Length, GzipOut);
				result = CopyExact(GzipOut);
				GzipHits++;
				return true;
			}
			catch (Exception ex)
			{
				Note(ex);
				result = null;
				return false;
			}
			finally
			{
				GzipBusy = false;
			}
		}
		finally
		{
			Monitor.Exit(GzipGate);
		}
	}

	internal static bool TryDecompressGzip(byte[] compressed, out byte[] result)
	{
		result = null;
		if (!On || compressed == null || GzipBusy || !Monitor.TryEnter(GzipGate))
			return false;
		try
		{
			if (GzipBusy)
				return false;
			GzipBusy = true;
			try
			{
				if (!ResettableDeflate.TryInflateGzip(compressed, GzipDecOut))
				{
					InflateDecline++;
					return false;
				}
				result = CopyExact(GzipDecOut);
				GunzipHits++;
				return true;
			}
			catch (Exception ex)
			{
				Note(ex);
				return false;
			}
			finally
			{
				GzipBusy = false;
			}
		}
		finally
		{
			Monitor.Exit(GzipGate);
		}
	}

	internal static bool TryCompressDeflate(byte[] data, out byte[] result)
	{
		result = null;
		if (!On || data == null || DeflateBusy || !Monitor.TryEnter(DeflateGate))
			return false;
		try
		{
			if (DeflateBusy)
				return false;
			DeflateBusy = true;
			try
			{
				ResettableDeflate.WriteRaw(data, 0, data.Length, DeflateOut);
				result = CopyExact(DeflateOut);
				DeflateHits++;
				return true;
			}
			catch (Exception ex)
			{
				Note(ex);
				result = null;
				return false;
			}
			finally
			{
				DeflateBusy = false;
			}
		}
		finally
		{
			Monitor.Exit(DeflateGate);
		}
	}

	internal static bool TryDecompressDeflate(byte[] compressed, out byte[] result)
	{
		result = null;
		if (!On || compressed == null || DeflateBusy || !Monitor.TryEnter(DeflateGate))
			return false;
		try
		{
			if (DeflateBusy)
				return false;
			DeflateBusy = true;
			try
			{
				if (!ResettableDeflate.TryInflateRaw(compressed, DeflateDecOut))
				{
					InflateDecline++;
					return false;
				}
				result = CopyExact(DeflateDecOut);
				InflateHits++;
				return true;
			}
			catch (Exception ex)
			{
				Note(ex);
				return false;
			}
			finally
			{
				DeflateBusy = false;
			}
		}
		finally
		{
			Monitor.Exit(DeflateGate);
		}
	}

	internal static byte[] CopyExact(MemoryStream stream)
	{
		int len = (int)stream.Length;
		if (len == 0)
			return Array.Empty<byte>();
		byte[] owned = new byte[len];
		Buffer.BlockCopy(stream.GetBuffer(), 0, owned, 0, len);
		return owned;
	}

	internal static byte[] Ensure(byte[] buffer, int length)
	{
		if (length < 0)
			length = 0;
		if (buffer != null && buffer.Length >= length)
			return buffer;
		int cap = buffer == null || buffer.Length < 256 ? 256 : buffer.Length;
		while (cap < length)
			cap *= 2;
		return new byte[cap];
	}
}

/// <summary>
/// 仅供世界区块编码（FixChunkBlock / FixChunkFluid）复用的纯静态转发入口（已移除 Harmony 寄生补丁）。
/// </summary>
internal static class FixUtilCompressDeflate
{
	internal static bool Try(byte[] data, out byte[] result) => CompressPool.TryCompressDeflate(data, out result);
}
