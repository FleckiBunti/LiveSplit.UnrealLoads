using System;

namespace LiveSplit.UnrealLoads.Games
{
	// HP3 keeps loading for up to ~1 s after UGameEngine::LoadMap returns due to precaching
	// We pause the timer (Status.PostLoad) until a tick finishes without precaching, and the
	// paused ticks get a fixed DeltaSeconds of 0.0005 to ensure a consistent gamestate.
	//
	// Flag (StatusPtr + 4): set when a Draw precached during the current tick.

	static class HP3Asm
	{
		public static string Status(IntPtr statusPtr) => statusPtr.ToBytes().ToHex();
		public static string Flag(IntPtr statusPtr) => (statusPtr + 4).ToBytes().ToHex();
		public static string PostLoad => LiveSplit.UnrealLoads.Status.PostLoad.ToBytes().ToHex();
		public static string None => LiveSplit.UnrealLoads.Status.None.ToBytes().ToHex();

		// status = PostLoad; flag = 1
		public static string EnterPostLoad(IntPtr statusPtr) => string.Join("\n",
			"C7 05 " + Status(statusPtr) + PostLoad,  // mov [status],PostLoad
			"C7 05 " + Flag(statusPtr) + "01000000"   // mov [flag],1
		);
	}

	public class HP3LoadMapDetour : LoadMapDetour
	{
		protected override string AfterLoad() => HP3Asm.EnterPostLoad(StatusPtr);
	}

	public class HP3SaveGameDetour : SaveGameDetour
	{
		protected override string AfterSave() => HP3Asm.EnterPostLoad(StatusPtr);
	}

	/// <summary>
	/// void UGameEngine::Tick(float DeltaSeconds)
	/// </summary>
	public class HP3TickDetour : StatusDetour
	{
		public override string Symbol => "?Tick@UGameEngine@@UAEXM@Z";
		public override string Module => "engine.dll";

		public override byte[] GetBytes()
		{
			var status = HP3Asm.Status(StatusPtr);
			var flag = HP3Asm.Flag(StatusPtr);

			var str = string.Join("\n",
				"55",                               // +00 push ebp
				"8B EC",                            // +01 mov ebp,esp
				"51",                               // +03 push ecx                  ; [ebp-4] = this
				"83 3D " + status + "03",           // +04 cmp dword ptr [status],PostLoad
				"75 11",                            // +0B jne +1E
				"C7 45 08 6F12033A",                // +0D mov dword ptr [ebp+8],0.0005f ; DeltaSeconds = 0.0005
				"C7 05 " + flag + "00000000",       // +14 mov dword ptr [flag],0
				"FF 75 08",                         // +1E push dword ptr [ebp+8]
				"8B 4D FC",                         // +21 mov ecx,dword ptr [ebp-4]
				"#FF FF FF FF FF",                  // +24 call UGameEngine::Tick
				"83 3D " + status + "03",           // +29 cmp dword ptr [status],PostLoad
				"75 13",                            // +30 jne +45
				"83 3D " + flag + "00",             // +32 cmp dword ptr [flag],0
				"75 0A",                            // +39 jne +45                   ; precached this tick, stay paused
				"C7 05 " + status + HP3Asm.None,    // +3B mov dword ptr [status],None
				"8B E5",                            // +45 mov esp,ebp
				"5D",                               // +47 pop ebp
				"C2 04 00"                          // +48 ret 4
			);

			var bytes = Utils.ParseBytes(str, out var offsets);
			_originalFuncCallOffset = offsets[0];

			return bytes.ToArray();
		}
	}

	/// <summary>
	/// void UGameEngine::Draw(UViewport* Viewport, int Blit, byte* HitData, int* HitSize)
	/// </summary>
	public class HP3DrawDetour : StatusDetour
	{
		public override string Symbol => "?Draw@UGameEngine@@UAEXPAVUViewport@@HPAEPAH@Z";
		public override string Module => "engine.dll";

		public override byte[] GetBytes()
		{
			var flag = HP3Asm.Flag(StatusPtr);

			// UGameEngine::Draw precaches when Viewport->[0x74]->[0x48] != 0 (Engine.dll).
			var str = string.Join("\n",
				"55",                               // +00 push ebp
				"8B EC",                            // +01 mov ebp,esp
				"51",                               // +03 push ecx                  ; [ebp-4] = this
				"8B 45 08",                         // +04 mov eax,dword ptr [ebp+8] ; Viewport
				"85 C0",                            // +07 test eax,eax
				"74 17",                            // +09 je +22
				"8B 40 74",                         // +0B mov eax,dword ptr [eax+74]
				"85 C0",                            // +0E test eax,eax
				"74 10",                            // +10 je +22
				"83 78 48 00",                      // +12 cmp dword ptr [eax+48],0  ; precache pending?
				"74 0A",                            // +16 je +22
				"C7 05 " + flag + "01000000",       // +18 mov dword ptr [flag],1
				"FF 75 14",                         // +22 push dword ptr [ebp+14]
				"FF 75 10",                         // +25 push dword ptr [ebp+10]
				"FF 75 0C",                         // +28 push dword ptr [ebp+C]
				"FF 75 08",                         // +2B push dword ptr [ebp+8]
				"8B 4D FC",                         // +2E mov ecx,dword ptr [ebp-4]
				"#FF FF FF FF FF",                  // +31 call UGameEngine::Draw
				"8B E5",                            // +36 mov esp,ebp
				"5D",                               // +38 pop ebp
				"C2 10 00"                          // +39 ret 10
			);

			var bytes = Utils.ParseBytes(str, out var offsets);
			_originalFuncCallOffset = offsets[0];

			return bytes.ToArray();
		}
	}
}
