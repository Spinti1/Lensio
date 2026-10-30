using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
namespace ApexCam {
 public static class UpdateSetup {
  public static string QuoteArgument(string value){
   var b=new StringBuilder("\"");int slashes=0;
   foreach(char c in value){if(c=='\\'){slashes++;continue;}if(c=='\"'){b.Append('\\',slashes*2+1);b.Append(c);}else{b.Append('\\',slashes);b.Append(c);}slashes=0;}
   b.Append('\\',slashes*2);return b.Append('"').ToString();
  }
  public static void InstallVerified(string source,string folder,string hash){
   if(!Regex.IsMatch(hash??"",@"\A[0-9a-fA-F]{64}\z"))throw new InvalidDataException("Invalid update digest");
   folder=Path.GetFullPath(folder);source=Path.GetFullPath(source);
   if(!Directory.Exists(folder))throw new DirectoryNotFoundException("The current installation folder is unavailable.");
   string destination=Path.Combine(folder,"LensioStudio.exe");
   if(string.Equals(source,destination,StringComparison.OrdinalIgnoreCase))throw new IOException("Setup must run from the verified download.");
   BundledSetup.RejectReparsePath(source);BundledSetup.RejectReparsePath(folder);BundledSetup.RejectReparsePath(destination);
   if(!GitHubRelease.Verify(source,hash))throw new InvalidDataException("Setup integrity verification failed");
   string temporary=Path.Combine(folder,".lensio-update-"+Guid.NewGuid().ToString("N")+".tmp");
   try{
    using(var input=File.OpenRead(source))using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))input.CopyTo(output);
    if(!GitHubRelease.Verify(temporary,hash))throw new InvalidDataException("Written update integrity verification failed");
    if(File.Exists(destination))File.Replace(temporary,destination,null);else File.Move(temporary,destination);
   }finally{if(File.Exists(temporary))File.Delete(temporary);}
  }
  public static void Run(string folder,int parentId,string hash){
   if(new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)){MessageBox.Show("Open LensioStudio without administrator rights to install app updates.","LensioStudio Setup");return;}
   if(parentId<=0||parentId==Process.GetCurrentProcess().Id||!Regex.IsMatch(hash??"",@"\A[0-9a-fA-F]{64}\z")){MessageBox.Show("Invalid setup request.","LensioStudio Setup");return;}
   folder=Path.GetFullPath(folder);
   Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
   using(var dialog=new Form{Text="LensioStudio Setup",StartPosition=FormStartPosition.CenterScreen,ClientSize=new Size(510,245),FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false,BackColor=Color.FromArgb(30,33,38),ForeColor=Color.White,Font=new Font("Segoe UI",9f)}){
    var title=new Label{Text="Update LensioStudio",Location=new Point(20,18),Size=new Size(465,28),Font=new Font("Segoe UI",14f,FontStyle.Bold)};
    var path=new Label{Text="Installation folder: "+folder,Location=new Point(20,57),Size=new Size(465,45),AutoEllipsis=true};
    var status=new Label{Text="Waiting for LensioStudio to close safely...",Location=new Point(20,110),Size=new Size(465,52)};
    var unsigned=new Label{Text="This installer is not code-signed. Your saved layout is preserved.",Location=new Point(20,169),Size=new Size(465,24),ForeColor=Color.LightGray};
    var install=new Button{Text="Install and open",Location=new Point(286,204),Size=new Size(135,29),Enabled=false};
    var cancel=new Button{Text="Cancel",Location=new Point(426,204),Size=new Size(65,29),DialogResult=DialogResult.Cancel};
    dialog.Controls.AddRange(new Control[]{title,path,status,unsigned,install,cancel});dialog.CancelButton=cancel;
    dialog.Shown+=async(s,e)=>{
     try{
      bool exited=await Task.Run(()=>{try{using(var parent=Process.GetProcessById(parentId))return parent.WaitForExit(30000);}catch(ArgumentException){return true;}});
      if(dialog.IsDisposed)return;if(!exited)throw new IOException("LensioStudio is still running. Close it and retry the update.");
      if(!GitHubRelease.Verify(Application.ExecutablePath,hash))throw new InvalidDataException("Setup integrity verification failed");
      install.Enabled=true;status.Text="Ready to install in the current folder. Administrator rights are not requested.";
     }catch(Exception error){if(!dialog.IsDisposed)status.Text=error.Message;}
    };
    install.Click+=async(s,e)=>{
     install.Enabled=false;cancel.Enabled=false;dialog.ControlBox=false;status.Text="Installing verified update...";
     try{
      string source=Application.ExecutablePath;await Task.Run(()=>InstallVerified(source,folder,hash));
      using(var app=Process.Start(new ProcessStartInfo{FileName=Path.Combine(folder,"LensioStudio.exe"),WorkingDirectory=folder,UseShellExecute=false})){}
      dialog.Close();
     }catch(Exception error){status.Text="Unable to install: "+error.Message;install.Enabled=true;cancel.Enabled=true;dialog.ControlBox=true;}
    };
    Application.Run(dialog);
   }
  }
 }
}
