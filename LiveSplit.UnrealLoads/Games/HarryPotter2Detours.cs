using System;
using System.Diagnostics;

namespace LiveSplit.UnrealLoads.Games
{
	// Used by HP1 and HP2 (Unreal Engine 1).
	//
	// StatusPtr: +4 - unused; +8 TSC when pause started; +16 paused TSC cycles

	static class HP2Asm
	{
		public static string Status(IntPtr statusPtr) => statusPtr.ToBytes().ToHex();
		public static string StartLo(IntPtr statusPtr) => (statusPtr + 8).ToBytes().ToHex();
		public static string StartHi(IntPtr statusPtr) => (statusPtr + 12).ToBytes().ToHex();
		public static string PausedLo(IntPtr statusPtr) => (statusPtr + 16).ToBytes().ToHex();
		public static string PausedHi(IntPtr statusPtr) => (statusPtr + 20).ToBytes().ToHex();
		public static string Hex(LiveSplit.UnrealLoads.Status s) => s.ToBytes().ToHex();

		// start = rdtsc (clobbers eax, edx)
		public static string StartPause(IntPtr statusPtr) => string.Join("\n",
			"0F 31",                                // rdtsc
			"A3 " + StartLo(statusPtr),             // mov [start],eax
			"89 15 " + StartHi(statusPtr)           // mov [start+4],edx
		);

		// paused += rdtsc - start (26 bytes, clobbers eax, edx)
		public static string EndPause(IntPtr statusPtr) => string.Join("\n",
			"0F 31",                                // rdtsc
			"2B 05 " + StartLo(statusPtr),          // sub eax,[start]
			"1B 15 " + StartHi(statusPtr),          // sbb edx,[start+4]
			"01 05 " + PausedLo(statusPtr),         // add [paused],eax
			"11 15 " + PausedHi(statusPtr)          // adc [paused+4],edx
		);
	}

	public class HP2LoadMapDetour : LoadMapDetour
	{
		protected override string BeforeLoad()
			=> "C7 05 " + HP2Asm.Status(StatusPtr) + HP2Asm.Hex(Status.Fading); // mov [status],Fading

		// If LoadMap started, add the paused time and enter PostLoad.
		// If LoadMap returned during the fade-out, nothing was paused.
		protected override string AfterLoad() => string.Join("\n",
			"83 3D " + HP2Asm.Status(StatusPtr) + "01",                    // +00 cmp dword ptr [status],LoadingMap
			"75 26",                                                       // +07 jne +2F
			HP2Asm.EndPause(StatusPtr),                                    // +09 paused += rdtsc - start
			"C7 05 " + HP2Asm.Status(StatusPtr) + HP2Asm.Hex(Status.PostLoad), // +23 mov [status],PostLoad
			"EB 0A",                                                       // +2D jmp +39
			"C7 05 " + HP2Asm.Status(StatusPtr) + HP2Asm.Hex(Status.None)  // +2F mov [status],None
		);
	}

	public class HP2SaveGameDetour : SaveGameDetour
	{
		protected override string BeforeSave() => string.Join("\n",
			"C7 05 " + HP2Asm.Status(StatusPtr) + HP2Asm.Hex(Status.Saving), // mov [status],Saving
			HP2Asm.StartPause(StatusPtr)
		);

		protected override string AfterSave() => string.Join("\n",
			HP2Asm.EndPause(StatusPtr),
			"C7 05 " + HP2Asm.Status(StatusPtr) + HP2Asm.Hex(Status.PostLoad) // mov [status],PostLoad
		);
	}

	/// <summary>
	/// static void UObject::BeginLoad()
	/// </summary>
	public class HP2BeginLoadDetour : StatusDetour
	{
		public override string Symbol => "?BeginLoad@UObject@@SAXXZ";
		public override string Module => "core.dll";

		public override byte[] GetBytes()
		{
			var status = HP2Asm.Status(StatusPtr);

			var str = string.Join("\n",
				"83 3D " + status + "04",                                // +00 cmp dword ptr [status],Fading
				"75 17",                                                 // +07 jne +20
				"C7 05 " + status + HP2Asm.Hex(Status.LoadingMap),       // +09 mov dword ptr [status],LoadingMap
				HP2Asm.StartPause(StatusPtr),                            // +13 start = rdtsc
				"#FF FF FF FF FF",                                       // +20 call UObject::BeginLoad
				"C3"                                                     // +25 ret
			);

			var bytes = Utils.ParseBytes(str, out var offsets);
			_originalFuncCallOffset = offsets[0];

			return bytes.ToArray();
		}
	}

	/// <summary>
	/// void UGameEngine::Tick(float DeltaSeconds)
	/// </summary>
	public class HP2TickDetour : StatusDetour
	{
		public override string Symbol => "?Tick@UGameEngine@@UAEXM@Z";
		public override string Module => "engine.dll";

		const string SECONDS_PER_CYCLE_SYMBOL = "?GSecondsPerCycle@@3MA"; // float in Core.dll
		const string TINY_DELTA = "6F12033A"; // 0.0005f: lower bound

		IntPtr _secondsPerCycle;

		public override void Install(Process process, IntPtr funcToDetour)
		{
			if (!GetExportTableParser(process, "core.dll").Exports.TryGetValue(SECONDS_PER_CYCLE_SYMBOL, out _secondsPerCycle))
				throw new Exception("Could not find " + SECONDS_PER_CYCLE_SYMBOL);

			base.Install(process, funcToDetour);
		}
		
		public override byte[] GetBytes()
		{
			var status = HP2Asm.Status(StatusPtr);
			var pausedLo = HP2Asm.PausedLo(StatusPtr);
			var pausedHi = HP2Asm.PausedHi(StatusPtr);

			var str = string.Join("\n",
				"55",                                       // +00 push ebp
				"8B EC",                                    // +01 mov ebp,esp
				"51",                                       // +03 push ecx                  ; [ebp-4] = this
				"A1 " + pausedLo,                           // +04 mov eax,[paused]
				"0B 05 " + pausedHi,                        // +09 or eax,[paused+4]
				"74 37",                                    // +0F je +48                    ; nothing paused
				"DF 2D " + pausedLo,                        // +11 fild qword ptr [paused]
				"D8 0D " + _secondsPerCycle.ToBytes().ToHex(), // +17 fmul dword ptr [GSecondsPerCycle]
				"D8 6D 08",                                 // +1D fsubr dword ptr [ebp+8]   ; DeltaSeconds - paused
				"D9 5D 08",                                 // +20 fstp dword ptr [ebp+8]
				"8B 45 08",                                 // +23 mov eax,[ebp+8]
				"3D " + TINY_DELTA,                         // +26 cmp eax,0.0005f           ; float bits compare like ints
				"7D 07",                                    // +2B jge +34
				"C7 45 08 " + TINY_DELTA,                   // +2D mov dword ptr [ebp+8],0.0005f
				"C7 05 " + pausedLo + "00000000",           // +34 mov [paused],0
				"C7 05 " + pausedHi + "00000000",           // +3E mov [paused+4],0
				"83 3D " + status + "03",                   // +48 cmp dword ptr [status],PostLoad
				"75 0A",                                    // +4F jne +5B
				"C7 05 " + status + HP2Asm.Hex(Status.None), // +51 mov dword ptr [status],None
				"FF 75 08",                                 // +5B push dword ptr [ebp+8]
				"8B 4D FC",                                 // +5E mov ecx,dword ptr [ebp-4]
				"#FF FF FF FF FF",                          // +61 call UGameEngine::Tick
				"8B E5",                                    // +66 mov esp,ebp
				"5D",                                       // +68 pop ebp
				"C2 04 00"                                  // +69 ret 4
			);

			var bytes = Utils.ParseBytes(str, out var offsets);
			_originalFuncCallOffset = offsets[0];

			return bytes.ToArray();
		}
	}
}
