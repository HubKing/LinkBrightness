/*
	Written by Sin Jeong-hun.
	MIT Licence.
 */
using System;
using System.Management;
using System.Runtime.InteropServices;

namespace LinkBrightness
{
	class Program
	{
		readonly Guid VideoSubgroup = new Guid("7516b95f-f776-4464-8c53-06167f40cc99");
		readonly Guid BrightnessKey = new Guid("aded5e82-b909-4619-9949-f5d71dac0bcb");
		readonly IntPtr NULL = IntPtr.Zero;
   
		public static void Main(string[] args)
		{
			Program p = new Program();
			p.Start();
		}
		
		void Start()
		{
			//todo:Is there a way to get brightness changed event using Win32 API without WMI?
			string scope = @"\\localhost\root\WMI";
			string query = "SELECT * FROM WmiMonitorBrightnessEvent";
			ManagementEventWatcher watcher = new ManagementEventWatcher(scope, query);
			watcher.EventArrived += new EventArrivedEventHandler(OnBrightnessChanged);
			Console.WriteLine("Monitoring brightness change...");
			watcher.Start();
			Console.ReadKey();
			watcher.Stop();
			Console.WriteLine("Good bye.");
		}
		
		
		void OnBrightnessChanged(object sender, EventArrivedEventArgs e)
		{
			bool isAC = false;
			SYSTEM_POWER_STATUS stat;
			GetSystemPowerStatus(out stat);
			if (stat._ACLineStatus == 0)
				isAC = false;
			else if (stat._ACLineStatus == 1)
				isAC = true;
			else {
				Console.WriteLine("Cannot determine the power mode. Doing nothing.");
				return;
			}
			
			var current = GetBrightness();
			if (current == null) {
				Console.WriteLine("Cannot read current brightness information. Doing nothing.");
				return;			
			}
			
			if (isAC) {
				if (current.AC != current.DC) {
					Console.WriteLine("==================================");
					Console.WriteLine("Current power source: AC");
					Console.WriteLine("AC brightness: " + current.AC);
					Console.WriteLine("Changing DC brightness to " + current.AC + ".");
					SetBrightness(false, current.AC);
				}
			} else {
				if (current.AC != current.DC) {
					Console.WriteLine("==================================");
					Console.WriteLine("Current power source: DC");
					Console.WriteLine("DC brightness: " + current.DC);
					Console.WriteLine("Changing AC brightenss to " + current.DC + ".");
					SetBrightness(true, current.DC);
				}				
			}
		}
		
		class BrightnessInfo
		{
			public int AC;
			public int DC;
		}
		
		BrightnessInfo GetBrightness()
		{
			IntPtr pGuid = NULL;
			PowerGetActiveScheme(NULL, ref pGuid);
			Guid activeScheme = (Guid)Marshal.PtrToStructure(pGuid, typeof(Guid));

			uint result;
			IntPtr brightness = NULL;
			int type = 0;
			uint size = 4;
			BrightnessInfo info = new BrightnessInfo();

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
		
		void SetBrightness(bool setAC, int brightness)
		{
			if (brightness < 0 || brightness > 100) {
				throw new ArgumentException("Brightness should be in between 0 and 100.");
			}
			
			IntPtr pGuid = NULL;
			PowerGetActiveScheme(NULL, ref pGuid);
			Guid activeScheme = (Guid)Marshal.PtrToStructure(pGuid, typeof(Guid));

			uint result;
			if (setAC) {
				result = PowerWriteACValueIndex(NULL, activeScheme, VideoSubgroup, BrightnessKey, brightness);
			} else {
				result = PowerWriteDCValueIndex(NULL, activeScheme, VideoSubgroup, BrightnessKey, brightness);
			}
			
			if (result != 0) {
				Console.WriteLine("Could not set the brightness of the " + (setAC ? "AC mode." : "DC mode."));
			}		
		}
		
		#region Windows API
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
		#endregion
	}
}
