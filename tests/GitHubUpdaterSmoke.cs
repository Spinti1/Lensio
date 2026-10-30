using System;
using System.IO;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using ApexCam;
class GitHubUpdaterSmoke {
 static void Check(bool ok,string text){if(!ok)throw new Exception(text);}
 static string Release(string url,string digest,bool draft){return new JavaScriptSerializer().Serialize(new {tag_name="v99.0.0",draft=draft,prerelease=false,body="Test",assets=new[]{new{name="LensioStudio.exe",browser_download_url=url,digest=digest,size=4}}});}
 static int Main(){
  string good="https://github.com/Spinti1/Lensio/releases/download/v99.0.0/LensioStudio.exe",hash=new string('a',64);
  Check(GitHubRelease.Parse(Release(good,"sha256:"+hash,false)).Assets.Count==1,"Valid release rejected");
  Check(GitHubRelease.Parse(Release(good,"sha256:"+hash,true))==null,"Draft offered");
  Check(GitHubRelease.Parse(Release("https://example.com/app.exe","sha256:"+hash,false)).Assets.Count==0,"Untrusted download accepted");
  Check(GitHubRelease.Parse(Release(good,"",false)).Assets.Count==0,"Missing digest accepted");
  Check(GitHubRelease.Parse(Release(good+"?override=1","sha256:"+hash,false)).Assets.Count==0,"Query override accepted");
  Check(GitHubRelease.Parse(Release(good.Replace("v99.0.0/","v98.0.0/"),"sha256:"+hash,false)).Assets.Count==0,"Wrong release version accepted");
  foreach(int length in new[]{3,5}){
   bool rejected=false;try{using(var input=new MemoryStream(new byte[length]))using(var output=new MemoryStream())GitHubRelease.CopyBounded(input,output,4);}catch(InvalidDataException){rejected=true;}
   Check(rejected,"Incorrect download length accepted");
  }
  using(var input=new MemoryStream(new byte[4]))using(var output=new MemoryStream()){GitHubRelease.CopyBounded(input,output,4);Check(output.Length==4,"Exact release length rejected");}
  string file=Path.GetTempFileName();try{File.WriteAllText(file,"verified");string digest;using(var input=File.OpenRead(file))using(var sha=SHA256.Create())digest=BitConverter.ToString(sha.ComputeHash(input)).Replace("-","");Check(GitHubRelease.Verify(file,digest),"Valid hash rejected");File.AppendAllText(file,"changed");Check(!GitHubRelease.Verify(file,digest),"Tampered update accepted");}finally{File.Delete(file);}
  Console.WriteLine("PASS stable releases, trusted URLs, required digests and tamper detection");return 0;
 }
}
