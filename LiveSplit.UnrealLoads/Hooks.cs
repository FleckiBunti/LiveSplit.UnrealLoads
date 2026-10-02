using LiveSplit.ComponentUtil;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

namespace LiveSplit.UnrealLoads
{
	public enum StringType
	{
		ASCII,
		UTF16
	}

	public abstract class Detour
	{
		static Dictionary<int, Dictionary<string, ExportTableParser>> ExportsCache = new Dictionary<int, Dictionary<string, ExportTableParser>>();

		public virtual string Symbol { get; }
		public virtual string Module { get; }

		public IntPtr Pointer { get; protected set; }
		public IntPtr InjectedFuncPtr { get; protected set; }
		public IntPtr DetouredFuncPtr { get; protected set; }
		public bool Installed => InjectedFuncPtr != IntPtr.Zero;

		/// <summary>
		/// Returns true when the required object properties are set.
		/// </summary>
		protected virtual bool ReadyToInstall => true;
		protected virtual int OverwrittenBytes => 5;

		protected int _originalFuncCallOffset = -1;
		protected bool _usesTrampoline;

		public abstract byte[] GetBytes();

		public virtual void Install(Process process, IntPtr funcToDetour)
		{
			if (funcToDetour == IntPtr.Zero)
				throw new ArgumentException($"{nameof(funcToDetour)} cannot be IntPtr.Zero", nameof(funcToDetour));
			if (!ReadyToInstall)
				throw new InvalidOperationException("Not ready to install.");

			Pointer = funcToDetour;
			if (process != null && Pointer != IntPtr.Zero)
				DetouredFuncPtr = process.ReadJMP(Pointer);
			_usesTrampoline = DetouredFuncPtr == IntPtr.Zero;

			Debug.WriteLine($"[NoLoads] Hooking using {(_usesTrampoline ? "trampoline" : "thunk")}...");

			var bytes = GetBytes();

			if (InjectedFuncPtr == IntPtr.Zero)
				InjectedFuncPtr = process.AllocateMemory(bytes.Length);

			if (_usesTrampoline)
			{
				DetouredFuncPtr = process.WriteDetour(Pointer, OverwrittenBytes, InjectedFuncPtr);
			}
			else
			{
				// in case of thunks just replace them
				process.WriteJumpInstruction(Pointer, InjectedFuncPtr);
			}

			process.WriteBytes(InjectedFuncPtr, bytes);
			if (_originalFuncCallOffset >= 0)
				process.WriteCallInstruction(InjectedFuncPtr + _originalFuncCallOffset, DetouredFuncPtr);
		}

		public void Install(Process process)
		{
			var funcPtr = FindExportedFunc(process);
			if (funcPtr == IntPtr.Zero)
				throw new Exception("Could not find the exported function.");

			Install(process, funcPtr);
		}

		public virtual void Uninstall(Process process)
		{
			if (InjectedFuncPtr == IntPtr.Zero)
				throw new InvalidOperationException("Not installed.");

			if (_usesTrampoline)
			{
				if (DetouredFuncPtr == IntPtr.Zero)
					throw new InvalidOperationException("Not installed.");
				process.CopyMemory(DetouredFuncPtr, Pointer, OverwrittenBytes);
			}
			else
			{
				process.WriteJumpInstruction(Pointer, DetouredFuncPtr);
			}
		}

		public virtual void FreeMemory(Process process)
		{
			if (process == null || process.HasExited)
				return;

			process.FreeMemory(InjectedFuncPtr);
			InjectedFuncPtr = IntPtr.Zero;
			if (_usesTrampoline)
			{
				process.FreeMemory(DetouredFuncPtr);
				DetouredFuncPtr = IntPtr.Zero;
			}
		}

		public static ExportTableParser GetExportTableParser(Process process, string module)
		{
			module = module?.ToLower() ?? string.Empty;

			if (!ExportsCache.TryGetValue(process.Id, out var tables))
			{
				tables = new Dictionary<string, ExportTableParser>();
				ExportsCache.Add(process.Id, tables);
			}

			if (!tables.TryGetValue(module, out var parser))
			{
				parser = new ExportTableParser(process, module);
				parser.Parse();
				tables.Add(module, parser);
			}

			return parser;
		}

		public IntPtr FindExportedFunc(Process process)
		{
			if (Symbol == null)
				throw new Exception("No symbol defined");

			var exportParser = GetExportTableParser(process, Module);
			if (exportParser.Exports.TryGetValue(Symbol, out var ptr))
				return ptr;
			else
				return IntPtr.Zero;
		}
	}

	public class LoadMapDetour : Detour
	{
		public override string Symbol => "?LoadMap@UGameEngine@@UAEPAVULevel@@ABVFURL@@PAVUPendingLevel@@PBV?$TMap@VFString@@V1@@@AAVFString@@@Z";
		public override string Module => "engine.dll";
		public virtual StringType Encoding => StringType.UTF16;

		protected override bool ReadyToInstall => base.ReadyToInstall
			&& SetMapPtr != IntPtr.Zero && StatusPtr != IntPtr.Zero;

		public IntPtr SetMapPtr { get; set; }
		public IntPtr StatusPtr { get; set; }

		protected int _setMapCallOffset;

		public override void Install(Process process, IntPtr funcToDetour)
		{
			base.Install(process, funcToDetour);
			process.WriteCallInstruction(InjectedFuncPtr + _setMapCallOffset, SetMapPtr);
		}

		public override byte[] GetBytes()
		{
			var status = StatusPtr.ToBytes().ToHex();
			var none = Status.None.ToBytes().ToHex();
			var loadingMap = Status.LoadingMap.ToBytes().ToHex();

			var str = string.Join("\n",
				"55",                           // push ebp
				"8B EC",                        // mov ebp,esp
				"83 EC 10",                     // sub esp,10
				"89 55 F0",                     // mov dword ptr ds:[ebp-10],edx
				"89 4D F8",                     // mov dword ptr ds:[ebp-8],ecx
				"8B 45 08",                     // mov eax,dword ptr ds:[ebp+8]
				"8B 48 1C",                     // mov ecx,dword ptr ds:[eax+1C]
				"89 4D FC",                     // mov dword ptr ds:[ebp-4],ecx
				"8B 55 FC",                     // mov edx,dword ptr ds:[ebp-4]
				"52",                           // push edx
				"#FF FF FF FF FF",              // call set_map
				"83 C4 04",                     // add esp,4
				BeforeLoad(),                   //
				"8B 45 14",                     // mov eax,dword ptr ds:[ebp+14]
				"50",                           // push eax
				"8B 4D 10",                     // mov ecx,dword ptr ds:[ebp+10]
				"51",                           // push ecx
				"8B 55 0C",                     // mov edx,dword ptr ds:[ebp+C]
				"52",                           // push edx
				"8B 45 08",                     // mov eax,dword ptr ds:[ebp+8]
				"50",                           // push eax
				"8B 4D F8",                     // mov ecx,dword ptr ds:[ebp-8]
				"#FF FF FF FF FF",              // call dword ptr ds:[B3780]
				"89 45 F4",                     // mov dword ptr ds:[ebp-C],eax
				AfterLoad(),                    //
				"8B 45 F4",                     // mov eax,dword ptr ds:[ebp-C]
				"8B E5",                        // mov esp,ebp
				"5D",                           // pop ebp
				"C2 10 00"                      // ret 10
			);

			var bytes = Utils.ParseBytes(str, out var offsets);
			_setMapCallOffset = offsets[0];
			_originalFuncCallOffset = offsets[1];

			return bytes.ToArray();
		}

		/// <summary>
		/// Instructions executed before the original LoadMap is called (no calls!)
		/// </summary>
		protected virtual string BeforeLoad()
			=> "C7 05 " + StatusPtr.ToBytes().ToHex() + Status.LoadingMap.ToBytes().ToHex(); // mov [status],LoadingMap

		/// <summary>
		/// Instructions executed after the original LoadMap returned (no rel32 or calls!)
		/// </summary>
		protected virtual string AfterLoad()
			=> "C7 05 " + StatusPtr.ToBytes().ToHex() + Status.None.ToBytes().ToHex(); // mov [status],None
	}

	public class SaveGameDetour : Detour
	{
		public override string Symbol => "?SaveGame@UGameEngine@@UAEXH@Z";
		public override string Module => "engine.dll";

		protected override bool ReadyToInstall => base.ReadyToInstall
			&& StatusPtr != IntPtr.Zero;

		public IntPtr StatusPtr { get; set; }

		public override byte[] GetBytes()
		{
			var status = StatusPtr.ToBytes().ToHex();
			var none = Status.None.ToBytes().ToHex();
			var saving = Status.Saving.ToBytes().ToHex();

			var str = string.Join("\n",
				"55",                               // PUSH EBP
				"8B EC",                            // MOV EBP,ESP
				"83 EC 08",                         // SUB ESP,8
				"89 55 F8",                         // MOV DWORD PTR SS:[EBP-8],EDX
				"89 4D FC",                         // MOV DWORD PTR SS:[EBP-4],ECX
				BeforeSave(),                       //
				"8B 45 08",                         // MOV EAX,DWORD PTR SS:[EBP+8]
				"50",                               // PUSH EAX
				"8B 4D FC",                         // MOV ECX,DWORD PTR SS:[EBP-4]
				"#FF FF FF FF FF",                  // CALL DWORD PTR DS:[SaveGame] (placeholder)
				AfterSave(),                        //
				"8B E5",                            // MOV ESP,EBP
				"5D",                               // POP EBP
				"C2 04 00"                          // RETN 4
			);

			var bytes = Utils.ParseBytes(str, out var offsets);
			_originalFuncCallOffset = offsets[0];

			return bytes.ToArray();
		}

		/// <summary>
		/// Instructions executed before the original SaveGame is called (no calls!)
		/// </summary>
		protected virtual string BeforeSave()
			=> "C7 05 " + StatusPtr.ToBytes().ToHex() + Status.Saving.ToBytes().ToHex(); // mov [status],Saving

		/// <summary>
		/// Instructions executed after the original SaveGame returned (no rel32 or calls!)
		/// </summary>
		protected virtual string AfterSave()
			=> "C7 05 " + StatusPtr.ToBytes().ToHex() + Status.None.ToBytes().ToHex(); // mov [status],None
	}

	/// <summary>
	/// Game specific detour that shares the status memory with the LoadMap/SaveGame detours.
	/// </summary>
	public abstract class StatusDetour : Detour
	{
		protected override bool ReadyToInstall => base.ReadyToInstall
			&& StatusPtr != IntPtr.Zero;

		public IntPtr StatusPtr { get; set; }
	}

	class SetMapUTF16Function
	{
		public byte[] Bytes { get; private set; }
		public IntPtr InjectedFuncPtr { get; private set; }

		public IntPtr Inject(Process process)
		{
			InjectedFuncPtr = process.AllocateMemory(Bytes.Length);
			process.WriteBytes(InjectedFuncPtr, Bytes);
			return InjectedFuncPtr;
		}

		public void FreeMemory(Process process)
		{
			if (process == null || process.HasExited)
				return;

			process.FreeMemory(InjectedFuncPtr);
			InjectedFuncPtr = IntPtr.Zero;
		}

		public SetMapUTF16Function(IntPtr mapAddr)
		{
			var map = mapAddr.ToBytes().ToHex();

			var str = string.Join("\n",
				"55",                  // push ebp
				"8B EC",               // mov ebp,esp
				"51",                  // push ecx
				"C7 45 FC 00000000",   // mov [ebp-04],00000000 { 0 }
				"EB 09",               // jmp hooks.set_map_utf16+16
				"8B 45 FC",            // mov eax,[ebp-04]
				"83 C0 01",            // add eax,01 { 1 }
				"89 45 FC",            // mov [ebp-04],eax
				"81 7D FC 04010000",   // cmp [ebp-04],00000104 { 260 }
				"7D 27",               // jnl hooks.set_map_utf16+46
				"8B 4D FC",            // mov ecx,[ebp-04]
				"8B 55 FC",            // mov edx,[ebp-04]
				"8B 45 08",            // mov eax,[ebp+08]
				"66 8B 14 50",         // mov dx,[eax+edx*2]
				"66 89 14 4D " + map,  // mov [ecx*2+hooks.g_map],dx
				"8B 45 FC",            // mov eax,[ebp-04]
				"8B 4D 08",            // mov ecx,[ebp+08]
				"0FB7 14 41",          // movzx edx,word ptr [ecx+eax*2]
				"85 D2",               // test edx,edx
				"75 02",               // jne hooks.set_map_utf16+44
				"EB 02",               // jmp hooks.set_map_utf16+46
				"EB C7",               // jmp hooks.set_map_utf16+D
				"8B E5",               // mov esp,ebp
				"5D",                  // pop ebp
				"C3"                   // ret
			);

			Bytes = Utils.ParseBytes(str).ToArray();
		}
	}

	public class SetMapASCIIFunction
	{
		public byte[] Bytes { get; private set; }
		public IntPtr InjectedFuncPtr { get; private set; }

		public IntPtr Inject(Process process)
		{
			InjectedFuncPtr = process.AllocateMemory(Bytes.Length);
			process.WriteBytes(InjectedFuncPtr, Bytes);
			return InjectedFuncPtr;
		}

		public void FreeMemory(Process process)
		{
			if (process == null || process.HasExited)
				return;

			process.FreeMemory(InjectedFuncPtr);
			InjectedFuncPtr = IntPtr.Zero;
		}

		public SetMapASCIIFunction(IntPtr mapAddr)
		{
			var map = mapAddr.ToBytes().ToHex();

			var str = string.Join("\n",
				"55",                   // push ebp
				"8B EC",                // mov ebp, esp
				"51",                   // push ecx
				"C7 45 FC 00000000",    // mov [ebp-04],00000000 { 0 }
				"EB 09",                // jmp hooks.set_map_ascii+16
				"8B 45 FC",             // mov eax,[ebp-04]
				"83 C0 01",             // add eax,01 { 1 }
				"89 45 FC",             // mov [ebp-04],eax
				"81 7D FC 04010000",    // cmp [ebp-04],00000104 { 260 }
				"7D 22",                // jnl hooks.set_map_ascii+41
				"8B 4D 08",             // mov ecx,[ebp+08]
				"03 4D FC",             // add ecx,[ebp-04]
				"8B 55 FC",             // mov edx,[ebp-04]
				"8A 01",                // mov al,[ecx]
				"88 82 " + map,         // mov [edx+hooks.g_map],al
				"8B 4D 08",             // mov ecx,[ebp+08]
				"03 4D FC",             // add ecx,[ebp-04]
				"0FBE 11",              // movsx edx,byte ptr [ecx]
				"85 D2",                // test edx,edx
				"75 02",                // jne hooks.set_map_ascii+3F
				"EB 02",                // jmp hooks.set_map_ascii+41
				"EB CC",                // jmp hooks.set_map_ascii+D
				"8B E5",                // mov esp,ebp
				"5D",                   // pop ebp
				"C3"                    // ret
			);

			Bytes = Utils.ParseBytes(str).ToArray();
		}

	}
}
