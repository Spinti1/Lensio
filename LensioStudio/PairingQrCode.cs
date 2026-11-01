// QR version 7 / M, byte mode. Bounded to pairing URLs, with no browser/runtime dependency.
// Adapted from qrcode-generator (c) 2009 Kazuhiko Arase, MIT; see docs/QR-CODE-LICENSE.txt.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
namespace ApexCam {
 public static class PairingQrCode {
  const int Size=45;
  static int Multiply(int a,int b){int r=0;while(b!=0){if((b&1)!=0)r^=a;b>>=1;a<<=1;if((a&256)!=0)a^=0x11d;}return r;}
  static int Bch(int value,int shift,int polynomial){int n=value<<shift;for(int bit=31;bit>=shift;bit--)if((n&(1<<bit))!=0)n^=polynomial<<(bit-shift);return (value<<shift)|n;}
  static byte[] Codewords(string text){
   var input=Encoding.UTF8.GetBytes(text);if(input.Length>122)throw new ArgumentException("Pairing URL exceeds QR capacity.");
   var bits=new List<bool>();Action<int,int> put=(v,n)=>{for(int i=n-1;i>=0;i--)bits.Add(((v>>i)&1)!=0);};
   put(4,4);put(input.Length,8);foreach(byte b in input)put(b,8);put(0,Math.Min(4,992-bits.Count));while(bits.Count%8!=0)bits.Add(false);
   var data=new byte[124];int used=bits.Count/8;for(int i=0;i<bits.Count;i++)if(bits[i])data[i/8]|=(byte)(128>>(i%8));for(int i=used;i<data.Length;i++)data[i]=(byte)((i-used)%2==0?236:17);
   var generator=new int[]{1};int power=1;for(int i=0;i<18;i++){var next=new int[generator.Length+1];for(int j=0;j<generator.Length;j++){next[j]^=generator[j];next[j+1]^=Multiply(generator[j],power);}generator=next;power=Multiply(power,2);}
   var parity=new byte[4][];
   for(int block=0;block<4;block++){var work=new int[49];for(int i=0;i<31;i++)work[i]=data[block*31+i];for(int i=0;i<31;i++){int factor=work[i];for(int j=0;j<generator.Length;j++)work[i+j]^=Multiply(generator[j],factor);}parity[block]=new byte[18];for(int i=0;i<18;i++)parity[block][i]=(byte)work[31+i];}
   var result=new byte[196];int at=0;for(int i=0;i<31;i++)for(int b=0;b<4;b++)result[at++]=data[b*31+i];for(int i=0;i<18;i++)for(int b=0;b<4;b++)result[at++]=parity[b][i];return result;
  }
  static bool Mask(int mask,int r,int c){switch(mask){case 0:return (r+c)%2==0;case 1:return r%2==0;case 2:return c%3==0;case 3:return (r+c)%3==0;case 4:return (r/2+c/3)%2==0;case 5:return r*c%2+r*c%3==0;case 6:return (r*c%2+r*c%3)%2==0;default:return (r*c%3+(r+c)%2)%2==0;}}
  static void Finder(int[,] m,int row,int col){for(int r=-1;r<=7;r++)for(int c=-1;c<=7;c++)if(row+r>=0&&row+r<Size&&col+c>=0&&col+c<Size)m[row+r,col+c]=(r>=0&&r<=6&&(c==0||c==6)||c>=0&&c<=6&&(r==0||r==6)||r>=2&&r<=4&&c>=2&&c<=4)?1:0;}
  // The explicit mask overload is used for independent module-by-module reference tests.
  public static bool[,] Modules(string text,int mask){
   if(text==null)throw new ArgumentNullException("text");if(mask<0||mask>7)throw new ArgumentOutOfRangeException("mask");var words=Codewords(text);
   var m=new int[Size,Size];for(int r=0;r<Size;r++)for(int c=0;c<Size;c++)m[r,c]=-1;Finder(m,0,0);Finder(m,Size-7,0);Finder(m,0,Size-7);
   foreach(int row in new[]{6,22,38})foreach(int col in new[]{6,22,38})if(m[row,col]<0)for(int r=-2;r<=2;r++)for(int c=-2;c<=2;c++)m[row+r,col+c]=(Math.Abs(r)==2||Math.Abs(c)==2||r==0&&c==0)?1:0;
   for(int i=8;i<Size-8;i++){if(m[i,6]<0)m[i,6]=i%2==0?1:0;if(m[6,i]<0)m[6,i]=i%2==0?1:0;}
   int version=Bch(7,12,0x1f25);for(int i=0;i<18;i++){int b=(version>>i)&1;m[i/3,i%3+Size-11]=b;m[i%3+Size-11,i/3]=b;}
   int format=Bch(mask,10,0x537)^0x5412;for(int i=0;i<15;i++){int b=(format>>i)&1;m[i<6?i:i<8?i+1:Size-15+i,8]=b;m[8,i<8?Size-i-1:i<9?15-i:14-i]=b;}m[Size-8,8]=1;
   int rowIndex=Size-1,inc=-1,bit=0;for(int col=Size-1;col>0;col-=2){if(col==6)col--;while(true){for(int c=0;c<2;c++)if(m[rowIndex,col-c]<0){bool dark=bit<words.Length*8&&(words[bit/8]&(128>>(bit%8)))!=0;m[rowIndex,col-c]=(dark^Mask(mask,rowIndex,col-c))?1:0;bit++;}rowIndex+=inc;if(rowIndex<0||rowIndex>=Size){rowIndex-=inc;inc=-inc;break;}}}
   var result=new bool[Size,Size];for(int r=0;r<Size;r++)for(int c=0;c<Size;c++)result[r,c]=m[r,c]!=0;return result;
  }
  static int Penalty(bool[,] m){int score=0,dark=0;
   for(int r=0;r<Size;r++)for(int c=0;c<Size;c++){if(m[r,c])dark++;if(r+1<Size&&c+1<Size&&m[r,c]==m[r+1,c]&&m[r,c]==m[r,c+1]&&m[r,c]==m[r+1,c+1])score+=3;}
   for(int axis=0;axis<2;axis++)for(int line=0;line<Size;line++){int run=0;bool last=false;for(int i=0;i<Size;i++){bool value=axis==0?m[line,i]:m[i,line];if(i==0||last!=value){if(run>=5)score+=run-2;run=1;last=value;}else run++;}if(run>=5)score+=run-2;
    for(int i=0;i<=Size-11;i++){int pattern=0;for(int j=0;j<11;j++)pattern=(pattern<<1)|((axis==0?m[line,i+j]:m[i+j,line])?1:0);if(pattern==0x5d0||pattern==0x05d)score+=40;}
   }return score+Math.Abs(dark*100/(Size*Size)-50)/5*10;
  }
  public static Bitmap Render(string text){bool[,] best=null;int penalty=int.MaxValue;for(int mask=0;mask<8;mask++){var candidate=Modules(text,mask);int p=Penalty(candidate);if(p<penalty){penalty=p;best=candidate;}}
   var bitmap=new Bitmap((Size+8)*4,(Size+8)*4);using(var g=Graphics.FromImage(bitmap)){g.Clear(Color.White);for(int r=0;r<Size;r++)for(int c=0;c<Size;c++)if(best[r,c])g.FillRectangle(Brushes.Black,(c+4)*4,(r+4)*4,4,4);}return bitmap;
  }
 }
}
