# LensioCam virtual camera for Windows

Run `register_virtual_cam.bat` from the project directory to install or repair the DirectShow registration. Administrator access is required. The installer uses the OBS virtual-camera modules bundled beside the driver script or already installed with OBS Studio.

Select **LensioCam** in Discord, browser video settings, Teams or other camera applications. LensioStudio must be running to publish the phone image. Restart an application that still displays a cached camera name.

Windows exposes one LensioCam device per architecture. The installer removes the old ApexCam/LensioStudio duplicate alias after verifying that it references the same filter. The shared OBS module remains installed. Removing Lensio branding restores the original OBS device name.

The installer validates both 64-bit and 32-bit module paths and returns a nonzero exit code on failure. For diagnostics:

```powershell
.\apexcam\driver\register_vcam.ps1 -Action status
```

`tests/DirectShowCaptureSmoke.cs` checks the actual Windows device name, rejects duplicate aliases, starts capture and verifies receipt of a video sample. Run separate x64 and x86 builds to check both driver registrations.
