using System;
using System.Diagnostics;

namespace LiveSplit.UnrealLoads.Games
{
	// Used by HP1 and HP2 (Unreal Engine 1).
	//
	// StatusPtr: +4 flag (first full tick after a load/save is running); +8 TSC when pause started; +16 paused TSC cycles

	static class HP2Asm
	{
		public static string Status(IntPtr statusPtr) => statusPtr.ToBytes().ToHex();
		public static string Flag(IntPtr statusPtr) => (statusPtr + 4).ToBytes().ToHex();
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

		// If a level was loaded, the pause keeps running into PostLoad 
		// If LoadMap returned during the fade-out, nothing was paused.
		// If it failed (reload), the pause is dropped. eax = returned ULevel* (restored by LoadMapDetour).
		protected override string AfterLoad() => string.Join("\n",
			"83 3D " + HP2Asm.Status(StatusPtr) + "01",                    // +00 cmp dword ptr [status],LoadingMap
			"75 1A",                                                       // +07 jne +23
			"85 C0",                                                       // +09 test eax,eax
			"74 16",                                                       // +0B je +23                  ; failed (reload)
			"C7 05 " + HP2Asm.Status(StatusPtr) + HP2Asm.Hex(Status.PostLoad), // +0D mov [status],PostLoad
			"C7 05 " + HP2Asm.Flag(StatusPtr) + "00000000",                // +17 mov [flag],0
			"EB 0A",                                                       // +21 jmp +2D
			"C7 05 " + HP2Asm.Status(StatusPtr) + HP2Asm.Hex(Status.None)  // +23 mov [status],None
		);
	}

	public class HP2SaveGameDetour : SaveGameDetour
	{
		// Start a pause unless one is already running (save right after a load).
		protected override string BeforeSave() => string.Join("\n",
			"83 3D " + HP2Asm.Status(StatusPtr) + "00",                    // +00 cmp dword ptr [status],None
			"75 0D",                                                       // +07 jne +16
			HP2Asm.StartPause(StatusPtr),                                  // +09 start = rdtsc
			"C7 05 " + HP2Asm.Status(StatusPtr) + HP2Asm.Hex(Status.Saving) // +16 mov [status],Saving
		);

		// The pause keeps running into PostLoad (the next frame precaches).
		protected override string AfterSave() => string.Join("\n",
			"C7 05 " + HP2Asm.Status(StatusPtr) + HP2Asm.Hex(Status.PostLoad), // mov [status],PostLoad
			"C7 05 " + HP2Asm.Flag(StatusPtr) + "00000000"                 // mov [flag],0
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
			var flag = HP2Asm.Flag(StatusPtr);
			var pausedLo = HP2Asm.PausedLo(StatusPtr);
			var pausedHi = HP2Asm.PausedHi(StatusPtr);

			var str = string.Join("\n",
				"55",                                       // +00 push ebp
				"8B EC",                                    // +01 mov ebp,esp
				"51",                                       // +03 push ecx                  ; [ebp-4] = this
				"83 3D " + status + "03",                   // +04 cmp dword ptr [status],PostLoad
				"75 31",                                    // +0B jne +3E
				HP2Asm.EndPause(StatusPtr),                 // +0D paused += rdtsc - start   ; include the time until now
				HP2Asm.StartPause(StatusPtr),               // +27 start = rdtsc             ; and pause this tick too
				"C7 05 " + flag + "01000000",               // +34 mov [flag],1
				"A1 " + pausedLo,                           // +3E mov eax,[paused]
				"0B 05 " + pausedHi,                        // +43 or eax,[paused+4]
				"74 37",                                    // +49 je +82                    ; nothing paused
				"DF 2D " + pausedLo,                        // +4B fild qword ptr [paused]
				"D8 0D " + _secondsPerCycle.ToBytes().ToHex(), // +51 fmul dword ptr [GSecondsPerCycle]
				"D8 6D 08",                                 // +57 fsubr dword ptr [ebp+8]   ; DeltaSeconds - paused
				"D9 5D 08",                                 // +5A fstp dword ptr [ebp+8]
				"8B 45 08",                                 // +5D mov eax,[ebp+8]
				"3D " + TINY_DELTA,                         // +60 cmp eax,0.0005f           ; float bits compare like ints
				"7D 07",                                    // +65 jge +6E
				"C7 45 08 " + TINY_DELTA,                   // +67 mov dword ptr [ebp+8],0.0005f
				"C7 05 " + pausedLo + "00000000",           // +6E mov [paused],0
				"C7 05 " + pausedHi + "00000000",           // +78 mov [paused+4],0
				"FF 75 08",                                 // +82 push dword ptr [ebp+8]
				"8B 4D FC",                                 // +85 mov ecx,dword ptr [ebp-4]
				"#FF FF FF FF FF",                          // +88 call UGameEngine::Tick
				"83 3D " + flag + "00",                     // +8D cmp dword ptr [flag],0    ; first tick after a load/save?
				"74 2E",                                    // +94 je +C4                    ; (cleared if it loaded/saved again)
				HP2Asm.EndPause(StatusPtr),                 // +96 paused += rdtsc - start   ; subtracted from the next tick
				"C7 05 " + status + HP2Asm.Hex(Status.None), // +B0 mov dword ptr [status],None
				"C7 05 " + flag + "00000000",               // +BA mov dword ptr [flag],0
				"8B E5",                                    // +C4 mov esp,ebp
				"5D",                                       // +C6 pop ebp
				"C2 04 00"                                  // +C7 ret 4
			);

			var bytes = Utils.ParseBytes(str, out var offsets);
			_originalFuncCallOffset = offsets[0];

			return bytes.ToArray();
		}
	}
}
