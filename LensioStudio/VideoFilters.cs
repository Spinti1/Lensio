using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace ApexCam
{
    public enum FilterMode
    {
        None,
        NightMode,
        BeautyMode,
        Hdr,
        BlackAndWhite,
        Sepia,
        CinematicTeal,
        Cyberpunk,
        WarmStudio
    }

    public enum SourceType
    {
        Camera,
        Image,
        Text,
        Clock,
        ColorBox
    }

    public class ObsLayer : IDisposable
    {
        private readonly object imageLock = new object();
        private ImageAttributes opaqueAttributes;
        private CameraFrameScaler cameraScaler;
        private long frameRevision,scaledRevision=-1;
        private int scaledWidth,scaledHeight;
        private float scaledOpacity;
        public bool IsDisposed { get; private set; }
        public string DeviceId { get; set; }
        public byte[] ExportImagePng() {
            lock(imageLock) {
                if(IsDisposed || BitmapImage==null)return null;
                using(var output=new System.IO.MemoryStream()) {BitmapImage.Save(output,ImageFormat.Png);return output.ToArray();}
            }
        }
        public void SetCameraFrame(Bitmap frame) {
            lock(imageLock) {
                if(IsDisposed) { if(frame!=null)frame.Dispose();return; }
                var old=BitmapImage;BitmapImage=frame;frameRevision++;if(old!=null)old.Dispose();
            }
        }
        public string Id { get; set; }
        public string Name { get; set; }
        public SourceType Type { get; set; }
        public bool Visible { get; set; }
        public bool Locked { get; set; }

        // Canvas coordinates (standardized 1920x1080 base)
        public float X { get; set; }
        public float Y { get; set; }
        public float Width { get; set; }
        public float Height { get; set; }
        public float Opacity { get; set; }

        // Specific to Image
        public Image BitmapImage { get; set; }
        public string ImagePath { get; set; }

        // Specific to Text
        public string TextContent { get; set; }
        public float FontSize { get; set; }
        public Color TextColor { get; set; }
        public Color BgColor { get; set; }
        public Color BorderColor { get; set; }
        public bool ShowBackground { get; set; }

        // Specific to Clock
        public string ClockFormat { get; set; }

        // Specific to ColorBox
        public Color SolidColor { get; set; }

        public ObsLayer()
        {
            Id = Guid.NewGuid().ToString();
            Visible = true;
            Locked = false;
            Opacity = 1.0f;
            TextContent = "Apex Streamer";
            FontSize = 26f;
            TextColor = Color.White;
            BgColor = Color.FromArgb(210, 15, 23, 42);
            BorderColor = Color.FromArgb(240, 14, 165, 233);
            ShowBackground = true;
            ClockFormat = "HH:mm:ss";
            SolidColor = Color.FromArgb(180, 14, 165, 233);
        }

        public RectangleF GetBounds()
        {
            return new RectangleF(X, Y, Width, Height);
        }

        public void SetBounds(RectangleF r)
        {
            X = r.X;
            Y = r.Y;
            Width = r.Width;
            Height = r.Height;
        }

        public void Render(Graphics g, int canvasW, int canvasH)
        {
            lock(imageLock) { if(!IsDisposed) RenderCore(g,canvasW,canvasH); }
        }

        public void RenderCamera(Graphics g,float scaleX,float scaleY)
        {
            lock(imageLock) {
                if(IsDisposed || !Visible || BitmapImage==null || Opacity<=0)return;
                var rect=new Rectangle((int)(X*scaleX),(int)(Y*scaleY),(int)(Width*scaleX),(int)(Height*scaleY));
                if(rect.Width<=0 || rect.Height<=0)return;
                // Keep oversized off-canvas sources bounded; use the original renderer for extreme zooms.
                if(rect.Width>8192 || rect.Height>8192) {
                    var state=g.Save();
                    try{g.ScaleTransform(scaleX,scaleY);RenderCore(g,1920,1080);}finally{g.Restore(state);}
                    return;
                }
                Bitmap image=(Bitmap)BitmapImage;
                if(rect.Width!=image.Width || rect.Height!=image.Height || Opacity<1) {
                    if(cameraScaler==null)cameraScaler=new CameraFrameScaler();
                    if(scaledRevision!=frameRevision || scaledWidth!=rect.Width || scaledHeight!=rect.Height || scaledOpacity!=Opacity) {
                        cameraScaler.Scale(image,rect.Width,rect.Height,Opacity);
                        scaledRevision=frameRevision;scaledWidth=rect.Width;scaledHeight=rect.Height;scaledOpacity=Opacity;
                    }
                    image=cameraScaler.Output;
                }
                var previous=g.CompositingMode;
                try {
                    g.CompositingMode=Opacity>=1 ? CompositingMode.SourceCopy : CompositingMode.SourceOver;
                    g.DrawImageUnscaled(image,rect.Location);
                } finally {g.CompositingMode=previous;}
            }
        }

        private void RenderCore(Graphics g, int canvasW, int canvasH)
        {
            if (!Visible) return;

            float alpha = Math.Max(0.0f, Math.Min(1.0f, Opacity));

            switch (Type)
            {
                case SourceType.Camera:
                case SourceType.Image:
                    if (BitmapImage != null)
                    {
                        if(Type==SourceType.Camera && alpha>=1f) {
                            if(opaqueAttributes==null) {
                                opaqueAttributes=new ImageAttributes();
                                opaqueAttributes.SetWrapMode(WrapMode.TileFlipXY);
                            }
                            var previous=g.CompositingMode;
                            try {
                                // JPEG camera frames are opaque. Copy them without an identity color matrix.
                                g.CompositingMode=CompositingMode.SourceCopy;
                                g.InterpolationMode=InterpolationMode.Bilinear;
                                g.SmoothingMode=SmoothingMode.HighSpeed;
                                g.DrawImage(BitmapImage,new Rectangle((int)X,(int)Y,(int)Width,(int)Height),
                                    0,0,BitmapImage.Width,BitmapImage.Height,GraphicsUnit.Pixel,opaqueAttributes);
                            } finally {g.CompositingMode=previous;}
                            break;
                        }
                        try
                        {
                            using (ImageAttributes attr = new ImageAttributes())
                            {
                                float[][] mat = new float[][]
                                {
                                    new float[] { 1, 0, 0, 0, 0 },
                                    new float[] { 0, 1, 0, 0, 0 },
                                    new float[] { 0, 0, 1, 0, 0 },
                                    new float[] { 0, 0, 0, alpha, 0 },
                                    new float[] { 0, 0, 0, 0, 1 }
                                };
                                attr.SetColorMatrix(new ColorMatrix(mat));
                                attr.SetWrapMode(WrapMode.TileFlipXY);
                                g.InterpolationMode = Type==SourceType.Camera ? InterpolationMode.Bilinear : InterpolationMode.HighQualityBicubic;
                                g.SmoothingMode = Type==SourceType.Camera ? SmoothingMode.HighSpeed : SmoothingMode.HighQuality;
                                g.DrawImage(BitmapImage, new Rectangle((int)X, (int)Y, (int)Width, (int)Height),
                                            0, 0, BitmapImage.Width, BitmapImage.Height, GraphicsUnit.Pixel, attr);
                            }
                        }
                        catch { }
                    }
                    break;

                case SourceType.Text:
                case SourceType.Clock:
                    try
                    {
                        string str = (Type == SourceType.Clock) ? DateTime.Now.ToString(ClockFormat) : TextContent;
                        if (string.IsNullOrEmpty(str)) break;

                        using (Font font = new Font("Segoe UI", FontSize, FontStyle.Bold))
                        {
                            SizeF measured = g.MeasureString(str, font);
                            int padX = 16;
                            int padY = 8;
                            int boxW = (int)Math.Max(Width, measured.Width + padX * 2);
                            int boxH = (int)Math.Max(Height, measured.Height + padY * 2);

                            if (Width < 10) Width = boxW;
                            if (Height < 10) Height = boxH;

                            Rectangle boxRect = new Rectangle((int)X, (int)Y, (int)Width, (int)Height);

                            if (ShowBackground)
                            {
                                int bgA = (int)(alpha * BgColor.A);
                                using (SolidBrush bgB = new SolidBrush(Color.FromArgb(bgA, BgColor)))
                                using (Pen borderP = new Pen(Color.FromArgb((int)(alpha * BorderColor.A), BorderColor), 2f))
                                using (GraphicsPath path = VideoFilters.CreateRoundedRectangle(boxRect, Math.Min(boxRect.Height / 2, 12)))
                                {
                                    g.SmoothingMode = SmoothingMode.AntiAlias;
                                    g.FillPath(bgB, path);
                                    g.DrawPath(borderP, path);
                                }
                            }

                            // Glowing live dot for clock / text
                            int dotSize = (int)(FontSize * 0.45f);
                            int dotX = (int)X + 12;
                            int dotY = (int)Y + ((int)Height - dotSize) / 2;
                            using (SolidBrush dotBrush = new SolidBrush(Color.FromArgb(239, 68, 68)))
                            {
                                g.SmoothingMode = SmoothingMode.AntiAlias;
                                g.FillEllipse(dotBrush, dotX, dotY, dotSize, dotSize);
                            }

                            // Text string
                            using (SolidBrush textB = new SolidBrush(Color.FromArgb((int)(alpha * TextColor.A), TextColor)))
                            {
                                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                                float textX = dotX + dotSize + 8;
                                float textY = Y + (Height - measured.Height) / 2f;
                                g.DrawString(str, font, textB, textX, textY);
                            }
                        }
                    }
                    catch { }
                    break;

                case SourceType.ColorBox:
                    try
                    {
                        int colA = (int)(alpha * SolidColor.A);
                        using (SolidBrush brush = new SolidBrush(Color.FromArgb(colA, SolidColor)))
                        using (GraphicsPath path = VideoFilters.CreateRoundedRectangle(new Rectangle((int)X, (int)Y, (int)Width, (int)Height), 8))
                        {
                            g.SmoothingMode = SmoothingMode.AntiAlias;
                            g.FillPath(brush, path);
                        }
                    }
                    catch { }
                    break;
            }
        }

        public void Dispose()
        {
            lock(imageLock) {
                IsDisposed=true;
                if(opaqueAttributes!=null){opaqueAttributes.Dispose();opaqueAttributes=null;}
                if(cameraScaler!=null){cameraScaler.Dispose();cameraScaler=null;}
                if(BitmapImage!=null)BitmapImage.Dispose();
                BitmapImage=null;
            }
        }

    }

    public class ObsScene
    {
        private readonly List<ObsLayer> layers = new List<ObsLayer>();
        private readonly object sceneLock = new object();

        public int LayerCount
        {
            get
            {
                lock (sceneLock)
                {
                    return layers.Count;
                }
            }
        }

        public List<ObsLayer> GetLayersSnapshot()
        {
            lock (sceneLock)
            {
                return new List<ObsLayer>(layers);
            }
        }

        public void AddLayer(ObsLayer layer)
        {
            lock (sceneLock)
            {
                layers.Insert(0, layer); // Insert at top of list (highest z-order)
            }
        }

        public void RemoveLayer(ObsLayer layer)
        {
            lock (sceneLock)
            {
                layers.Remove(layer);
                layer.Dispose();
            }
        }

        public void MoveUp(ObsLayer layer)
        {
            lock (sceneLock)
            {
                int idx = layers.IndexOf(layer);
                if (idx > 0)
                {
                    layers.RemoveAt(idx);
                    layers.Insert(idx - 1, layer);
                }
            }
        }

        public void MoveDown(ObsLayer layer)
        {
            lock (sceneLock)
            {
                int idx = layers.IndexOf(layer);
                if (idx >= 0 && idx < layers.Count - 1)
                {
                    layers.RemoveAt(idx);
                    layers.Insert(idx + 1, layer);
                }
            }
        }

        public ObsLayer HitTest(float canvasX, float canvasY)
        {
            lock (sceneLock)
            {
                // Top to bottom hit test
                foreach (var l in layers)
                {
                    if (l.Visible && !l.Locked)
                    {
                        if (canvasX >= l.X && canvasX <= l.X + l.Width &&
                            canvasY >= l.Y && canvasY <= l.Y + l.Height)
                        {
                            return l;
                        }
                    }
                }
            }
            return null;
        }

        public void RenderScene(Graphics g, int canvasW, int canvasH)
        {
            lock (sceneLock)
            {
                for(int i=layers.Count-1;i>=0;i--) {
                    var layer=layers[i];
                    if(layer.Type==SourceType.Camera) {
                        layer.RenderCamera(g,canvasW/1920f,canvasH/1080f);
                    } else {
                        var state=g.Save();
                        try {g.ScaleTransform(canvasW/1920f,canvasH/1080f);layer.Render(g,1920,1080);}
                        finally {g.Restore(state);}
                    }
                }
            }
        }
    }

    public static class VideoFilters
    {
        private static float smoothedAutoBrightness = 0.0f;
        private static float smoothedAutoContrast = 0.0f;
        private static int autoFrameCounter = 0;

        public static Bitmap ApplyFiltersAndTransform(
            Bitmap source,
            FilterMode filter,
            float zoom,
            bool mirrorH,
            bool flipV,
            float brightness = 0.0f,
            float contrast = 0.0f,
            float saturation = 0.0f,
            float exposure = 0.0f,
            bool autoMode = false,
            int targetW = 1920,
            int targetH = 1080,
            ObsScene scene = null)
        {
            if (source == null) return null;
            if (targetW <= 0) targetW = 1920;
            if (targetH <= 0) targetH = 1080;

            int srcW = source.Width;
            int srcH = source.Height;

            // Fast-path bypass: Zero latency, zero allocations if no effects/overlays active
            bool hasEffects = filter != FilterMode.None || zoom > 1.01f || mirrorH || flipV ||
                              autoMode || Math.Abs(brightness) > 0.01f || Math.Abs(contrast) > 0.01f ||
                              Math.Abs(saturation) > 0.01f || Math.Abs(exposure) > 0.01f ||
                              (scene != null && scene.LayerCount > 0);

            if (!hasEffects)
            {
                if (srcW == targetW && srcH == targetH)
                {
                    return new Bitmap(source);
                }
                Bitmap scaled = new Bitmap(targetW, targetH, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(scaled))
                {
                    g.InterpolationMode = InterpolationMode.Bilinear;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.DrawImage(source, 0, 0, targetW, targetH);
                }
                return scaled;
            }

            // Crop to the requested output aspect, including portrait frames.
            float targetAspect = targetW / (float)targetH;
            float srcAspect = (float)srcW / srcH;

            int cropW = srcW;
            int cropH = srcH;
            int cropX = 0;
            int cropY = 0;

            if (srcAspect < targetAspect - 0.01f)
            {
                cropH = (int)(srcW / targetAspect);
                if (cropH > srcH) cropH = srcH;
                cropY = (srcH - cropH) / 2;
            }
            else if (srcAspect > targetAspect + 0.01f)
            {
                cropW = (int)(srcH * targetAspect);
                if (cropW > srcW) cropW = srcW;
                cropX = (srcW - cropW) / 2;
            }

            // 2. Digital Zoom
            if (zoom > 1.01f)
            {
                int zoomedW = (int)(cropW / zoom);
                int zoomedH = (int)(cropH / zoom);
                cropX += (cropW - zoomedW) / 2;
                cropY += (cropH - zoomedH) / 2;
                cropW = Math.Max(16, zoomedW);
                cropH = Math.Max(9, zoomedH);
            }

            if (cropX < 0) cropX = 0;
            if (cropY < 0) cropY = 0;
            if (cropX + cropW > srcW) cropW = Math.Max(1, srcW - cropX);
            if (cropY + cropH > srcH) cropH = Math.Max(1, srcH - cropY);

            // 3. Auto Mode Levels
            if (autoMode)
            {
                if (++autoFrameCounter % 6 == 0)
                {
                    AnalyzeAutoLevels(source, cropX, cropY, cropW, cropH);
                }
                brightness += smoothedAutoBrightness;
                contrast += smoothedAutoContrast;
            }

            // 4. Target 16:9 resolution (1080p FHD or 720p HD)
            Bitmap result = new Bitmap(targetW, targetH, PixelFormat.Format32bppArgb);

            using (Graphics g = Graphics.FromImage(result))
            using (Matrix transform = new Matrix())
            {
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.HighSpeed;
                g.CompositingQuality = CompositingQuality.HighSpeed;

                // Mirror / Flip matrix
                if (mirrorH)
                {
                    transform.Translate(targetW, 0);
                    transform.Scale(-1, 1);
                }
                if (flipV)
                {
                    transform.Translate(0, targetH);
                    transform.Scale(1, -1);
                }
                g.Transform = transform;

                Rectangle destRect = new Rectangle(0, 0, targetW, targetH);

                bool hasFilter = (filter != FilterMode.None) ||
                                 (Math.Abs(brightness) >= 0.1f) ||
                                 (Math.Abs(contrast) >= 0.1f) ||
                                 (Math.Abs(saturation) >= 0.1f) ||
                                 (Math.Abs(exposure) >= 0.1f);

                if (hasFilter)
                {
                    using (ImageAttributes attr = CreateCombinedAttributes(filter, brightness, contrast, saturation, exposure))
                    {
                        g.DrawImage(source, destRect, cropX, cropY, cropW, cropH, GraphicsUnit.Pixel, attr);
                    }
                }
                else
                {
                    // Clean, 100% loss-free native color rendering directly from camera!
                    g.DrawImage(source, destRect, cropX, cropY, cropW, cropH, GraphicsUnit.Pixel);
                }

                g.ResetTransform();

                // Mini OBS Overlays (Render all active scene sources in real-time)
                if (scene != null)
                {
                    scene.RenderScene(g, targetW, targetH);
                }
            }

            return result;
        }

        public static GraphicsPath CreateRoundedRectangle(Rectangle rect, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            if (radius <= 0)
            {
                path.AddRectangle(rect);
                return path;
            }
            int d = radius * 2;
            if (d > rect.Width) d = rect.Width;
            if (d > rect.Height) d = rect.Height;

            Rectangle arc = new Rectangle(rect.X, rect.Y, d, d);
            path.AddArc(arc, 180, 90);
            arc.X = rect.Right - d;
            path.AddArc(arc, 270, 90);
            arc.Y = rect.Bottom - d;
            path.AddArc(arc, 0, 90);
            arc.X = rect.X;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static void AnalyzeAutoLevels(Bitmap bmp, int cropX, int cropY, int cropW, int cropH)
        {
            try
            {
                long totalLum = 0;
                int samples = 0;

                int stepX = Math.Max(1, cropW / 9);
                int stepY = Math.Max(1, cropH / 9);

                for (int y = cropY + stepY; y < cropY + cropH - stepY; y += stepY)
                {
                    for (int x = cropX + stepX; x < cropX + cropW - stepX; x += stepX)
                    {
                        if (x < bmp.Width && y < bmp.Height)
                        {
                            Color c = bmp.GetPixel(x, y);
                            int lum = (int)(0.2126f * c.R + 0.7152f * c.G + 0.0722f * c.B);
                            totalLum += lum;
                            samples++;
                        }
                    }
                }

                if (samples > 0)
                {
                    float avgLum = (float)totalLum / samples;
                    float targetBrightness = (125.0f - avgLum) * 0.35f;
                    targetBrightness = Math.Max(-30.0f, Math.Min(35.0f, targetBrightness));
                    float targetContrast = 8.0f;

                    if (Math.Abs(targetBrightness - smoothedAutoBrightness) > 2.5f)
                    {
                        smoothedAutoBrightness = smoothedAutoBrightness * 0.95f + targetBrightness * 0.05f;
                    }
                    smoothedAutoContrast = smoothedAutoContrast * 0.95f + targetContrast * 0.05f;
                }
            }
            catch { }
        }

        private static ImageAttributes CreateCombinedAttributes(
            FilterMode mode,
            float brightness,
            float contrast,
            float saturation,
            float exposure)
        {
            ImageAttributes attr = new ImageAttributes();

            float b = brightness / 255.0f;
            float expFactor = (float)Math.Pow(2.0, exposure / 80.0);
            float contrastFactor = Math.Max(0.1f, (contrast + 100.0f) / 100.0f);
            float c = contrastFactor * expFactor;
            float s = Math.Max(0.0f, (saturation + 100.0f) / 100.0f);

            float t = ((1.0f - contrastFactor) / 2.0f + b) * expFactor;

            float rw = 0.2126f;
            float gw = 0.7152f;
            float bw = 0.0722f;

            float sr = (1.0f - s) * rw;
            float sg = (1.0f - s) * gw;
            float sb = (1.0f - s) * bw;

            float[][] matrixValues = new float[][]
            {
                new float[] { (sr + s) * c, sr * c,        sr * c,        0, 0 },
                new float[] { sg * c,        (sg + s) * c, sg * c,        0, 0 },
                new float[] { sb * c,        sb * c,        (sb + s) * c, 0, 0 },
                new float[] { 0,             0,             0,            1, 0 },
                new float[] { t,             t,             t,            0, 1 }
            };

            // GDI+ row vectors: preset first, then user adjustments.
            float[][] adjustments = matrixValues;
            matrixValues = new float[5][];
            for (int i = 0; i < 5; i++) { matrixValues[i] = new float[5]; matrixValues[i][i] = 1; }
            switch (mode)
            {
                case FilterMode.NightMode:
                    matrixValues[4][0] += 0.14f;
                    matrixValues[4][1] += 0.14f;
                    matrixValues[4][2] += 0.14f;
                    break;

                case FilterMode.BeautyMode:
                    matrixValues[0][0] *= 1.08f;
                    matrixValues[1][1] *= 1.02f;
                    matrixValues[4][0] += 0.04f;
                    matrixValues[4][1] += 0.03f;
                    break;

                case FilterMode.Hdr:
                    matrixValues[0][0] *= 1.18f;
                    matrixValues[1][1] *= 1.18f;
                    matrixValues[2][2] *= 1.18f;
                    break;

                case FilterMode.BlackAndWhite:
                    matrixValues[0] = new float[] { 0.299f, 0.299f, 0.299f, 0, 0 };
                    matrixValues[1] = new float[] { 0.587f, 0.587f, 0.587f, 0, 0 };
                    matrixValues[2] = new float[] { 0.114f, 0.114f, 0.114f, 0, 0 };
                    break;

                case FilterMode.Sepia:
                    matrixValues[0] = new float[] { 0.393f, 0.349f, 0.272f, 0, 0 };
                    matrixValues[1] = new float[] { 0.769f, 0.686f, 0.534f, 0, 0 };
                    matrixValues[2] = new float[] { 0.189f, 0.168f, 0.131f, 0, 0 };
                    break;

                case FilterMode.CinematicTeal:
                    matrixValues[0][0] *= 1.15f;
                    matrixValues[1][1] *= 1.02f;
                    matrixValues[2][2] *= 1.25f;
                    matrixValues[4][0] += 0.05f;
                    matrixValues[4][2] += 0.09f;
                    break;

                case FilterMode.Cyberpunk:
                    matrixValues[0][0] *= 1.28f;
                    matrixValues[1][1] *= 0.88f;
                    matrixValues[2][2] *= 1.38f;
                    matrixValues[4][0] += 0.08f;
                    matrixValues[4][2] += 0.12f;
                    break;

                case FilterMode.WarmStudio:
                    matrixValues[0][0] *= 1.12f;
                    matrixValues[1][1] *= 1.05f;
                    matrixValues[2][2] *= 0.90f;
                    matrixValues[4][0] += 0.06f;
                    matrixValues[4][1] += 0.03f;
                    break;
            }

            float[][] combined = new float[5][];
            for (int i = 0; i < 5; i++) {
                combined[i] = new float[5];
                for (int j = 0; j < 5; j++)
                    for (int k = 0; k < 5; k++) combined[i][j] += matrixValues[i][k] * adjustments[k][j];
            }
            matrixValues = combined;
            ColorMatrix cm = new ColorMatrix(matrixValues);
            attr.SetColorMatrix(cm);
            return attr;
        }

    }
}
