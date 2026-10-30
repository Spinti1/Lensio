using System;
using System.IO;
using System.Security.Cryptography;
using System.Net;
using ApexCam;
class UpdateSetupSmoke {
 static void Check(bool value,string message){if(!value)throw new Exception(message);}
 static string Hash(string path){using(var s=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(s)).Replace("-","");}
 static int Main(){
  string root=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-data","setup-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
  string source=typeof(MainForm).Assembly.Location;string destination=Path.Combine(root,"LensioStudio.exe"),layout=Path.Combine(root,"workspace.json");
  File.WriteAllText(destination,"previous version");File.WriteAllText(layout,"saved layout");
  bool rejected=false;try{UpdateSetup.InstallVerified(source,root,new string('0',64));}catch(InvalidDataException){rejected=true;}
  Check(rejected&&File.ReadAllText(destination)=="previous version","Unverified update changed the installed app");
  string hash=Hash(source);UpdateSetup.InstallVerified(source,root,hash);
  Check(Hash(destination)==hash,"Verified update was not installed in the selected folder");
  Check(File.ReadAllText(layout)=="saved layout","Setup modified layout data");
  Check(Directory.GetFiles(root,"*.tmp").Length==0,"Setup left a temporary executable");
  Check(UpdateSetup.QuoteArgument(@"C:\Studio folder\")=="\"C:\\Studio folder\\\\\"","Setup folder argument was not escaped correctly");
  using(var server=new DiscoveryIdentityServer(0)){
   server.Start();using(var client=new WebClient()){
    Check(client.DownloadString("http://127.0.0.1:"+server.BoundPort+"/api/identity").Contains("LensioStudio"),"Camera discovery identity missing");
    foreach(string path in new[]{"/api/version","/download/LensioCam.apk","/trigger-update"}){
     rejected=false;try{client.DownloadString("http://127.0.0.1:"+server.BoundPort+path);}catch(WebException e){using(var response=(HttpWebResponse)e.Response)rejected=response!=null&&response.StatusCode==HttpStatusCode.NotFound;}
     Check(rejected,"Legacy PC phone-update endpoint is still available");
    }
   }
  }
  // Keep artifacts under ignored test-data for inspection; no installed user files are touched.
  Console.WriteLine("PASS verified atomic setup in current folder, saved layout, tamper rejection, argument quoting and removal of APK update endpoints");return 0;
 }
}
