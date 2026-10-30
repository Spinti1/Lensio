using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ApexCam {
 public static class BundledSetup {
  private const string Filter="{A3FCE0F5-3493-419F-958A-ABA1250EC20B}";
  private const string Category=@"SOFTWARE\Classes\CLSID\{860BB310-5D01-11D0-BD3B-00A0C911CE86}\Instance\";
  private const string Version="32.1.0";
  private static readonly object usbGate=new object();
  private static string adbPath;
  public static string DriverDirectory {get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Lensio","VirtualCamera",Version);}}
  private static Stream Resource(string name){var s=typeof(BundledSetup).Assembly.GetManifestResourceStream("LensioSetup."+name);if(s==null)throw new InvalidDataException("Missing setup component: "+name);return s;}
  public static byte[] ReadVerifiedPayload(string name){
   Dictionary<string,object> manifest;
   using(var s=Resource("manifest.json"))using(var r=new StreamReader(s))manifest=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(r.ReadToEnd());
   Dictionary<string,object> match=null;
   foreach(var raw in (System.Collections.IEnumerable)manifest["files"]){var item=(Dictionary<string,object>)raw;if(Convert.ToString(item["name"])==name){match=item;break;}}
   if(match==null || Path.GetFileName(name)!=name)throw new InvalidDataException("Unknown setup component");
   int size=Convert.ToInt32(match["size"]);if(size<1||size>16*1024*1024)throw new InvalidDataException("Invalid component size");
   byte[] data=new byte[size];
   using(var packed=Resource(name+".gz"))using(var gzip=new GZipStream(packed,CompressionMode.Decompress)){
    int at=0;while(at<size){int n=gzip.Read(data,at,size-at);if(n<=0)throw new InvalidDataException("Incomplete setup component");at+=n;}
    if(gzip.ReadByte()!=-1)throw new InvalidDataException("Oversized setup component");
   }
   using(var sha=System.Security.Cryptography.SHA256.Create()){
    string hash=BitConverter.ToString(sha.ComputeHash(data)).Replace("-","");
    if(!string.Equals(hash,Convert.ToString(match["sha256"]),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Setup integrity check failed");
   }
   return data;
  }
  private static bool SameFile(string path,byte[] bytes){
   if(!File.Exists(path))return false;
   using(var s=File.OpenRead(path))using(var sha=System.Security.Cryptography.SHA256.Create())return s.Length==bytes.Length && Convert.ToBase64String(sha.ComputeHash(s))==Convert.ToBase64String(sha.ComputeHash(bytes));
  }
  public static void RejectReparsePath(string path){
   for(var d=new DirectoryInfo(Path.GetFullPath(path));d!=null;d=d.Parent)if(d.Exists&&(d.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Setup cannot use redirected directories.");
   if(File.Exists(path)&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("Setup cannot use redirected files.");
  }
  private static void WriteComponent(string directory,string name,bool protectedInstall){
   RejectReparsePath(directory);string target=Path.Combine(directory,name);RejectReparsePath(target);
   byte[] bytes=ReadVerifiedPayload(name);
   if(protectedInstall&&File.Exists(target))ValidateProtectedSecurity(File.GetAccessControl(target));
   if(SameFile(target,bytes))return;
   if(protectedInstall&&File.Exists(target))throw new IOException("An unexpected camera component already exists. Installation stopped.");
   using(var output=new FileStream(target,protectedInstall?FileMode.CreateNew:FileMode.Create,FileAccess.Write,FileShare.None))output.Write(bytes,0,bytes.Length);
   if(!SameFile(target,bytes))throw new IOException("Written component integrity check failed");
  }
  private static void CreateProtectedDirectory(string path){
   RejectReparsePath(path);
   if(!Directory.Exists(path)){
    var acl=new DirectorySecurity();acl.SetAccessRuleProtection(true,false);
    foreach(var sid in new[]{WellKnownSidType.LocalSystemSid,WellKnownSidType.BuiltinAdministratorsSid})acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid,null),FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
    acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid,null),FileSystemRights.ReadAndExecute,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
    Directory.CreateDirectory(path,acl);
   }
   ValidateProtectedSecurity(Directory.GetAccessControl(path));
  }
  private static void ValidateProtectedSecurity(FileSystemSecurity security){
   var admins=new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null);var system=new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null);
   var owner=security.GetOwner(typeof(SecurityIdentifier));
   if(!admins.Equals(owner)&&!system.Equals(owner))throw new IOException("Camera setup path is not owned by administrators or SYSTEM.");
   const FileSystemRights writes=FileSystemRights.Write|FileSystemRights.Delete|FileSystemRights.DeleteSubdirectoriesAndFiles|FileSystemRights.ChangePermissions|FileSystemRights.TakeOwnership;
   foreach(FileSystemAccessRule rule in security.GetAccessRules(true,true,typeof(SecurityIdentifier)))if(rule.AccessControlType==AccessControlType.Allow&&(rule.FileSystemRights&writes)!=0&&!rule.IdentityReference.Equals(admins)&&!rule.IdentityReference.Equals(system))throw new IOException("Camera setup directory is writable by a non-administrator.");
  }
  public static bool IsCameraInstalled(){
   try{
    foreach(var view in new[]{RegistryView.Registry64,RegistryView.Registry32})using(var hive=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,view)){
     string name=view==RegistryView.Registry64?"obs-virtualcam-module64.dll":"obs-virtualcam-module32.dll";
     using(var module=hive.OpenSubKey(@"SOFTWARE\Classes\CLSID\"+Filter+@"\InprocServer32"))using(var device=hive.OpenSubKey(Category+Filter)){
      if(module==null||device==null||Convert.ToString(device.GetValue("FriendlyName"))!="LensioCam")return false;
      string path=Convert.ToString(module.GetValue(""));
      // Existing working OBS registrations remain usable; no needless replacement or elevation.
      if(string.IsNullOrEmpty(path)||!File.Exists(path)||!string.Equals(Path.GetFileName(path),name,StringComparison.OrdinalIgnoreCase))return false;
     }
    }
    return true;
   }catch{return false;}
  }
  public static int InstallCameraElevated(){
   if(!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))return 5;
   try{
    using(var mutex=new Mutex(false,"Local\\LensioVirtualCameraSetup")){
     if(!mutex.WaitOne(0))return 1618;
     try{
      string root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Lensio");
      CreateProtectedDirectory(root);CreateProtectedDirectory(Path.Combine(root,"VirtualCamera"));CreateProtectedDirectory(DriverDirectory);
      foreach(string name in new[]{"obs-virtualcam-module64.dll","obs-virtualcam-module32.dll","placeholder.png"})WriteComponent(DriverDirectory,name,true);
      foreach(var view in new[]{RegistryView.Registry64,RegistryView.Registry32}){
       string name=view==RegistryView.Registry64?"obs-virtualcam-module64.dll":"obs-virtualcam-module32.dll";
       string registrar=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),view==RegistryView.Registry64?"System32":"SysWOW64","regsvr32.exe");
       using(var p=Process.Start(new ProcessStartInfo{FileName=registrar,Arguments="/i /s \""+Path.Combine(DriverDirectory,name)+"\"",WorkingDirectory=Environment.SystemDirectory,UseShellExecute=false,CreateNoWindow=true})){
        if(!p.WaitForExit(30000)){p.Kill();throw new IOException("Camera registration timed out");}if(p.ExitCode!=0)throw new IOException("Camera registration failed");
       }
       using(var hive=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,view)){
        using(var module=hive.OpenSubKey(@"SOFTWARE\Classes\CLSID\"+Filter+@"\InprocServer32"))if(module==null||!string.Equals(Convert.ToString(module.GetValue("")),Path.Combine(DriverDirectory,name),StringComparison.OrdinalIgnoreCase))throw new IOException("Camera registration could not be verified");
        using(var key=hive.OpenSubKey(Category+Filter,true)){if(key==null)throw new IOException("Camera device registration missing");key.SetValue("FriendlyName","LensioCam",RegistryValueKind.String);}
       }
      }
      return IsCameraInstalled()?0:1;
     }finally{mutex.ReleaseMutex();}
    }
   }catch{return 1;}
  }
  public static bool RequestCameraInstall(IWin32Window owner){
   try{
    using(var p=Process.Start(new ProcessStartInfo{FileName=Application.ExecutablePath,Arguments="--install-virtual-camera",WorkingDirectory=Environment.SystemDirectory,UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden})){
     p.WaitForExit();if(p.ExitCode==0&&IsCameraInstalled())return true;
    }
    MessageBox.Show(owner,"Virtual camera setup did not complete. Close apps using the camera and try again from the tray menu.","Lensio camera setup",MessageBoxButtons.OK,MessageBoxIcon.Warning);
   }catch(System.ComponentModel.Win32Exception e){if(e.NativeErrorCode!=1223)MessageBox.Show(owner,"Unable to start camera setup.","Lensio camera setup",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
   return false;
  }
  public static string GetAdbPath(){lock(usbGate){
   if(adbPath!=null)return adbPath;
   string directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LensioStudio","runtime","adb-36.0.2");
   RejectReparsePath(directory);Directory.CreateDirectory(directory);
   foreach(string name in new[]{"adb.exe","AdbWinApi.dll","AdbWinUsbApi.dll"})WriteComponent(directory,name,false);
   adbPath=Path.Combine(directory,"adb.exe");return adbPath;
  }}
  public static bool InstallWebViewRuntime(){
   string directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LensioStudio","runtime","webview-setup");
   RejectReparsePath(directory);Directory.CreateDirectory(directory);WriteComponent(directory,"MicrosoftEdgeWebview2Setup.exe",false);
   using(var p=Process.Start(new ProcessStartInfo{FileName=Path.Combine(directory,"MicrosoftEdgeWebview2Setup.exe"),Arguments="/silent /install",WorkingDirectory=directory,UseShellExecute=false,CreateNoWindow=true})){return p.WaitForExit(180000)&&p.ExitCode==0;}
  }
 }
 public partial class MainForm {
  private void CheckVirtualCameraSetup(){
   if(BundledSetup.IsCameraInstalled())return;
   if(MessageBox.Show(this,"Install the LensioCam virtual camera for Discord and other video apps?\n\nWindows will ask for administrator approval for this installation only.","Lensio camera setup",MessageBoxButtons.YesNo,MessageBoxIcon.Information)==DialogResult.Yes && BundledSetup.RequestCameraInstall(this))return;
   if(isVirtualCamActive)OnToggleVcam(this,EventArgs.Empty);
  }
 }
}
