# Static wallpaper under the live one: the primary monitor (OLED) gets pure black.
#   set-wallpapers.ps1                     -> black on the primary monitor, the others keep theirs
#   set-wallpapers.ps1 -Other my-pic.png   -> also set a picture on the other monitors
param([string]$Other = "")
$src = @'
using System; using System.Runtime.InteropServices;
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
[ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDesktopWallpaper {
  void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorID, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
  [return: MarshalAs(UnmanagedType.LPWStr)] string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorID);
  [return: MarshalAs(UnmanagedType.LPWStr)] string GetMonitorDevicePathAt(uint monitorIndex);
  uint GetMonitorDevicePathCount();
  RECT GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorID);
  void SetBackgroundColor(uint color);
  uint GetBackgroundColor();
  void SetPosition(int position);
  int GetPosition();
}
[ComImport, Guid("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD")] public class DesktopWallpaperClass { }
public static class WP {
  public static string Apply(string primaryImg, string otherImg) {
    var dw = (IDesktopWallpaper)new DesktopWallpaperClass();
    dw.SetBackgroundColor(0);
    dw.SetPosition(4); // DWPOS_FILL
    var log = "";
    for (uint i = 0; i < dw.GetMonitorDevicePathCount(); i++) {
      var id = dw.GetMonitorDevicePathAt(i);
      var r = dw.GetMonitorRECT(id);
      bool primary = r.Left == 0 && r.Top == 0;
      if (primary) dw.SetWallpaper(id, primaryImg); else if (otherImg.Length > 0) dw.SetWallpaper(id, otherImg);
      log += (primary ? "primary" : "other") + " " + r.Left + "," + r.Top + " " + (r.Right - r.Left) + "x" + (r.Bottom - r.Top) + "\n";
    }
    return log;
  }
}
'@
Add-Type -TypeDefinition $src
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$o = if ($Other) { (Resolve-Path $Other).Path } else { "" }
[WP]::Apply("$dir\assets\black.png", $o)
