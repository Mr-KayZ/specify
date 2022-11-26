﻿// everything that handles actual data collection to be passed over to the monolith
using System;
using System.Collections;
using System.Management;
using Microsoft.Win32.TaskScheduler;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text;

namespace specify_client
{
    public class Data
    {
        /**
         * <summary>
         * Gets the WMI object (with GetWmiObj), and converts it to a dictionary.
         * </summary>
         * <seealso cref="GetWmiObj"/>
         */
        public static List<Dictionary<string, object>> GetWmi(string cls, string selected = "*", string ns = @"root\cimv2")
        {
            var collection = GetWmiObj(cls, selected, ns);
            var res = new List<Dictionary<string, object>>();

            foreach (var i in collection)
            {
                var tempD = new Dictionary<string, object>();
                foreach (var j in i.Properties)
                {
                    tempD[j.Name] = j.Value;
                }
                
                res.Add(tempD);
            }

            return res;
        }
        
        /**
         * <summary>
         * Gets the WMI Object for the specified query. Try to use GetWmi when possible.
         * </summary>
         * <remarks>
         * Microsoft recommends using the CIM libraries (Microsoft.Management.Infrastructure).
         * However, some classes can't be called in CIM and only in WMI (e.g. Win32_PhysicalMemory).
         * </remarks>
         * <seealso cref="GetWmi"/>
         */
        public static ManagementObjectCollection GetWmiObj(string cls, string selected, string ns)
        {
            var scope = new ManagementScope(ns);
            scope.Connect();

            var query = new ObjectQuery($"SELECT {selected} FROM {cls}");
            var collection = new ManagementObjectSearcher(scope, query).Get();
            return collection;
        }

        /**
         * <summary>
         * Gets tasks from Task Scheduler that satisfy all of the following conditions:
         * <list type="bullet">
         *  <item>Author isn't Microsoft</item>
         *  <item>State is Ready or Running</item>
         *  <item>Triggered at boot or login</item>
         * </list>
         * </summary>
         */
        public static List<Task> GetTsStartupTasks()
        {
            var ts = new TaskService();
            return EnumTsTasks(ts.RootFolder);
        }

        private static List<Task> EnumTsTasks(TaskFolder fld)
        {
            var res = new List<Task>();
            foreach (var task in fld.Tasks)
            {
                if (
                    !string.IsNullOrEmpty(task.Definition.RegistrationInfo.Author) &&
                    task.Definition.RegistrationInfo.Author.StartsWith("Microsof")
                    ) continue;

                if (task.State != TaskState.Ready || task.State != TaskState.Running) continue;
                
                var triggers = task.Definition.Triggers;
                var triggersFlag = true;
                foreach (var trigger in triggers)
                {
                    if (trigger.TriggerType == TaskTriggerType.Logon || trigger.TriggerType == TaskTriggerType.Boot)
                        triggersFlag = false;
                }

                if (triggersFlag) continue;

                res.Add(task);
            }
            
            foreach (var sfld in fld.SubFolders)
                res.AddRange(EnumTsTasks(sfld));

            return res;
        }

        /**
         * Convert a CIM date (what would be gotten from WMI) into an ISO date
         * See https://learn.microsoft.com/en-us/windows/win32/wmisdk/cim-datetime
         */
        public static string CimToIsoDate(string cim)
        {
            return DateTimeToIsoDate(ManagementDateTimeConverter.ToDateTime(cim));
        }

        public static string DateTimeToIsoDate(DateTime d)
        {
            return d.ToString("yyyy-MM-ddTHH:mm:sszzz");
        }
        
        public static T GetRegistryValue<T>(RegistryKey regKey, string path, string name, T def = default)
        {
            var key = regKey.OpenSubKey(path);
            if (key == null) return def;
            var value = key.GetValue(name);
            return (T)value;
        }
    }

    /**
     * Cache of values so they don't have to be called multiple times
     */
    public static class DataCache
    {
        public static List<string> Issues { get; set; }
        public static Dictionary<string, object> Os { get; private set; }
        public static Dictionary<string, object> Cs { get; private set; }
        public static IDictionary SystemVariables { get; private set; }
        public static IDictionary UserVariables { get; private set; }
        public static List<OutputProcess> RunningProcesses { get; private set; }
        public static List<Dictionary<string, object>> Services { get; private set; }
        public static List<Dictionary<string, object>> InstalledApps { get; private set; }
        public static List<Dictionary<string, object>> InstalledHotfixes { get; private set; }
        public static List<string> AvList { get; private set; }
        public static List<string> FwList { get; private set; }
        public static string HostsFile { get; private set;  }
        public static bool? UacEnabled { get; private set; }
        public static int? UacLevel { get; private set; }
        public static List<Dictionary<string, object>> NetAdapters { get; private set; }
        public static List<Dictionary<string, object>> IPRoutes { get; private set; }

        public static string Username => Environment.UserName;
        // all the hardware stuff
        //each item in the list is a stick of ram
        public static List<RamStick> Ram {get; private set;}
        public static Dictionary<string, object> Cpu {get; private set;}
        public static List<Dictionary<string, object>> Gpu {get; private set;}
        public static Dictionary<string, object> Motherboard {get; private set;}
        public static Dictionary<string, object> Tpm { get; private set; }
        public static List<Dictionary<string, object>> Drivers { get; private set; }
        public static List<Dictionary<string, object>> Devices { get; private set; }
        public static bool? SecureBootEnabled { get; private set; }
        private static readonly List<string> SystemProcesses = new List<string>()
        {
            "Memory Compression",
            "Registry",
            "System",
            "Idle"
        };

        // DllImports to P/Invoke process information queries. The typical C# process call fails on certain system processes.
        [Flags]
        private enum ProcessAccessFlags : uint
        {
            QueryLimitedInformation = 0x00001000
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool QueryFullProcessImageName(
              [In] IntPtr hProcess,
              [In] int dwFlags,
              [Out] StringBuilder lpExeName,
              ref int lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(
         ProcessAccessFlags processAccess,
         bool bInheritHandle,
         int processId);

        public static void MakeMainData()
        {
            Os = Data.GetWmi("Win32_OperatingSystem").First();
            Cs = Data.GetWmi("Win32_ComputerSystem").First();
        }

        public static void DummyTimer()
        {
            Thread.Sleep(5000);
        }

        public static void MakeSystemData()
        {
            SystemVariables = Environment.GetEnvironmentVariables(EnvironmentVariableTarget.Machine);
            UserVariables = Environment.GetEnvironmentVariables(EnvironmentVariableTarget.User);
            Services = Data.GetWmi("Win32_Service", "Name, Caption, PathName, StartMode, State");
            InstalledApps = Data.GetWmi("Win32_Product", "Name, Version");
            InstalledHotfixes = Data.GetWmi("Win32_QuickFixEngineering", "Description,HotFixID,InstalledOn");

            RunningProcesses = new List<OutputProcess>();
            var rawProcesses = Process.GetProcesses();

            foreach (var rawProcess in rawProcesses)
            {
                double cpuPercent = -1.0; // TODO: make this actually work properly
                var exePath = "";
                /*try
                {
                    var counter = new PerformanceCounter("Process", "% Processor Time", rawProcess.ProcessName);
                    counter.NextValue();
                    Thread.Sleep(100);
                    cpuPercent = counter.NextValue();
                }
                catch (Win32Exception e)
                {
                    cpuPercent = -1;
                }*/

                try
                {
                    // capacity must be declared so it can be referenced.
                    int capacity = 2000;

                    StringBuilder sb = new StringBuilder(capacity);
                    IntPtr ptr = OpenProcess(ProcessAccessFlags.QueryLimitedInformation, false, rawProcess.Id);

                    if (!QueryFullProcessImageName(ptr, 0, sb, ref capacity))
                    {
                        if (!SystemProcesses.Contains(rawProcess.ProcessName))
                        {
                            exePath = "null - Not found";
                            Issues.Add($"System Data: Could not get the EXE path of {rawProcess.ProcessName} ({rawProcess.Id})");
                        }
                        else
                        {
                            exePath = "SYSTEM";
                        }
                    }
                    else
                    {
                        exePath = sb.ToString();
                    }
                }
                catch (Win32Exception e)
                {
                    exePath = "null - Win32Exception";
                    Issues.Add($"System Data: Could not get the EXE path of {rawProcess.ProcessName} ({rawProcess.Id})");
                    Console.WriteLine(e.GetBaseException());
                }

                RunningProcesses.Add(new OutputProcess
                {
                    ProcessName = rawProcess.ProcessName,
                    ExePath = exePath,
                    Id = rawProcess.Id,
                    WorkingSet = rawProcess.WorkingSet64,
                    CpuPercent = cpuPercent
                });
            }
        }

        public static void MakeHardwareData()
        {
            //Ram = Data.GetWmi("Win32_PhysicalMemory",
                //"DeviceLocator, Capacity, ConfiguredClockSpeed, Manufacturer, PartNumber, SerialNumber");
            Cpu = Data.GetWmi("Win32_Processor",
                "CurrentClockSpeed, Manufacturer, Name, SocketDesignation").First();
            Gpu = Data.GetWmi("Win32_VideoController",
                "Description, AdapterRam, CurrentHorizontalResolution, CurrentVerticalResolution, "
                + "CurrentRefreshRate, CurrentBitsPerPixel");
            Motherboard = Data.GetWmi("Win32_BaseBoard", "Manufacturer, Product, SerialNumber").First();
            Drivers = Data.GetWmi("Win32_PnpSignedDriver", "FriendlyName,Manufacturer,DeviceID,DeviceName,DriverVersion");
            Devices = Data.GetWmi("Win32_PnpEntity", "DeviceID,Name,Description,Status");
            Ram = GetSMBiosMemoryInfo();
        }

        public static void MakeSecurityData()
        {
            AvList = Data.GetWmi("AntivirusProduct", "displayName", @"root\SecurityCenter2")
                .Select(x => (string)x["displayName"]).ToList();
            FwList = Data.GetWmi("FirewallProduct", "displayName", @"root\SecurityCenter2")
                .Select(x => (string)x["displayName"]).ToList();
            
            var enableLua = Data.GetRegistryValue<int?>(Registry.LocalMachine,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA");
            if (enableLua == null) Issues.Add($"Security data: could not get EnableLUA value");
            else UacEnabled = enableLua == 1;

            if (Environment.GetEnvironmentVariable("firmware_type").Equals("UEFI"))
            {
                var secBootEnabled = Data.GetRegistryValue<int?>(
                    Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\SecureBoot\State",
                    "UEFISecureBootEnabled");

                if (secBootEnabled == null) Issues.Add($"Security data: could not get UEFISecureBootEnabled value");
                else SecureBootEnabled = secBootEnabled == 1;
            }
            
            try 
            { 
                Tpm = Data.GetWmi("Win32_Tpm","*", @"Root\CIMV2\Security\MicrosoftTpm").First();
                Tpm["IsPresent"] = true;
            }
            catch (InvalidOperationException)
            {
                // No TPM
                Tpm = new Dictionary<string, object>(){{ "IsPresent", false }};
            }
            catch (ManagementException)
            {
                Tpm = null;
                Issues.Add("Security Data: could not get TPM. This is probably because specify was not run as administrator.");
            }

            UacLevel = Data.GetRegistryValue<int?>(
                Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System",
                "ConsentPromptBehaviorAdmin");
        }

        public static void MakeNetworkData()
        {
            NetAdapters = Data.GetWmi("Win32_NetworkAdapterConfiguration", 
                "Description, DHCPEnabled, DHCPServer, DNSDomain, DNSDomainSuffixSearchOrder, DNSHostName, "
                    + "DNSServerSearchOrder, IPEnabled, IPAddress, IPSubnet, DHCPLeaseObtained, DHCPLeaseExpires, "
                    + "DefaultIPGateway, MACAddress, InterfaceIndex");
            IPRoutes = Data.GetWmi("Win32_IP4RouteTable", 
                "Description, Destination, Mask, NextHop, Metric1, InterfaceIndex");
            HostsFile = System.IO.File.ReadAllText(@"C:\Windows\system32\drivers\etc\hosts");
        }
        
        private static List<RamStick> GetSMBiosMemoryInfo()
        {
            // Made a new GetWmi function because I'm not knowledgeable enough in how this works to translate the Dictionary object into what I need for this.
            var SMBiosObj = Data.GetWmiObj("MSSMBios_RawSMBiosTables", "*", "root\\WMI");

            // If no data is received, stop before it excepts. Add error message?
            if (SMBiosObj == null)
            {
                Issues.Add("Hardware Data: Could not get SMBios info for RAM.");
                return null;
            }

            // Store raw SMBios Data
            byte[] SMBios = null;
            foreach (ManagementObject obj in SMBiosObj)
            {
                SMBios = (byte[])obj["SMBiosData"];
            }

            var offset = 0;
            var type = SMBios[offset];

            var SMBiosMemoryInfo = new List<RamStick>();

            while (offset + 4 < SMBios.Length && type != 127)
            {
                type = SMBios[offset];
                var dataLength = SMBios[offset + 1];

                // If the data extends the bounds of the SMBios Data array, stop.
                if (offset + dataLength > SMBios.Length)
                {
                    break;
                }

                var data = new byte[dataLength];
                Array.Copy(SMBios, offset, data, 0, dataLength);
                offset += dataLength;

                List<string> smbStringsList = new List<string>();

                if (offset < SMBios.Length && SMBios[offset] == 0)
                    offset++;

                // Iterate the byte array to build a list of SMBios structures.
                while (offset < SMBios.Length && SMBios[offset] != 0)
                {
                    System.Text.StringBuilder smbDataString = new System.Text.StringBuilder();
                    while (offset < SMBios.Length && SMBios[offset] != 0)
                    {
                        smbDataString.Append((char)SMBios[offset]);
                        offset++;
                    }
                    offset++;
                    smbStringsList.Add(smbDataString.ToString());
                    // Console.WriteLine(smbDataString);
                }
                offset++;

                // This is the only type we care about; Type 17. If the type is anything else, it simply loops again.
                if (type != 0x11) continue;
                
                var stick = new RamStick();
                // These if statements confirm the data received is valid data.
                // We don't need else statements here because the default is null
                if (0x10 < data.Length && data[0x10] > 0 && data[0x10] <= smbStringsList.Count)
                {
                    stick.DeviceLocation = smbStringsList[data[0x10] - 1].Trim();
                }

                if (0x11 < data.Length && data[0x11] > 0 && data[0x11] <= smbStringsList.Count)
                {
                    stick.BankLocator = smbStringsList[data[0x11] - 1].Trim();
                }

                if (0x17 < data.Length && data[0x17] > 0 && data[0x17] <= smbStringsList.Count)
                {
                    stick.Manufacturer = smbStringsList[data[0x17] - 1].Trim();
                }

                if (0x18 < data.Length && data[0x18] > 0 && data[0x18] <= smbStringsList.Count)
                {
                    stick.SerialNumber = smbStringsList[data[0x18] - 1].Trim();
                }

                if (0x1A < data.Length && data[0x1A] > 0 && data[0x1A] <= smbStringsList.Count)
                {
                    stick.PartNumber = smbStringsList[data[0x1A] - 1].Trim();
                }

                if (0x15 + 1 < data.Length)
                {
                    stick.ConfiguredSpeed = (data[0x15 + 1] << 8) | data[0x15];
                }

                if (0xC + 1 < data.Length)
                {
                    stick.Capacity = (data[0xC + 1] << 8) | data[0xC];
                }
                SMBiosMemoryInfo.Add(stick);
            }
            return SMBiosMemoryInfo;
        }
    }

    public class OutputProcess
    {
        public string ProcessName;
        public string ExePath;
        public int Id;
        public long WorkingSet;
        public double CpuPercent;
    }

    public class RamStick
    {
        public string DeviceLocation;
        public string BankLocator;
        public string Manufacturer;
        public string SerialNumber;
        public string PartNumber;
        /** MHz */
        public int? ConfiguredSpeed;

        /** MiB */
        public int? Capacity;
    }
}
