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
   Version version;if(!Version.TryParse(Convert.ToString(data["tag_name"]).TrimStart('v'),out version) || version.Build<0 || version.Revision>=0)return null;
   var release=new GitHubRelease {Version=version,Notes=data.ContainsKey("body")?Convert.ToString(data["body"]):""};
   foreach(var raw in (System.Collections.IEnumerable)data["assets"]) {
    var item=(Dictionary<string,object>)raw;string name=Convert.ToString(item["name"]);
    if(name!="LensioStudio.exe")continue;
    string url=Convert.ToString(item["browser_download_url"]),digest=item.ContainsKey("digest")?Convert.ToString(item["digest"]):"";
    if(!IsReleaseUrl(url,version,name) ||
      !System.Text.RegularExpressions.Regex.IsMatch(digest??"",@"\Asha256:[0-9a-fA-F]{64}\z"))continue;
    long size=Convert.ToInt64(item["size"]);if(size<=0 || size>200*1024*1024)continue;
    release.Assets.Add(new ReleaseAsset {Name=name,Url=url,Hash=digest.Substring(7),Size=size});
   }
   return release;
  }
  public static bool IsReleaseUrl(string url,Version version,string name){
   Uri uri;return Uri.TryCreate(url,UriKind.Absolute,out uri)&&uri.Scheme=="https"&&uri.Host=="github.com"&&uri.IsDefaultPort&&uri.UserInfo.Length==0&&uri.Query.Length==0&&uri.Fragment.Length==0&&uri.AbsolutePath=="/"+Repository+"/releases/download/v"+version+"/"+name;
  }
  public static void DownloadVerified(ReleaseAsset asset,string path){
   if(asset.Size<1||asset.Size>200*1024*1024)throw new InvalidDataException("Invalid update size");
   BundledSetup.RejectReparsePath(path);
   bool created=false;
   try{
    using(var client=NewClient())using(var input=client.OpenRead(asset.Url))using(var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None)){
     created=true;CopyBounded(input,output,asset.Size);
    }
    if(new FileInfo(path).Length!=asset.Size||!Verify(path,asset.Hash))throw new InvalidDataException("Update integrity verification failed");
   }catch{if(created&&File.Exists(path))File.Delete(path);throw;}
  }
  public static void CopyBounded(Stream input,Stream output,long expected){
   var buffer=new byte[32768];long total=0;int count;
   while((count=input.Read(buffer,0,buffer.Length))>0){total+=count;if(total>expected)throw new InvalidDataException("Update exceeds release size");output.Write(buffer,0,count);}
   if(total!=expected)throw new InvalidDataException("Incomplete update download");
  }
  public static bool Verify(string path,string hash) {
   using(var input=File.OpenRead(path))using(var sha=SHA256.Create())return string.Equals(BitConverter.ToString(sha.ComputeHash(input)).Replace("-",""),hash,StringComparison.OrdinalIgnoreCase);
  }
  private sealed class Client:WebClient {
   protected override WebRequest GetWebRequest(Uri uri){if(uri.Scheme!="https")throw new InvalidDataException("Updates require HTTPS");var request=base.GetWebRequest(uri);request.Timeout=15000;var http=request as HttpWebRequest;if(http!=null)http.ReadWriteTimeout=15000;return request;}
   protected override WebResponse GetWebResponse(WebRequest request){var response=base.GetWebResponse(request);if(response.ResponseUri.Scheme!="https"){response.Close();throw new InvalidDataException("Update redirect requires HTTPS");}return response;}
  }
  public static WebClient NewClient(){ServicePointManager.SecurityProtocol|=SecurityProtocolType.Tls12;var c=new Client();c.Encoding=Encoding.UTF8;c.Headers["User-Agent"]="LensioStudio-Updater";c.Headers["Accept"]="application/vnd.github+json";return c;}
 }
 public partial class MainForm {
  private int githubUpdateBusy;
  private void SetUpdateButton(bool busy,string text){SafeBeginInvoke(()=>{btnTopUpdate.Enabled=!busy;btnTopUpdate.Text=text;});}
  private void CheckGitHubUpdate(bool manual) {
   if(Interlocked.CompareExchange(ref githubUpdateBusy,1,0)!=0)return;
   SetUpdateButton(true,"Checking for updates...");
   ThreadPool.QueueUserWorkItem(_ => {
    GitHubRelease release;
    try {using(var client=GitHubRelease.NewClient())release=GitHubRelease.Parse(client.DownloadString("https://api.github.com/repos/"+GitHubRelease.Repository+"/releases/latest"));}
    catch(Exception error){if(manual)SafeBeginInvoke(()=>MessageBox.Show(this,"Unable to check GitHub: "+error.Message,"Updates",MessageBoxButtons.OK,MessageBoxIcon.Warning));return;}
    finally{Interlocked.Exchange(ref githubUpdateBusy,0);SetUpdateButton(false,"Check for updates");}
    var current=typeof(MainForm).Assembly.GetName().Version;
    bool available=release!=null && release.Version>new Version(current.Major,current.Minor,current.Build) && release.Assets.Exists(a=>a.Name=="LensioStudio.exe");
    SafeBeginInvoke(() => {
     if(!available){if(manual)MessageBox.Show(this,"You are using the latest LensioStudio release.","Updates",MessageBoxButtons.OK,MessageBoxIcon.Information);return;}
     if(MessageBox.Show(this,"LensioStudio "+release.Version+" is available. Download and open Setup?\n\nChecks use GitHub HTTPS. Downloads must match the release's size and SHA-256 digest. Installation saves your layout, closes the app safely, and opens Setup in the current installation folder. These installers are not code-signed.","LensioStudio updates",MessageBoxButtons.YesNo,MessageBoxIcon.Information)==DialogResult.Yes)DownloadGitHubUpdate(release);
    });
   });
  }
  private void DownloadGitHubUpdate(GitHubRelease release) {
   if(Interlocked.CompareExchange(ref githubUpdateBusy,1,0)!=0)return;
   SetUpdateButton(true,"Downloading update...");
   ThreadPool.QueueUserWorkItem(_ => {
    try {
     var asset=release.Assets.Find(a=>a.Name=="LensioStudio.exe");
     if(asset==null||!GitHubRelease.IsReleaseUrl(asset.Url,release.Version,asset.Name))throw new InvalidDataException("Untrusted update asset");
     string stage=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LensioStudio","updates",release.Version.ToString(),Guid.NewGuid().ToString("N"));
     BundledSetup.RejectReparsePath(stage);Directory.CreateDirectory(stage);
     string path=Path.Combine(stage,asset.Name);GitHubRelease.DownloadVerified(asset,path);
     SafeBeginInvoke(() => {
      try {
       string expected=workspaceJson.Serialize(CaptureWorkspace());
       SaveWorkspace();
       if(!workspaceReady||!File.Exists(WorkspacePath)||File.ReadAllText(WorkspacePath)!=expected)throw new IOException("Your layout could not be saved. Update cancelled.");
       string folder=Path.GetDirectoryName(Application.ExecutablePath);
       var info=new ProcessStartInfo{FileName=path,Arguments="--setup "+UpdateSetup.QuoteArgument(folder)+" "+Process.GetCurrentProcess().Id+" "+asset.Hash,WorkingDirectory=folder,UseShellExecute=false};
       using(var setup=Process.Start(info)){if(setup==null)throw new IOException("Unable to start Setup");}
       Close();
      }catch(Exception error){MessageBox.Show(this,"Unable to open Setup: "+error.Message,"Updates",MessageBoxButtons.OK,MessageBoxIcon.Error);}
     });
    }catch(Exception error){SafeBeginInvoke(()=>MessageBox.Show(this,"Unable to download update: "+error.Message,"Updates",MessageBoxButtons.OK,MessageBoxIcon.Error));}
    finally{Interlocked.Exchange(ref githubUpdateBusy,0);SetUpdateButton(false,"Check for updates");}
   });
  }
 }
}
