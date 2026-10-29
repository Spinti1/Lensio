using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
namespace ApexCam {
    public partial class MainForm {
        private readonly Dictionary<string,string> phoneNames=new Dictionary<string,string>();
        private readonly string phoneNamesPath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LensioStudio","phone-names.txt");
        private void LoadPhoneNames() {
            try {
                if(!File.Exists(phoneNamesPath))return;
                foreach(string line in File.ReadAllLines(phoneNamesPath)) {
                    var pair=line.Split('\t');
                    if(pair.Length==2)phoneNames[Encoding.UTF8.GetString(Convert.FromBase64String(pair[0]))]=Encoding.UTF8.GetString(Convert.FromBase64String(pair[1]));
                }
            } catch {}
        }
        private string FriendlyPhoneName(PhoneDeviceInfo info) {
            string name;
            if(phoneNames.TryGetValue(info.Id,out name))return name;
            if(info.Name=="SM-A217F")return "Galaxy A21s";
            if(info.Name=="SM-J510FN")return "Galaxy J5";
            return info.Name;
        }
        private void RenamePhone(PhoneDeviceInfo info) {
            using(var dialog=new Form {Text="Phone name",ClientSize=new Size(340,100),StartPosition=FormStartPosition.CenterParent,
                FormBorderStyle=FormBorderStyle.FixedDialog,BackColor=ColorCardBg,ForeColor=ColorTextPrimary,MinimizeBox=false,MaximizeBox=false,ShowInTaskbar=false}) {
                var text=new TextBox {Text=FriendlyPhoneName(info),MaxLength=60,Location=new Point(12,12),Width=316,BackColor=ColorButtonDark,ForeColor=ColorTextPrimary};
                var save=CreateModernButton("Save",ColorAccentCyan,230,53,98,30);save.DialogResult=DialogResult.OK;
                dialog.Controls.Add(text);dialog.Controls.Add(save);dialog.AcceptButton=save;
                if(dialog.ShowDialog(this)!=DialogResult.OK || string.IsNullOrWhiteSpace(text.Text))return;
                phoneNames[info.Id]=text.Text.Trim();
                lock(devicesGate){PhoneSlot slot;if(phoneSlots.TryGetValue(info.Id,out slot))slot.Layer.Name=text.Text.Trim();}
                try {
                    Directory.CreateDirectory(Path.GetDirectoryName(phoneNamesPath));
                    var lines=new List<string>();
                    foreach(var pair in phoneNames)lines.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(pair.Key))+"\t"+Convert.ToBase64String(Encoding.UTF8.GetBytes(pair.Value)));
                    File.WriteAllLines(phoneNamesPath,lines.ToArray());
                } catch {}
                RefreshPhoneCards();RefreshSourcesList();
            }
        }
    }
}
