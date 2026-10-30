using System;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Net;
using System.Web.Script.Serialization;

namespace ApexCam
{
    public static class AdbTunnel
    {
        public static string GetAdbPath()
        {
            try { return BundledSetup.GetAdbPath(); } catch { /* Preserve an existing SDK installation if extraction is unavailable. */ }
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string localAdb = Path.Combine(baseDir, "adb.exe");
            if (File.Exists(localAdb)) return localAdb;

            string toolsAdb = Path.Combine(baseDir, @"tools\adb.exe");
            if (File.Exists(toolsAdb)) return toolsAdb;

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string userSdk = Path.Combine(localAppData, @"Android\Sdk\platform-tools\adb.exe");
            if (File.Exists(userSdk)) return userSdk;

            return "adb.exe";
        }

        public static int CameraCatalogPort { get; private set; }
        private static readonly object tunnelGate=new object();
        private static readonly Dictionary<string,int> catalogPorts=new Dictionary<string,int>();
        public static int FindCameraCatalogPort(string deviceId)
        {
            int[] ports;
            lock(tunnelGate){ports=new int[catalogPorts.Count];catalogPorts.Values.CopyTo(ports,0);}
            foreach(int port in ports) {
                try {
                    var request=(HttpWebRequest)WebRequest.Create("http://127.0.0.1:"+port+"/cameras");
                    request.Proxy=null;request.Timeout=700;request.ReadWriteTimeout=700;
                    using(var response=request.GetResponse())
                    using(var reader=new StreamReader(response.GetResponseStream())) {
                        var data=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(reader.ReadToEnd());
                        object id;
                        if(data.TryGetValue("deviceId",out id) && Convert.ToString(id)==deviceId)return port;
                    }
                } catch {}
            }
            return deviceId!=null && deviceId.StartsWith("legacy:") && ports.Length==1 ? ports[0] : 0;
        }

        public static readonly int[] RequiredReversePorts = new int[] { 8556, 44100, 44101 };

        public static bool SetupPortForwarding(int port = 8556)
        {
            return SetupAllReverseTunnels();
        }

        public static bool SetupAllReverseTunnels()
        {
            try
            {
                string adb = GetAdbPath();
                string devices = CheckConnectedDevices();
                if (string.IsNullOrEmpty(devices) || !devices.Contains("\tdevice"))
                {
                    // Niciun dispozitiv conectat prin USB, evitam apelul inutil reverse
                    return false;
                }

                bool configured=false;
                foreach(string line in devices.Split(new [] {'\r','\n'},StringSplitOptions.RemoveEmptyEntries)) {
                    var parts=line.Split(new [] {'\t',' '},StringSplitOptions.RemoveEmptyEntries);
                    if(parts.Length<2 || parts[1]!="device" ||
                        !System.Text.RegularExpressions.Regex.IsMatch(parts[0],@"^[A-Za-z0-9_.:-]+$"))continue;
                    string serial=parts[0];
                    foreach(int p in RequiredReversePorts)
                        RunCommand(adb,"-s "+serial+" reverse tcp:"+p+" tcp:"+p);
                    lock(tunnelGate) {
                        if(!catalogPorts.ContainsKey(serial)) {
                            int allocated;
                            if(int.TryParse(RunCommand(adb,"-s "+serial+" forward tcp:0 tcp:8080").Trim(),out allocated))
                                catalogPorts[serial]=allocated;
                        }
                        foreach(int value in catalogPorts.Values){CameraCatalogPort=value;break;}
                    }
                    configured=true;
                }
                return configured;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[ADB] Error port reverse tunnels: " + ex.Message);
                return false;
            }
        }

        public static void RemovePortForwarding(int port = 8556)
        {
            try
            {
                string adb = GetAdbPath();
                lock(tunnelGate) {
                    foreach(var pair in catalogPorts) {
                        foreach(int p in RequiredReversePorts)
                            RunCommand(adb,"-s "+pair.Key+" reverse --remove tcp:"+p);
                        RunCommand(adb,"-s "+pair.Key+" forward --remove tcp:"+pair.Value);
                    }
                    catalogPorts.Clear();CameraCatalogPort=0;
                }
            }
            catch { }
        }

        public static string CheckConnectedDevices()
        {
            try
            {
                string adb = GetAdbPath();
                return RunCommand(adb, "devices -l");
            }
            catch
            {
                return "";
            }
        }

        private static string RunCommand(string exe, string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (Process p = Process.Start(psi))
                {
                    if (p != null && p.WaitForExit(2000))
                    {
                        return p.StandardOutput.ReadToEnd();
                    }
                    return "";
                }
            }
            catch
            {
                return "";
            }
        }
    }
}
