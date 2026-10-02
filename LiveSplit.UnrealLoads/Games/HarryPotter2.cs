using LiveSplit.ComponentUtil;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Management;

namespace LiveSplit.UnrealLoads.Games
{
	class HarryPotter2 : GameSupport
	{
		public override HashSet<string> GameNames { get; } = new HashSet<string>
		{
			"Harry Potter 2",
			"Harry Potter II",
			"Harry Potter and the Chamber of Secrets",
			"HP2",
			"HP 2",
			"HP II"
		};

		public override HashSet<string> ProcessNames { get; } = new HashSet<string>
		{
			"game"
		};

		public override HashSet<string> Maps { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"Adv1Willow",
			"Adv3DungeonQuest",
			"Adv4Greenhouse",
			"Adv6Goyle",
			"Adv7SlythComRoom",
			"Adv8Forest",
			"Adv9Aragog",
			"Adv11aCorridor",
			"Adv11bSecrets",
			"Adv12Chamber",
			"Arena",
			"BeanRewardRoom",
			"Ch1Rictusempra",
			"Ch2Skurge",
			"Ch3Diffindo",
			"Ch4Spongify",
			"Ch6WizardCard",
			"Ch7Gryffindor",
			"Credits",
			"Duel01",
			"Duel02",
			"Duel03",
			"Duel04",
			"Duel05",
			"Duel06",
			"Duel07",
			"Duel08",
			"Duel09",
			"Duel10",
			"Entryhall_hub",
			"FlyingFordCutScene",
			"Grandstaircase_hub",
			"GreatHall_G",
			"Grounds_hub",
			"Grounds_Night",
			"PrivetDr",
			"Quidditch",
			"Quidditch_Intro",
			"Sepia_Hallway",
			"Transition"
		};

		private readonly HashSet<int> _moduleMemorySizes = new HashSet<int>
		{
			704512,
			749568, // US
			674234 //no-cd
		};

		public override IdentificationResult IdentifyProcess(Process process)
		{
			if (_moduleMemorySizes.Contains(process.MainModuleWow64Safe().ModuleMemorySize)
				&& GetCommandLine(process).Contains("-SAVESLOT="))
				return IdentificationResult.Success;

			return IdentificationResult.Failure;
		}

		static string GetCommandLine(Process process)
		{
			using (var searcher = new ManagementObjectSearcher(
				"SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + process.Id))
			{
				StringBuilder commandLine = new StringBuilder();
				foreach (var @object in searcher.Get())
					commandLine.Append(@object["CommandLine"]).Append(" ");
				return commandLine.ToString();
			}
		}

		public override LoadMapDetour GetNewLoadMapDetour() => new HP2LoadMapDetour();

		public override SaveGameDetour GetNewSaveGameDetour() => new HP2SaveGameDetour();

		public override StatusDetour[] GetNewExtraDetours() => new StatusDetour[]
		{
			new HP2BeginLoadDetour(),
			new HP2TickDetour()
		};

		public override TimerAction[] OnMapLoad(MemoryWatcherList watchers)
		{
			var map = (StringWatcher)watchers["map"];

			if (map != null)
			{
				if (map.Current.Equals("privetdr.unr", StringComparison.OrdinalIgnoreCase)
					&& map.Old.Equals("privetdr.unr", StringComparison.OrdinalIgnoreCase))
				{
					return new TimerAction[] { TimerAction.Start };
				}
			}

			return null;
		}

		public override TimerAction[] OnDetach(Process game)
		{
			return new TimerAction[] { TimerAction.UnpauseGameTime };
		}
	}
}
