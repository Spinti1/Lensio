using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Security.AccessControl;
using ApexCam;
class BundledSetupSmoke {
 static void Check(bool value,string text){if(!value)throw new Exception(text);}
 static int Main(){
  foreach(var name in new[]{"obs-virtualcam-module64.dll","obs-virtualcam-module32.dll","placeholder.png","adb.exe","AdbWinApi.dll","AdbWinUsbApi.dll","MicrosoftEdgeWebview2Setup.exe"}){
   byte[] data=BundledSetup.ReadVerifiedPayload(name);Check(data.Length>1000,"Missing payload: "+name);
   if(name.EndsWith(".exe")||name.EndsWith(".dll"))Check(data[0]=='M'&&data[1]=='Z',"Not a Windows executable");
  }
  bool rejected=false;try{BundledSetup.ReadVerifiedPayload("../adb.exe");}catch(InvalidDataException){rejected=true;}Check(rejected,"Unlisted setup path accepted");
  if(!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)){
   Check(BundledSetup.InstallCameraElevated()==5,"Installer must reject an unelevated caller");
   using(var helper=Process.Start(new ProcessStartInfo{FileName=typeof(MainForm).Assembly.Location,Arguments="--install-virtual-camera",UseShellExecute=false,CreateNoWindow=true})){
    Check(helper.WaitForExit(10000)&&helper.ExitCode==5,"Unelevated helper did not exit without starting the app");
   }
  }
  var validate=typeof(BundledSetup).GetMethod("ValidateProtectedSecurity",BindingFlags.Static|BindingFlags.NonPublic);
  var acl=new DirectorySecurity();acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null));
  acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid,null),FileSystemRights.ReadAndExecute,AccessControlType.Allow));
  validate.Invoke(null,new object[]{acl});
  acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid,null),FileSystemRights.Write,AccessControlType.Allow));
  rejected=false;try{validate.Invoke(null,new object[]{acl});}catch(TargetInvocationException e){rejected=e.InnerException is IOException;}Check(rejected,"User-writable setup path accepted");
  acl=new DirectorySecurity();acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid,null));
  rejected=false;try{validate.Invoke(null,new object[]{acl});}catch(TargetInvocationException e){rejected=e.InnerException is IOException;}Check(rejected,"Untrusted setup owner accepted");
  var line=typeof(DiscoveryIdentityServer).GetMethod("ReadBoundedLine",BindingFlags.Static|BindingFlags.NonPublic);
  rejected=false;try{line.Invoke(null,new object[]{new StringReader(new string('X',4097)+"\r\n")});}catch(TargetInvocationException e){rejected=e.InnerException is InvalidDataException;}Check(rejected,"Unbounded HTTP line accepted");
  Check((string)line.Invoke(null,new object[]{new StringReader("GET /api/version HTTP/1.1\r\n")})=="GET /api/version HTTP/1.1","Normal HTTP line rejected");
  string adb=BundledSetup.GetAdbPath();
  using(var p=Process.Start(new ProcessStartInfo{FileName=adb,Arguments="version",UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true})){
   string output=p.StandardOutput.ReadToEnd();Check(p.WaitForExit(10000)&&p.ExitCode==0&&output.Contains("Android Debug Bridge version"),"Bundled USB tool cannot start without a local SDK");
  }
  Console.WriteLine("PASS verified embedded payloads, path allowlist, least-privilege helper, bounded HTTP and standalone ADB");return 0;
 }
}
