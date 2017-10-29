/*
	Copyright 2015-2017 Sin Jeong-hun, Jay Satiro
	MIT Licence.
	https://github.com/HubKing/LinkBrightness
 */
using System;
using System.Diagnostics;
using System.Drawing;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LinkBrightness
{
	class Program
	{
		static readonly string ProgName = "LinkBrightness";

		/* SynchronizationContext is used to allow other threads to run code in the Main UI thread.
		 * It is created later because creating it here causes a bit of a delay in hiding the
		 * console when option /hide_on_start is used.
		 */
		static SynchronizationContext UISyncContext;

		static readonly string AppGuid = ((GuidAttribute)typeof(Program).Assembly.GetCustomAttributes(typeof(GuidAttribute), false)[0]).Value.ToUpper();
		static readonly string SessionInstanceMutexName = "Local\\" + ProgName + ".Session.Instance.{" + AppGuid + "}";
		static readonly string RestoreConsoleEventName = "Local\\" + ProgName + ".Restore.Console.{" + AppGuid + "}";
		static EventWaitHandle RestoreConsoleEvent = new EventWaitHandle(
			false,                                // Create handle in an unsignaled state
			EventResetMode.AutoReset,             // Once signaled auto-reset to unsignaled
			RestoreConsoleEventName
		);
		static readonly string SyncBrightnessMutexName = "Global\\" + ProgName + ".Sync.Brightness.{" + AppGuid + "}";
		static Mutex SyncBrightnessMutex = null;

		static readonly Guid VideoSubgroup = new Guid("7516b95f-f776-4464-8c53-06167f40cc99");
		static readonly Guid BrightnessKey = new Guid("aded5e82-b909-4619-9949-f5d71dac0bcb");
		static readonly IntPtr NULL = IntPtr.Zero; // Remember NULL != null in C#

		static bool we_own_the_console = false;
		static bool opt_hide_on_start = false;
		static bool opt_hide_on_minimize = false;
		static bool opt_verbose = false;

		public static void ShowUsage()
		{
			Console.WriteLine(
				"\n" +
				"Usage: " + ProgName + " [/hide_on_start] [/hide_on_minimize] [/verbose]\n" +
				"\n" +
				"If " + ProgName + " is running in its own console window (ie not with another " +
				"process attached such as command prompt) then it has an icon in the system " +
				"tray. You can show/hide the window by double-clicking the tray icon.\n" +
				"\n" +
				"[/hide_on_minimize]\n" +
				"Hide the console window when it is minimized. This only works if " +
				ProgName + " is running in its own console window. (Note when this option is " +
				"enabled and the console window is visible it will not be hidden during a " +
				"group-style minimize command (Win+D, Win+M, etc), and will remain minimized " +
				"during a reversal.)\n" +
				"\n" +
				"[/hide_on_start]\n" +
				"Hide the console window on start. This only works if " + ProgName + " is " +
				"running in its own console window. (Note when this option is enabled the " +
				"console window may flicker on start because it cannot be hidden until after " +
				"it has been created. You can mitigate that by running " + ProgName +
				" /hide_on_start from a shortcut with Run properties set to 'Minimized'.)\n" +
				"\n" +
				"[/verbose]\n" +
				"Be more verbose, such as show every brightness event.\n"
			);
		}

		///<summary>
		/// Wait for a keypress if we_own_the_console and it's visible.
		///</summary>
		static void SoftPause()
		{
			IntPtr console = GetConsoleWindow();
			if (we_own_the_console && IsWindowVisible(console) && !IsIconic(console)) {
				while (Console.KeyAvailable) {
					Console.ReadKey(true);
				}
				Console.WriteLine("Press any key to continue . . . ");
				Console.ReadKey(true);
			}
		}

		public static int Main(string[] args)
		{
			const int EXIT_SUCCESS = 0, EXIT_FAILURE = 1;

			/* Check if we "own" the console. MainWindowHandle is NULL if we are not the first
			 * process currently attached to an existing console.
			 */
			we_own_the_console = ((GetConsoleProcessList(new uint[1], 1) == 1) &&
				(Process.GetCurrentProcess().MainWindowHandle == GetConsoleWindow()));

			// Parse arguments
			for (int i = 0; i < args.Length; ++i)
			{
				if (args[i] == "/?" || args[i] == "--help") {
					ShowUsage();
					SoftPause();
					return EXIT_SUCCESS;
				}
				else if (args[i] == "/hide_on_start")
					opt_hide_on_start = true;
				else if (args[i] == "/hide_on_minimize")
					opt_hide_on_minimize = true;
				else if (args[i] == "/verbose")
					opt_verbose = true;
				else {
					Console.Error.WriteLine("Error: Unrecognized option: " + args[i]);
					Console.Error.WriteLine("Use option /? for usage information.");
					SoftPause();
					return EXIT_FAILURE;
				}
			}

			// Limit the program to a single instance per session (normally.. see below)
			using (var SessionInstanceMutex = new Mutex(true, SessionInstanceMutexName))
			{
				bool already_running = false;

				try {
					if (!SessionInstanceMutex.WaitOne(0, false)) {
						already_running = true;
					}
				}
				catch (AbandonedMutexException) {
					/* The existing instance that owned the mutex has malfunctioned (process alive
					 * but Main UI thread has terminated unexpectedly). It's possible it may be
					 * processing brightness events normally, or not.
					 *
					 * For now we'll treat this as not running. Since we use a separate global mutex
					 * around the brightness event processing for the case of multiple sessions,
					 * this should be fine.
					 */
					Console.WriteLine(
						"WARNING: An existing instance of this program is already running in " +
						"this session but has malfunctioned.\n"
					);
				}

				if (already_running) {
					// Signal the existing instance to restore its console
					if (!opt_hide_on_start && !IsIconic(GetConsoleWindow())) {
						RestoreConsoleEvent.Set();
					}
					return EXIT_SUCCESS;
				}

				Program p = new Program();
				bool success = p.Start();

				if (!success)
					SoftPause();

				return success ? EXIT_SUCCESS : EXIT_FAILURE;
			}
		}

		bool Start()
		{
			bool success = false;

			// Delegate for the control handler to handle console break and close events
			ConsoleCtrlDelegate console_ctrl_delegate = null;

			// Event window to handle console-minimize events
			ConsoleEventWindow console_event_window = null;

			// Event watcher to handle WMI brightness events
			ManagementEventWatcher watcher = null;

			/* If it was requested to hide the console do that first, since doing anything else
			 * first would mean the console is more likely to be seen (ie flicker) even if it should
			 * be hidden.
			 */
			if (we_own_the_console) {
				if (opt_hide_on_start || (opt_hide_on_minimize && IsIconic(GetConsoleWindow()))) {
					MinimizeAndHideWindow(GetConsoleWindow());
				}
				Console.BufferHeight = 10000;
			}
			else {
				if (opt_hide_on_start || opt_hide_on_minimize) {
					Console.WriteLine(
						"WARNING: " + ProgName + " isn't running in its own console window, so " +
						"the options that were specified to hide the console are being ignored.\n"
					);
				}
			}

			string title = ProgName + ": Sync AC & DC brightness";
			Console.Title = title;
			Console.WriteLine(title);
			Console.WriteLine("");
			Console.WriteLine("Use option /? for usage information.");
			Console.WriteLine("");

			// Synchronization used by other threads to run code in this main UI thread
			UISyncContext = new WindowsFormsSynchronizationContext();
			SynchronizationContext.SetSynchronizationContext(UISyncContext);

			// Pop our console to the front when we're signaled by a secondary instance
			ThreadPool.RegisterWaitForSingleObject(RestoreConsoleEvent,
				delegate
				{
					IntPtr console = GetConsoleWindow();
					ShowWindow(console, SW_MINIMIZE);
					ShowWindow(console, SW_RESTORE);
				},
				null, -1, false);

			if (we_own_the_console) {
				Tray_Create();

				console_ctrl_delegate = new ConsoleCtrlDelegate(ConsoleCtrlHandlerRoutine);
				SetConsoleCtrlHandler(console_ctrl_delegate, true);

				if (opt_hide_on_minimize) {
					console_event_window = new ConsoleEventWindow(GetConsoleWindow());

					Console.Title += "   (minimize-to-tray enabled)";

					/* If the console window is already minimized then emulate the notification to
					 * the event window. It's important to send this so that the event window is
					 * hidden in a way that the window manager unhides it when the console window is
					 * restored.
					 *
					 * It's possible to have a race condition here, for example the console window
					 * is minimized during or after the event window is created but before this
					 * code, so the event window may receive the message twice. That is fine, the
					 * window manager tracks the event window status properly and it will still
					 * unhide the event window properly when the console window is restored.
					 */
					if (IsIconic(GetConsoleWindow())) {
						//Console.Beep(300, 250);
						SendMessage(console_event_window.Handle, WM_SHOWWINDOW, NULL, (IntPtr)SW_PARENTCLOSING);
					}
				}
			}

			// Global mutex to handle multiple sessions running this program at the same time
			if (SyncBrightnessMutex == null) {
				SecurityIdentifier sid = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
				MutexAccessRule rule = new MutexAccessRule(sid, MutexRights.FullControl, AccessControlType.Allow);
				MutexSecurity sec = new MutexSecurity();
				sec.AddAccessRule(rule);
				bool created_new = false;
				SyncBrightnessMutex = new Mutex(false, SyncBrightnessMutexName, out created_new, sec);
			}

			//todo:Is there a way to get brightness changed event using Win32 API without WMI?
			string scope = @"\\localhost\root\WMI";
			string query = "SELECT * FROM WmiMonitorBrightnessEvent";
			watcher = new ManagementEventWatcher(scope, query);
			watcher.EventArrived += new EventArrivedEventHandler(OnBrightnessChanged);

			Console.WriteLine("Monitoring brightness change...");

			SyncBrightness();

			// Start monitoring brightness events. The watcher calls SyncBrightness when necessary.
			try {
				watcher.Start();
			}
			// Check for access denied, for example a Guest account can't monitor brightness events.
			catch (UnauthorizedAccessException) {
				Console.Error.WriteLine("\nError: Can't monitor brightness events, access denied.");
				goto Cleanup;
			}
#if DEBUG
			// Force GC to help coax out any bugs that otherwise wouldn't be apparent.
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
#endif
			// Start the message loop to handle synchronization events and window messsages.
			Application.Run();
			// Application.Run() returns due to Application.Exit().
			// Currently that only happens at the user's request.
			success = true;

		Cleanup:
			/* Do a graceful cleanup.
			 * Note since this is a console application if the user closes the console window then
			 * this code is never reached. Refer to ConsoleCtrlHandlerRoutine.
			 */
			if (console_ctrl_delegate != null)
				SetConsoleCtrlHandler(console_ctrl_delegate, false);

			if(Tray != null)
				Tray.Dispose();

			if (watcher != null)
				watcher.Stop();

			GC.KeepAlive(console_event_window);
			return success ? true : false;
		}

		/* This is a signal handler for console signals.
		 * It is called by the system in a separate thread so we have to be careful here.
		 * "When the signal is received, the system creates a new thread in the process to execute
		 * the function."
		 * https://docs.microsoft.com/en-us/windows/console/handlerroutine
		 * Note .NET can handle CTRL_C and CTRL_BREAK events via Console.CancelKeyPress but it can't
		 * handle CTRL_CLOSE which is why I used this handler routine and the Win32 API instead.
		 */
		private static bool ConsoleCtrlHandlerRoutine(CtrlTypes ctrlType)
		{
			switch (ctrlType) {
				case CtrlTypes.CTRL_C_EVENT:
				case CtrlTypes.CTRL_BREAK_EVENT:
					/* We handle these for consistency. Application.Exit() causes Application.Run()
					 * in the main thread to return followed by a graceful cleanup. If we didn't
					 * handle these signals then ExitProcess may or may not be called, depending on
					 * whether or not any other handlers handle these signals.
					 */
					Application.Exit();
					return true;
				case CtrlTypes.CTRL_CLOSE_EVENT:
					/* For this event it appears ExitProcess is imminent even if the event is
					 * handled. Instead we just won't handle it and we'll assume an impending exit.
					 * The system will wait several seconds for this handler before terminating the
					 * process (observed in Windows 8.1). There's basically nothing guaranteed to
					 * finish at this point but we make an attempt at disposing of the tray icon so
					 * it doesn't linger.
					 */
					UISyncContext.Send(delegate { if (Tray != null) Tray.Dispose(); }, null);
					return false;
			}
			return false;
		}

		///<summary>
		/// Show a blank line and then a timestamp line.
		/// Timestamp example: ============ [2017-10-29 12:22:11 AM] ============
		///</summary>
		public static void ShowHeader()
		{
			string b = "============";
			string timestamp = string.Format("{0:yyyy-MM-dd hh:mm:ss tt}", DateTime.Now);
			Console.WriteLine("\n" + b + " [" + timestamp + "] " + b);
		}

		///<summary>
		/// This function is called whenever a WMI brightness event is received.
		/// It is called by a thread other than the UI thread.
		///</summary>
		void OnBrightnessChanged(object sender, EventArrivedEventArgs e)
		{
			if (opt_verbose) {
				ShowHeader();
				Console.WriteLine("Received WmiMonitorBrightnessEvent.");
				Console.WriteLine("Active :        " + e.NewEvent.Properties["Active"].Value.ToString());
				Console.WriteLine("Brightness :    " + e.NewEvent.Properties["Brightness"].Value.ToString());
				Console.WriteLine("InstanceName :  " + e.NewEvent.Properties["InstanceName"].Value.ToString());
			}

			// Get the current power scheme's AC & DC brightness levels
			var current = GetBrightness();
			if (current == null) {
				Console.WriteLine("Cannot read current brightness information. Doing nothing.");
				return;
			}

			/* If the current power scheme's AC & DC brightness levels are different from each
			 * other then sync them. Note that this does not take into account this event's
			 * brightness level, since that may be different for some other reason (power-saving).
			 */
			if (current.AC != current.DC) {
				SyncBrightness();
			}
			else {
				ShowCurrentBrightnessInfo(ref current);
				Console.WriteLine("Already in sync.");
			}
		}

		///<summary>
		/// Sync AC and DC brightness settings in the current power scheme.
		/// This function is a wrapper to acquire the sync mutex and call SyncBrightness_NoMutex().
		///</summary>
		void SyncBrightness()
		{
			try {
				if (!SyncBrightnessMutex.WaitOne(0, false)) {
					ShowHeader();
					Console.WriteLine("Another instance of this program is syncing the brightness.");
					SyncBrightnessMutex.WaitOne(Timeout.Infinite, false);
				}
			}
			catch (AbandonedMutexException) { }

			try {
				SyncBrightness_NoMutex();
			}
			finally {
				SyncBrightnessMutex.ReleaseMutex();
			}
		}

		///<summary>
		/// Sync AC and DC brightness settings in the current power scheme.
		/// The sync mutex is NOT acquired by this function.
		/// Call SyncBrightness() instead to prevent a race condition when multiple instances.
		///</summary>
		void SyncBrightness_NoMutex()
		{
			// Get the current power scheme's AC & DC brightness levels
			BrightnessInfo current = GetBrightness();
			if (current == null) {
				Console.WriteLine("Cannot read current brightness information. Doing nothing.");
				return;
			}

			ShowCurrentBrightnessInfo(ref current);

			if (current.AC == current.DC) {
				Console.WriteLine("Already in sync.");
				return;
			}

			// Sync
			if (current.isAC) {
				Console.WriteLine("Changing DC brightness to " + current.AC + ".");
				SetBrightness(PowerType.DC, current.AC);
			}
			else {
				Console.WriteLine("Changing AC brightness to " + current.DC + ".");
				SetBrightness(PowerType.AC, current.DC);
			}
		}

		void ShowCurrentBrightnessInfo(ref BrightnessInfo current)
		{
			ShowHeader();

			Console.WriteLine("Current power source: " + (current.isAC ? "AC" : "DC"));

			if (current.isAC || opt_verbose) {
				Console.WriteLine("AC brightness: " + current.AC);
			}

			if (!current.isAC || opt_verbose) {
				Console.WriteLine("DC brightness: " + current.DC);
			}
		}

		class BrightnessInfo
		{
			public bool isAC;  // Whether or not the computer is running on AC power
			public int AC;     // Current power scheme's AC brightness setting
			public int DC;     // Current power scheme's DC brightness setting
		}

		BrightnessInfo GetBrightness()
		{
			uint result;
			IntPtr pGuid = NULL;
			BrightnessInfo info = new BrightnessInfo();

			SYSTEM_POWER_STATUS stat;
			if (!GetSystemPowerStatus(out stat) || (stat._ACLineStatus != 0 && stat._ACLineStatus != 1)) {
				Console.WriteLine("Cannot determine the power mode. Doing nothing.");
				return null;
			}
			info.isAC = (stat._ACLineStatus == 1);

			result = PowerGetActiveScheme(NULL, ref pGuid);
			if (result != 0) {
				Console.WriteLine("Could not get the active power scheme.");
				return null;
			}
			Guid activeScheme = (Guid)Marshal.PtrToStructure(pGuid, typeof(Guid));

			IntPtr brightness = NULL;
			int type = 0;
			uint size = 4;

			result = PowerReadACValue(NULL, activeScheme, VideoSubgroup, BrightnessKey, ref type, ref brightness, ref size);
			if (result != 0) {
				Console.WriteLine("Could not get the brightness of AC.");
				return null;
			}
			info.AC = (int)brightness;

			result = PowerReadDCValue(NULL, activeScheme, VideoSubgroup, BrightnessKey, ref type, ref brightness, ref size);
			if (result != 0) {
				Console.WriteLine("Could not get the brightness of DC.");
				return null;
			}
			info.DC = (int)brightness;

			return info;
		}

		enum PowerType { AC, DC };
		void SetBrightness(PowerType power, int brightness)
		{
			uint result;

			if (brightness < 0 || brightness > 100) {
				throw new ArgumentException("Brightness should be in between 0 and 100.");
			}

			IntPtr pGuid = NULL;
			result = PowerGetActiveScheme(NULL, ref pGuid);
			if (result != 0) {
				Console.WriteLine("Could not get the active power scheme.");
				return;
			}
			Guid activeScheme = (Guid)Marshal.PtrToStructure(pGuid, typeof(Guid));

			if (power == PowerType.AC) {
				result = PowerWriteACValueIndex(NULL, activeScheme, VideoSubgroup, BrightnessKey, brightness);
			} else {
				result = PowerWriteDCValueIndex(NULL, activeScheme, VideoSubgroup, BrightnessKey, brightness);
			}

			if (result != 0) {
				Console.WriteLine("Could not set the brightness of the " +
					(power == PowerType.AC ? "AC" : "DC") + " mode.");
				return;
			}

			/* It is necessary to set the active scheme after changing the
			 * brightness, otherwise the system is not fully aware of the
			 * change and may use a value different from what was set.
			 */
			result = PowerSetActiveScheme(NULL, activeScheme);
			if (result != 0) {
				Console.WriteLine("Could not set the active power scheme.");
			}
		}

		private static NotifyIcon Tray = null;
		private static AboutBox1 About = null;

		private static void Tray_Create()
		{
			About = new AboutBox1();

			ContextMenuStrip menu = new ContextMenuStrip();
			ToolStripMenuItem m_about = new ToolStripMenuItem("About " + ProgName, null, new EventHandler(Tray_About));
			ToolStripMenuItem m_exit = new ToolStripMenuItem("Exit", null, new EventHandler(Tray_Exit));
			menu.Items.AddRange(new ToolStripMenuItem[] { m_about, m_exit });

			Tray = new NotifyIcon()
			{
				ContextMenuStrip = menu,
				Icon = LinkBrightness.Properties.Resources.TrayIcon,
				Visible = true,
				Text = ProgName + " (double-click to show/hide)",
			};

			Tray.DoubleClick += new EventHandler(Tray_DoubleClick);
		}

		private static void Tray_DoubleClick(object sender, EventArgs e)
		{
			IntPtr console = GetConsoleWindow();

			if (console != NULL) {
				if (IsWindowVisible(console)) {
					MinimizeAndHideWindow(console);
				}
				else {
					/* Restore the hidden minimized console window. Restore also unhides it.
					 * This will also unhide the owned windows that were hidden when it was
					 * minimized. In my experience restoring a minimized window makes the system
					 * more likely to "pop" the window to the foreground, bypassing the foreground
					 * window restrictions.
					 */
					ShowWindow(console, SW_RESTORE);
					SetForegroundWindow(console);
				}
			}
		}

		private static void Tray_About(object sender, EventArgs e)
		{
			if (About.Visible) {
				About.BringToFront();
			}
			else {
				IntPtr console = GetConsoleWindow();

				/* If the console window exists then make it the owner. If console is not minimized
				 * then About is shown default centered relative to console. In other cases center
				 * relative to screen.
				 */
				if (console != NULL) {
					NativeWindow nativeWindow = new NativeWindow();
					nativeWindow.AssignHandle(console);
					if (IsIconic(console))
						About.StartPosition = FormStartPosition.CenterScreen;
					About.ShowDialog(nativeWindow);
				}
				else {
					About.StartPosition = FormStartPosition.CenterScreen;
					About.ShowDialog();
				}
			}
		}

		private static void Tray_Exit(object sender, EventArgs e)
		{
			Application.Exit();
		}

		/* Minimize and hide a window.
		 * Minimizing first causes any owned windows to be hidden until the window is restored.
		 * For example minimizing the console would cause any owned window such as our event window
		 * to be hidden until the console is restored, and then system unhides the event window.
		 */
		private static void MinimizeAndHideWindow(IntPtr window)
		{
			IntPtr console = GetConsoleWindow();
			ShowWindow(console, SW_SHOWMINNOACTIVE);
			ShowWindow(console, SW_HIDE);
		}

		// This event window receives console-minimize events and hides the console.
		public class ConsoleEventWindow : NativeWindow
		{
			public IntPtr ConsoleHandle { get; private set; }
			public ConsoleEventWindow(IntPtr console_handle)
			{
				const int CS_HREDRAW = 0x0002;
				const int CS_NOCLOSE = 0x0200;
				const int CS_VREDRAW = 0x0001;
				const int WS_DISABLED = 0x08000000;
				const int WS_POPUP = unchecked((int)0x80000000);
				const int WS_EX_LAYERED = 0x00080000;
				const int WS_EX_NOACTIVATE = 0x08000000;
				const int WS_EX_NOPARENTNOTIFY = 0x00000004;
				const int WS_EX_TRANSPARENT = 0x00000020;

				if (console_handle == NULL) {
					throw new ArgumentNullException("Console window not found");
				}

				ConsoleHandle = console_handle;

				CreateParams cp = new CreateParams();

				cp.Caption = ProgName + ": Console Event Window";
				// cp.Parent is also set as the owner since this event window sets the WS_POPUP bit
				cp.Parent = ConsoleHandle;

				cp.ClassStyle = CS_HREDRAW | CS_NOCLOSE | CS_VREDRAW;
				cp.Style = WS_DISABLED | WS_POPUP;
				cp.ExStyle = WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_NOPARENTNOTIFY |
					WS_EX_TRANSPARENT;

				// Create the window
				CreateHandle(cp);

				// Disable DWM transitions
				int dparam = TRUE;
				DwmSetWindowAttribute(Handle, DWMWA_TRANSITIONS_FORCEDISABLED, ref dparam, sizeof(int));

				// Disable DWM non-client area rendering
				dparam = DWMNCRP_DISABLED;
				DwmSetWindowAttribute(Handle, DWMWA_NCRENDERING_POLICY, ref dparam, sizeof(int));

				// Disable IME input context
				ImmAssociateContext(Handle, NULL);

				/* Show the window without activating it.
				 *
				 * This adds WS_VISIBLE bit to the window, which is needed to receive
				 * SW_PARENTCLOSING events in WndProc. WS_VISIBLE cannot be used on creation because
				 * then the window would be activated, which would de-activate the console.
				 *
				 * Note that our event window is not actually visible to the eye because
				 * the window is sized 0,0 and also the WS_EX_LAYERED style is used but the layer
				 * is never initialized so the window is still transparent, which is what we want.
				 */
				ShowWindow(Handle, SW_SHOWNOACTIVATE);
				if (opt_verbose) {
					Console.WriteLine("Monitoring console-minimize events using event window 0x" +
						Handle.ToString("X") + " ...");
				}
			}

			protected override void WndProc(ref Message m)
			{
				/* "The WS_EX_NOACTIVATE value for dwExStyle prevents foreground activation by the
				 * system." However it's possible something such as a 3rd party window manager
				 * activates the window. Handle that by passing activation to the console window.
				 */

				if (m.Msg == WM_NCACTIVATE) {
					// ignore state change to active.
					// this message is received before the change-to-active via wm_activate.
					if (m.WParam == (IntPtr)TRUE) {
						SetFocus(ConsoleHandle);
						ShowWindowAsync(ConsoleHandle, IsIconic(ConsoleHandle) ? SW_RESTORE : SW_SHOW);
						m.Result = NULL;
						return;
					}
				}

				if (m.Msg == WM_ACTIVATE) {
					// ignore state change to active.
					// this message is received after the change-to-active via wm_ncactivate.
					if (LOWORD(m.WParam) == WA_ACTIVE || LOWORD(m.WParam) == WA_CLICKACTIVE) {
						SetFocus(ConsoleHandle);
						m.Result = NULL;
						return;
					}
				}

				if (m.Msg == WM_MOUSEACTIVATE) {
					// Do not activate the event window; Discard the mouse message.
					m.Result = (IntPtr)MA_NOACTIVATEANDEAT;
					return;
				}

				/* zero the minimize/maximize sizes.
				 * for posterity. this really shouldn't have any visible effect since the window is
				 * essentially transparent and can't be activated. this doesn't stop the minimize
				 * calculation fully (the system still calculates an invisible minimized title bar).
				 */
				if (m.Msg == WM_GETMINMAXINFO) {
					MINMAXINFO mmi = default(MINMAXINFO); // zeroed
					Marshal.StructureToPtr(mmi, m.LParam, true);
					m.Result = NULL;
					return;
				}

				/* Hide the console when it is minimized.
				 *
				 * SW_PARENTCLOSING: "The window's owner window is being minimized."
				 * Owner: Console Window
				 * Owned: The Event Window (This)
				 *
				 * - SW_PARENTCLOSING may be sent from the system or we may have emulated it.
				 *   The system will not send it unless the event window is visible (WS_VISIBLE).
				 *   We make sure to emulate it only when the console window is minimized.
				 *
				 * - The event window is hidden by the system (via the default window procedure
				 *   called in base.WndProc) when it receives SW_PARENTCLOSING, even if the message
				 *   was emulated. If the event window was hidden because of SW_PARENTCLOSING then
				 *   when the owner (console) is restored the system makes the event window visible.
				 *
				 * - The event window may receive this message multiple times without the owner
				 *   state (minimize->restore->minimize) changing in between. That is because we may
				 *   emulate SW_PARENTCLOSING as described above.
				 */
				if (m.Msg == WM_SHOWWINDOW && m.LParam == (IntPtr)SW_PARENTCLOSING) {
					//Console.Beep(500, 100);
					ShowWindowAsync(ConsoleHandle, SW_HIDE);
				}

				base.WndProc(ref m);
			}
		}

		#region Windows API
		#pragma warning disable 649   // default value warning due to missing assignments
		struct SYSTEM_POWER_STATUS
		{
			public Byte _ACLineStatus;
			public Byte _BatteryFlag;
			public Byte _BatteryLifePercent;
			public Byte _Reserved1;
			public Int32	_BatteryLifeTime;
			public Int32	_BatteryFullLifeTime;
		}

		[DllImport("kernel32.dll")]
		static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

		[DllImport("PowrProf.dll")]
		public static extern uint PowerGetActiveScheme(IntPtr UserRootPowerKey, ref IntPtr ActivePolicyGuid);

		[DllImport("PowrProf.dll")]
		public static extern uint PowerSetActiveScheme(IntPtr UserRootPowerKey,
			[MarshalAs(UnmanagedType.LPStruct)] Guid ActivePolicyGuid);

		[DllImport("PowrProf.dll", CharSet = CharSet.Unicode)]
		static extern UInt32 PowerWriteDCValueIndex(IntPtr RootPowerKey,
			[MarshalAs(UnmanagedType.LPStruct)] Guid SchemeGuid,
			[MarshalAs(UnmanagedType.LPStruct)] Guid SubGroupOfPowerSettingsGuid,
			[MarshalAs(UnmanagedType.LPStruct)] Guid PowerSettingGuid,
			int AcValueIndex);

		[DllImport("PowrProf.dll", CharSet = CharSet.Unicode)]
		static extern UInt32 PowerWriteACValueIndex(IntPtr RootPowerKey,
			[MarshalAs(UnmanagedType.LPStruct)] Guid SchemeGuid,
			[MarshalAs(UnmanagedType.LPStruct)] Guid SubGroupOfPowerSettingsGuid,
			[MarshalAs(UnmanagedType.LPStruct)] Guid PowerSettingGuid,
			int AcValueIndex);

		[DllImport("PowrProf.dll", CharSet = CharSet.Unicode)]
		static extern uint PowerReadACValue(
			IntPtr RootPowerKey,
			[MarshalAs(UnmanagedType.LPStruct)] Guid SchemeGuid,
			[MarshalAs(UnmanagedType.LPStruct)] Guid SubGroupOfPowerSettingsGuid,
			[MarshalAs(UnmanagedType.LPStruct)] Guid PowerSettingGuid,
			ref int Type,
			ref IntPtr Buffer,
			ref uint BufferSize
		);
		[DllImport("PowrProf.dll", CharSet = CharSet.Unicode)]
		static extern uint PowerReadDCValue(
			IntPtr RootPowerKey,
			[MarshalAs(UnmanagedType.LPStruct)] Guid SchemeGuid,
			[MarshalAs(UnmanagedType.LPStruct)] Guid SubGroupOfPowerSettingsGuid,
			[MarshalAs(UnmanagedType.LPStruct)] Guid PowerSettingGuid,
			ref int Type,
			ref IntPtr Buffer,
			ref uint BufferSize
		);

		const int TRUE = 1;
		const int FALSE = 0;

		[DllImport("kernel32.dll")]
		static extern uint GetCurrentThreadId();

		[DllImport("user32.dll")]
		static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

		[DllImport("user32.dll")]
		static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

		const int SW_HIDE = 0;
		const int SW_MINIMIZE = 6;
		const int SW_RESTORE = 9;
		const int SW_SHOW = 5;
		const int SW_SHOWMINNOACTIVE = 7;
		const int SW_SHOWNA = 8;
		const int SW_SHOWNOACTIVATE = 4;

		[DllImport("kernel32.dll")]
		static extern uint GetConsoleProcessList(uint[] ProcessList, uint ProcessCount);

		[DllImport("kernel32.dll")]
		static extern IntPtr GetConsoleWindow();

		[DllImport("user32.dll")]
		static extern bool IsIconic(IntPtr hWnd);

		[DllImport("user32.dll")]
		static extern bool IsWindowVisible(IntPtr hWnd);

		[DllImport("kernel32.dll", SetLastError = true)]
		static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate HandlerRoutine, bool Add);

		// Delegate type to be used as the Handler Routine for SCCH
		delegate Boolean ConsoleCtrlDelegate(CtrlTypes CtrlType);

		// Enumerated type for the control messages sent to the handler routine
		enum CtrlTypes : uint
		{
			CTRL_C_EVENT = 0,
			CTRL_BREAK_EVENT,
			CTRL_CLOSE_EVENT,
			CTRL_LOGOFF_EVENT = 5,
			CTRL_SHUTDOWN_EVENT
		}

		[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
		static extern int MessageBoxW(int hWnd, String text, String caption, uint type);

		[DllImport("user32.dll")]
		static extern bool SetForegroundWindow(IntPtr hWnd);

		[DllImport("user32.dll", CharSet = CharSet.Auto)]
		static extern IntPtr SendMessage(IntPtr hWnd, UInt32 Msg, IntPtr wParam, IntPtr lParam);

		const int WM_ACTIVATE = 0x0006;
		const int WA_ACTIVE = 1;
		const int WA_CLICKACTIVE = 2;
		const int WA_INACTIVE = 0;

		const int WM_MOUSEACTIVATE = 0x0021;
		const int MA_ACTIVATE = 1;
		const int MA_ACTIVATEANDEAT = 2;
		const int MA_NOACTIVATE = 3;
		const int MA_NOACTIVATEANDEAT = 4;

		const int WM_NCACTIVATE = 0x0086;
		public struct NCCALCSIZE_PARAMS
		{
			[MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
			public RECT[] rgrc;
			public IntPtr lppos; // pointer to WINDOWPOS
		}

		public struct RECT
		{
			public int Left;
			public int Top;
			public int Right;
			public int Bottom;
		}

		const int WM_GETMINMAXINFO = 0x0024;

		public struct POINTAPI
		{
			public int x;
			public int y;
		}

		public struct MINMAXINFO
		{
			public POINTAPI ptReserved;
			public POINTAPI ptMaxSize;
			public POINTAPI ptMaxPosition;
			public POINTAPI ptMinTrackSize;
			public POINTAPI ptMaxTrackSize;
		}

		const int WM_SHOWWINDOW = 0x0018;
		const int SW_OTHERUNZOOM = 4;
		const int SW_OTHERZOOM = 2;
		const int SW_PARENTCLOSING = 1;
		const int SW_PARENTOPENING = 3;

		const int WM_WINDOWPOSCHANGING = 0x0046;

		public struct WINDOWPOS
		{
			public IntPtr hwnd;
			public IntPtr hwndInsertAfter;
			public int x;
			public int y;
			public int cx;
			public int cy;
			public uint flags;
		}

		public static Int32 HIWORD(IntPtr ptr)
		{
			Int32 val32 = ptr.ToInt32();
			return ((val32 >> 16) & 0xFFFF);
		}

		public static Int32 LOWORD(IntPtr ptr)
		{
			Int32 val32 = ptr.ToInt32();
			return (val32 & 0xFFFF);
		}

		[DllImport("user32.dll", SetLastError = true)]
		public static extern IntPtr SetActiveWindow(IntPtr hWnd);

		[DllImport("user32.dll", SetLastError = true)]
		public static extern IntPtr SetFocus(IntPtr hWnd);

		[DllImport("dwmapi.dll", PreserveSig = true)]
		public static extern int DwmSetWindowAttribute(IntPtr hwnd, uint attr, ref int attrValue, int attrSize);

		const uint DWMWA_NCRENDERING_POLICY = 2;
		const int DWMNCRP_DISABLED = 1;
		const uint DWMWA_TRANSITIONS_FORCEDISABLED = 3;

		[DllImport("imm32.dll", CharSet = CharSet.Auto)]
		public static extern IntPtr ImmAssociateContext(IntPtr hWnd, IntPtr hIMC);

		#pragma warning restore 649   // default value warning due to missing assignments
		#endregion
	}
}
