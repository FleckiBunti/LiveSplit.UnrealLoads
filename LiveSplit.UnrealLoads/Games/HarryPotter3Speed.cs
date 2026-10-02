using LiveSplit.ComponentUtil;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace LiveSplit.UnrealLoads.Games
{
	// HP3 converts rdtsc cycles to seconds with Core.dll!GSecondsPerCycle, which appInitTiming measures at startup
	// over ~200 ms of a 1 ms resolution clock, which can cause a big drift. This measures the real TSC frequency precisely and
	// keeps GSecondsPerCycle at the correct value
	class HP3GameSpeed
	{
		const string SYMBOL = "?GSecondsPerCycle@@3NA";
		const double MAX_ERROR = 1e-5;
		static readonly TimeSpan CHECK_INTERVAL = TimeSpan.FromSeconds(1);

		static double _tscHz;

		IntPtr _ptr;
		readonly Stopwatch _sinceCheck = new Stopwatch();

		public void Attach(Process game)
		{
			_ptr = IntPtr.Zero;
			try
			{
				if (!Detour.GetExportTableParser(game, "core.dll").Exports.TryGetValue(SYMBOL, out _ptr))
				{
					Trace.WriteLine("[NoLoads] HP3: GSecondsPerCycle not found, game speed not corrected.");
					return;
				}

				if (_tscHz == 0)
					_tscHz = Tsc.MeasureHz(1000);

				Trace.WriteLine($"[NoLoads] HP3: TSC {_tscHz / 1e6:F4} MHz, game measured {1e-6 / Read(game):F4} MHz");
				Check(game);
			}
			catch (Exception ex)
			{
				_ptr = IntPtr.Zero;
				Trace.WriteLine("[NoLoads] HP3: game speed correction failed: " + ex);
			}
		}

		public void Update(Process game)
		{
			if (_ptr == IntPtr.Zero || _sinceCheck.Elapsed < CHECK_INTERVAL)
				return;

			Check(game);
		}

		void Check(Process game)
		{
			_sinceCheck.Restart();

			var correct = 1.0 / _tscHz;
			var current = Read(game);
			if (double.IsNaN(current) || Math.Abs(current / correct - 1) <= MAX_ERROR)
				return;

			game.WriteBytes(_ptr, BitConverter.GetBytes(correct));
			Trace.WriteLine($"[NoLoads] HP3: game was running {(current / correct - 1) * 100:+0.0000;-0.0000}% vs real time, corrected.");
		}

		// NaN when the read fails (e.g. the game is closing)
		double Read(Process game)
		{
			var bytes = game.ReadBytes(_ptr, sizeof(double));
			return bytes == null ? double.NaN : BitConverter.ToDouble(bytes, 0);
		}

		static class Tsc
		{
			[DllImport("kernel32")] static extern IntPtr VirtualAlloc(IntPtr a, UIntPtr s, uint t, uint p);
			[DllImport("kernel32")] static extern bool QueryPerformanceCounter(out long c);
			[DllImport("kernel32")] static extern bool QueryPerformanceFrequency(out long f);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			delegate ulong RdtscFn();

			static readonly RdtscFn Rdtsc = Create();

			static RdtscFn Create()
			{
				var code = IntPtr.Size == 4
					? new byte[] { 0x0F, 0x31, 0xC3 }                                            // rdtsc; ret
					: new byte[] { 0x0F, 0x31, 0x48, 0xC1, 0xE2, 0x20, 0x48, 0x09, 0xD0, 0xC3 }; // rdtsc; shl rdx,32; or rax,rdx; ret
				var mem = VirtualAlloc(IntPtr.Zero, (UIntPtr)64, 0x3000, 0x40);
				Marshal.Copy(code, 0, mem, code.Length);
				return (RdtscFn)Marshal.GetDelegateForFunctionPointer(mem, typeof(RdtscFn));
			}

			// QPC and TSC read back-to-back; keeps the best of several attempts.
			static void Pair(out long qpc, out ulong tsc)
			{
				var best = long.MaxValue;
				qpc = 0;
				tsc = 0;
				for (var i = 0; i < 50; i++)
				{
					QueryPerformanceCounter(out var q0);
					var t = Rdtsc();
					QueryPerformanceCounter(out var q1);
					if (q1 - q0 < best)
					{
						best = q1 - q0;
						qpc = q0 + (q1 - q0) / 2;
						tsc = t;
					}
				}
			}

			public static double MeasureHz(int ms)
			{
				QueryPerformanceFrequency(out var freq);
				Pair(out var q0, out var t0);
				Thread.Sleep(ms);
				Pair(out var q1, out var t1);
				return (t1 - t0) / ((double)(q1 - q0) / freq);
			}
		}
	}
}
