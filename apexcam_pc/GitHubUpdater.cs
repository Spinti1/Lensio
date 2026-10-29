using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
namespace ApexCam {
 public sealed class ReleaseAsset {public string Name,Url,Hash;public long Size;}
 public sealed class GitHubRelease {
  public const string Repository="Spinti1/Lensio";
  public Version Version;public string Notes;public List<ReleaseAsset> Assets=new List<ReleaseAsset>();
  public static GitHubRelease Parse(string json) {
   var data=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(json);
   if((data.ContainsKey("draft") && Convert.ToBoolean(data["draft"])) || (data.ContainsKey("prerelease") && Convert.ToBoolean(data["prerelease"])))return null;
   Version version;if(!Version.TryParse(Convert.ToString(data["tag_name"]).TrimStart('v'),out version))return null;
   var release=new GitHubRelease {Version=version,Notes=data.ContainsKey("body")?Convert.ToString(data["body"]):""};
   foreach(var raw in (System.Collections.IEnumerable)data["assets"]) {
    var item=(Dictionary<string,object>)raw;string name=Convert.ToString(item["name"]);
    if(name!="LensioStudio.exe" && name!="LensioCam.apk")continue;
    string url=Convert.ToString(item["browser_download_url"]),digest=item.ContainsKey("digest")?Convert.ToString(item["digest"]):"";
    if(!url.StartsWith("https://github.com/"+Repository+"/releases/download/",StringComparison.Ordinal) ||
      !System.Text.RegularExpressions.Regex.IsMatch(digest??"",@"\Asha256:[0-9a-fA-F]{64}\z"))continue;
    long size=Convert.ToInt64(item["size"]);if(size<=0 || size>200*1024*1024)continue;
    release.Assets.Add(new ReleaseAsset {Name=name,Url=url,Hash=digest.Substring(7),Size=size});
   }
   return release;
  }
  public static bool Verify(string path,string hash) {
   using(var input=File.OpenRead(path))using(var sha=SHA256.Create())return string.Equals(BitConverter.ToString(sha.ComputeHash(input)).Replace("-",""),hash,StringComparison.OrdinalIgnoreCase);
  }
  private sealed class Client:WebClient {protected override WebRequest GetWebRequest(Uri uri){var request=base.GetWebRequest(uri);request.Timeout=15000;return request;}}
  public static WebClient NewClient(){ServicePointManager.SecurityProtocol|=SecurityProtocolType.Tls12;var c=new Client();c.Encoding=Encoding.UTF8;c.Headers["User-Agent"]="LensioStudio-Updater";c.Headers["Accept"]="application/vnd.github+json";return c;}
 }
 public partial class MainForm {
  private int githubUpdateBusy;
  private void CheckGitHubUpdate(bool manual) {
   if(Interlocked.CompareExchange(ref githubUpdateBusy,1,0)!=0)return;
   ThreadPool.QueueUserWorkItem(_ => {
    try {
     GitHubRelease release;using(var client=GitHubRelease.NewClient())release=GitHubRelease.Parse(client.DownloadString("https://api.github.com/repos/"+GitHubRelease.Repository+"/releases/latest"));
     var current=typeof(MainForm).Assembly.GetName().Version;
     bool available=release!=null && release.Version>new Version(current.Major,current.Minor,current.Build) && release.Assets.Exists(a=>a.Name=="LensioStudio.exe");
     SafeBeginInvoke(() => {
      if(!available){if(manual)MessageBox.Show(this,"You are using the latest LensioStudio release.","Updates",MessageBoxButtons.OK,MessageBoxIcon.Information);return;}
      if(!manual){trayIcon.ShowBalloonTip(6000,"Lensio update available","Version "+release.Version+" is ready. Use Check for app updates in the tray menu.",ToolTipIcon.Info);return;}
      if(MessageBox.Show(this,"Install LensioStudio "+release.Version+" from GitHub?\n\nThe app will save your workspace and restart. Phone updates still require Android confirmation.","Lensio update",MessageBoxButtons.YesNo,MessageBoxIcon.Question)==DialogResult.Yes)DownloadGitHubUpdate(release);
     });
    } catch(Exception error){if(manual)SafeBeginInvoke(()=>MessageBox.Show(this,"Unable to check GitHub: "+error.Message,"Updates",MessageBoxButtons.OK,MessageBoxIcon.Warning));}
    finally{Interlocked.Exchange(ref githubUpdateBusy,0);}
   });
  }
  private void DownloadGitHubUpdate(GitHubRelease release) {
   if(Interlocked.CompareExchange(ref githubUpdateBusy,1,0)!=0)return;
   ThreadPool.QueueUserWorkItem(_ => {
    try {
     string stage=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LensioStudio","updates",release.Version.ToString());Directory.CreateDirectory(stage);
     var targets=new Dictionary<string,string>();
     foreach(var asset in release.Assets) {
      string path=Path.Combine(stage,asset.Name);
      using(var client=GitHubRelease.NewClient())client.DownloadFile(asset.Url,path);
      if(new FileInfo(path).Length!=asset.Size || !GitHubRelease.Verify(path,asset.Hash))throw new InvalidDataException("Update integrity verification failed: "+asset.Name);
      targets[asset.Name]=path;
     }
     string executable=Application.ExecutablePath,folder=Path.GetDirectoryName(executable);
     var script=new StringBuilder("$ErrorActionPreference='Stop'\r\n");
     script.AppendLine("Wait-Process -Id "+Process.GetCurrentProcess().Id+" -ErrorAction SilentlyContinue");
     script.AppendLine("Start-Sleep -Milliseconds 500");
     script.AppendLine("try {");
     foreach(var pair in targets) {
      string destination=Path.Combine(folder,pair.Key);
      script.AppendLine("Copy-Item -LiteralPath "+PsQuote(pair.Value)+" -Destination "+PsQuote(destination+".new")+" -Force");
      script.AppendLine("if(Test-Path -LiteralPath "+PsQuote(destination)+"){[IO.File]::Replace("+PsQuote(destination+".new")+","+PsQuote(destination)+",$null)}else{[IO.File]::Move("+PsQuote(destination+".new")+","+PsQuote(destination)+")}");
     }
     script.AppendLine("} catch { $_ | Out-String | Set-Content -LiteralPath "+PsQuote(Path.Combine(stage,"update-error.txt"))+" }");
     script.AppendLine("Start-Process -FilePath "+PsQuote(executable)+" -WorkingDirectory "+PsQuote(folder));
     string helper=Path.Combine(stage,"install.ps1");File.WriteAllText(helper,script.ToString(),new UTF8Encoding(true));
     SafeBeginInvoke(() => {
      SaveWorkspace();
      Process.Start(new ProcessStartInfo {FileName="powershell.exe",Arguments="-NoProfile -ExecutionPolicy Bypass -File \""+helper+"\"",UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden});
      Close();
     });
    }catch(Exception error){SafeBeginInvoke(()=>MessageBox.Show(this,"Unable to install update: "+error.Message,"Updates",MessageBoxButtons.OK,MessageBoxIcon.Error));}
    finally{Interlocked.Exchange(ref githubUpdateBusy,0);}
   });
  }
  private static string PsQuote(string value){return "'"+value.Replace("'","''")+"'";}
 }
}
