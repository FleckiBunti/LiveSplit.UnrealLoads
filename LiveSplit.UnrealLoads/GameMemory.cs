using LiveSplit.ComponentUtil;
using LiveSplit.UnrealLoads.Games;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LiveSplit.UnrealLoads
{
	enum Status
	{
		None = 0,
		LoadingMap = 1,
		Saving = 2,
		PostLoad = 3, // map loaded, but the game is still stalled finishing the load (e.g. precaching)
		Fading = 4 // inside LoadMap, but still playing the fade-out before loading (not a load)
	}

	public class GameMemory : IGameMemory
	{
		public const int SLEEP_TIME = 15;

		readonly GameSupport[] SupportedGames;
		readonly string[] SupportedProcessesNames;

		public event EventHandler OnReset;
		public event EventHandler OnStart;
		public event EventHandler OnSplit;
		public event EventHandler OnLoadStarted;
		public event EventHandler OnLoadEnded;
		public event MapChangeEventHandler OnMapChange;

		private GameSupport Game { get; set; }

		Task _thread;
		CancellationTokenSource _cancelSource;
		SynchronizationContext _uiThread;
		HashSet<int> _ignorePIDs;
		int _lastPID;
		MemoryWatcherList _watchers;
		MemoryWatcher<Status> _status;
		StringWatcher _map;

		IntPtr _setMapPtr;
		LoadMapDetour _loadMapHook;
		SaveGameDetour _saveGameHook;
		StatusDetour[] _extraHooks = new StatusDetour[0];
		IntPtr _statusPtr;
		IntPtr _mapPtr;

		readonly int MAP_SIZE = Encoding.Unicode.GetMaxByteCount(260); // MAX_PATH == 260

		public GameMemory(IEnumerable<GameSupport> supportedGames)
		{
			SupportedGames = supportedGames.ToArray();
			SupportedProcessesNames = SupportedGames.SelectMany(g => g.ProcessNames
				.Select(pName => pName.ToLower()))
				.ToArray();

			_ignorePIDs = new HashSet<int>();
			_watchers = new MemoryWatcherList();
		}

		public void StartMonitoring()
		{
			if (_thread != null && _thread.Status == TaskStatus.Running)
				throw new InvalidOperationException();

			if (!(SynchronizationContext.Current is WindowsFormsSynchronizationContext))
				throw new InvalidOperationException("SynchronizationContext.Current is not a UI thread.");

			_uiThread = SynchronizationContext.Current;
			_cancelSource = new CancellationTokenSource();
			_thread = Task.Factory.StartNew(MemoryReadThread);
		}

		public void Stop()
		{
			if (_cancelSource == null || _thread == null || _thread.Status != TaskStatus.Running)
				return;

			_cancelSource.Cancel();
			_thread.Wait();
		}

		void MemoryReadThread()
		{
			Process game = null;

			while (!_cancelSource.IsCancellationRequested)
			{
				try
				{
					Trace.WriteLine("[NoLoads] Waiting for a game process...");

					while ((game = GetGameProcess()) == null)
					{
						Thread.Sleep(250);
						if (_cancelSource.IsCancellationRequested)
							return;
					}

					Trace.WriteLine($"[NoLoads] Attached to {game.ProcessName}.exe ({Game.GetType().Name})");
					_uiThread.Post(d => OnLoadEnded?.Invoke(this, EventArgs.Empty), null); //unpause at launch

					uint frameCounter = 0;
					bool isLoading;
					bool prevIsLoading = false;
					var map = string.Empty;
					var prevMap = string.Empty;
					string pendingMap = null;
					var pendingActions = new List<TimerAction>();

					DoTimerAction(Game.OnAttach(game));

					while (!game.HasExited)
					{
						_watchers.UpdateAll(game);
						DoTimerAction(Game.OnUpdate(game, _watchers));

						var gameSupportIsLoading = Game.IsLoading(_watchers);
						if (!gameSupportIsLoading.HasValue)
							isLoading = _status.Current != Status.None && _status.Current != Status.Fading;
						else
							isLoading = gameSupportIsLoading.Value;

						Debug.WriteLineIf(_status.Changed, string.Format("[NoLoads] Status changed from {1} to {2} - {0}", frameCounter, _status.Old, _status.Current));

						// While a game plays the fade-out, the actions are held back so the split happens after the fade
						var fading = _status.Current == Status.Fading;

						if (_map.Changed)
							pendingMap = _map.Current;

						if (pendingMap != null && !fading)
						{
							string mapname = Path.GetFileNameWithoutExtension(pendingMap);
							string extension = Path.GetExtension(pendingMap);

							Debug.WriteLine($"[NoLoads] Map changed to {pendingMap}, Type: {extension}");

							if ((Game.Maps.Count == 0 || Game.Maps.Contains(mapname))
								&& (Game.MapExtension == null || extension.Equals(Game.MapExtension, StringComparison.OrdinalIgnoreCase)))
							{
								prevMap = map;
								map = mapname;

								var from = prevMap;
								var to = map;
								_uiThread.Post(d => OnMapChange?.Invoke(this, from, to), null);

								Debug.WriteLine("[NoLoads] Map is changing from \"{0}\" to \"{1}\" - {2}", prevMap, map, frameCounter);
							}

							pendingMap = null;
						}

						// a map load starts with Fading (if the game has a fade-out) or directly with LoadingMap
						if (_status.Changed && _status.Old != Status.Fading
							&& (_status.Current == Status.LoadingMap || _status.Current == Status.Fading))
						{
							var actions = Game.OnMapLoad(_watchers);
							if (actions != null)
								pendingActions.AddRange(actions);
						}

						if (pendingActions.Count > 0 && !fading)
						{
							DoTimerAction(pendingActions);
							pendingActions.Clear();
						}

						if (isLoading != prevIsLoading)
						{
							if (isLoading)
							{
								_uiThread.Post(d => OnLoadStarted?.Invoke(this, EventArgs.Empty), null);
								Trace.WriteLine(string.Format("[NoLoads] Load start - {0}", frameCounter));
							}
							else
							{
								_uiThread.Post(d => OnLoadEnded?.Invoke(this, EventArgs.Empty), null);
								Trace.WriteLine(string.Format("[NoLoads] Load end - {0}", frameCounter));
							}
						}

						prevIsLoading = isLoading;
						frameCounter++;

						Thread.Sleep(SLEEP_TIME);

						if (_cancelSource.IsCancellationRequested)
							break;
					}

					DoTimerAction(Game.OnDetach(game));
				}
				catch (Exception ex)
				{
					Trace.WriteLine(ex.ToString());
					Thread.Sleep(1000);
				}
			}

			Unpatch(game);
		}

		void DoTimerAction(TimerAction action)
		{
			EventHandler evnt = null;
			switch (action)
			{
				case TimerAction.DoNothing:
					break;
				case TimerAction.Reset:
					evnt = OnReset;
					break;
				case TimerAction.Start:
					evnt = OnStart;
					break;
				case TimerAction.Split:
					evnt = OnSplit;
					break;
				case TimerAction.PauseGameTime:
					evnt = OnLoadStarted;
					break;
				case TimerAction.UnpauseGameTime:
					evnt = OnLoadEnded;
					break;
			}

			_uiThread.Post(d => evnt?.Invoke(this, EventArgs.Empty), null);
		}

		void DoTimerAction(IEnumerable<TimerAction> actions)
		{
			if (actions == null)
				return;

			foreach (var action in actions)
				DoTimerAction(action);
		}

		Process GetGameProcess()
		{
			Process game = null;

			var processes = SupportedProcessesNames.SelectMany(n => Process.GetProcessesByName(n))
				.Where(x => !x.HasExited)
				.OrderByDescending(p => p.StartTime);

			foreach (var p in processes)
			{
				if (p.HasExited || _ignorePIDs.Contains(p.Id))
					continue;

				var ignoreProcess = true;
				foreach (var gameSupport in SupportedGames)
				{
					if (!gameSupport.ProcessNames.Contains(p.ProcessName.ToLower()))
						continue;

					var modules = p.ModulesWow64Safe();
					if (!gameSupport.GetHookModules().All(hook_m =>
								modules.Any(m => hook_m.ToLower() == m.ModuleName.ToLower())))
					{
						ignoreProcess = false;
						continue;
					}

					switch (gameSupport.IdentifyProcess(p))
					{
						case IdentificationResult.Success:
							game = p;
							Game = gameSupport;
							break;
						case IdentificationResult.Undecisive:
							ignoreProcess = false; // don't ignore if at least one game is unsure
							break;
					}

					if (game != null)
						break;
				}

				if (game != null)
					break;

				if (ignoreProcess)
					_ignorePIDs.Add(p.Id);
			}

			if (game == null)
				return null;

			if (_lastPID != game.Id)
			{
				_watchers.Clear();

				if (!Patch(game))
					return null;

				var stringType = _loadMapHook == null || _loadMapHook.Encoding == StringType.ASCII
					? ReadStringType.ASCII
					: ReadStringType.UTF16;
				_map = new StringWatcher(_mapPtr, stringType, MAP_SIZE) { Name = "map" };

				_status = new MemoryWatcher<Status>(_statusPtr) { Name = "status" };
				_watchers.AddRange(new MemoryWatcher[] { _status, _map });
			}

			_lastPID = game.Id;
			return game;
		}

		bool Patch(Process game)
		{
			game.Suspend();
			try
			{
				_statusPtr = game.AllocateMemory(24); // status + spare memory for StatusDetours
				_mapPtr = game.AllocateMemory(MAP_SIZE);

				_loadMapHook = Game.GetNewLoadMapDetour();
				if (_loadMapHook != null)
				{
					_loadMapHook.StatusPtr = _statusPtr;

					var setMapFunction = new SetMapUTF16Function(_mapPtr);
					setMapFunction.Inject(game);
					_setMapPtr = _loadMapHook.Encoding == StringType.UTF16
						? new SetMapUTF16Function(_mapPtr).Inject(game)
						: new SetMapASCIIFunction(_mapPtr).Inject(game);
					_loadMapHook.SetMapPtr = _setMapPtr;
				}

				_saveGameHook = Game.GetNewSaveGameDetour();
				if (_saveGameHook != null)
				{
					_saveGameHook.StatusPtr = _statusPtr;
				}

				_extraHooks = Game.GetNewExtraDetours();
				foreach (var hook in _extraHooks)
					hook.StatusPtr = _statusPtr;

				_loadMapHook?.Install(game);
				_saveGameHook?.Install(game);
				foreach (var hook in _extraHooks)
					hook.Install(game);

				Debug.WriteLine($"[NoLoads] Status: {_statusPtr.ToString("X")} Map: {_mapPtr.ToString("X")} ");
				Debug.WriteLine($"[NoLoads] FakeSaveGame: {_saveGameHook?.InjectedFuncPtr.ToString("X")} FakeLoadMap: {_loadMapHook?.InjectedFuncPtr.ToString("X")}");
				Debug.WriteLine($"[NoLoads] SaveGame: {_saveGameHook?.DetouredFuncPtr.ToString("X")} LoadMap: {_loadMapHook?.DetouredFuncPtr.ToString("X")}");
				Debug.WriteLine("[NoLoads] Hooks installed");
			}
			catch (Exception e)
			{
				Debug.WriteLine(e.ToString());
				UninstallHooks(game);
				FreeMemory(game);
				return false;
			}
			finally
			{
				game.Resume();
			}

			return true;
		}

		bool Unpatch(Process game)
		{
			if (game == null || game.HasExited)
				return false;

			game.Suspend();
			try
			{
				UninstallHooks(game);
			}
			catch
			{
				Debug.WriteLine("[NoLoads] Unpatching failed.");
				return false;
			}
			finally
			{
				// do not free memory; a function that is wrapped by a detour can still run and would segfault. the memory is released when the game is quit
				game.Resume();
			}

			return true;
		}

		void UninstallHooks(Process game)
		{
			if (_loadMapHook != null & _loadMapHook.Installed)
				_loadMapHook.Uninstall(game);

			if (_saveGameHook != null && _saveGameHook.Installed)
				_saveGameHook.Uninstall(game);

			foreach (var hook in _extraHooks)
			{
				if (hook.Installed)
					hook.Uninstall(game);
			}
		}

		void FreeMemory(Process game)
		{
			if (game == null || game.HasExited)
				return;

			game.FreeMemory(_statusPtr);
			game.FreeMemory(_mapPtr);
			game.FreeMemory(_setMapPtr);
			_saveGameHook?.FreeMemory(game);
			_loadMapHook?.FreeMemory(game);
			foreach (var hook in _extraHooks)
				hook.FreeMemory(game);
		}
	}
}
