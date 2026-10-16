using System;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UsageDock;

internal static class IconAsset
{
    public static Icon WhiteOpenAI()
    {
        using var stream = new MemoryStream(IconBytes());
        using var icon = new Icon(stream, 32, 32); return (Icon)icon.Clone();
    }
    public static byte[] IconBytes()
    {
        var sizes = new[] { 16, 24, 32, 48, 256 }; var frames = new byte[sizes.Length][];
        for (int i = 0; i < sizes.Length; i++)
        {
            var visual = (System.Windows.Controls.Viewbox)Ui.CodexIcon(sizes[i]);
            ((System.Windows.Shapes.Path)visual.Child).Fill = System.Windows.Media.Brushes.White;
            visual.Measure(new System.Windows.Size(sizes[i], sizes[i])); visual.Arrange(new Rect(0, 0, sizes[i], sizes[i])); visual.UpdateLayout();
            var bitmap = new RenderTargetBitmap(sizes[i], sizes[i], 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var data = new MemoryStream(); encoder.Save(data); frames[i] = data.ToArray();
        }
        using var result = new MemoryStream(); using var writer = new BinaryWriter(result);
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
        int offset = 6 + sizes.Length * 16;
        for (int i = 0; i < sizes.Length; i++)
        {
            writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i])); writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i])); writer.Write((byte)0); writer.Write((byte)0);
            writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(frames[i].Length); writer.Write(offset); offset += frames[i].Length;
        }
        foreach (var frame in frames) writer.Write(frame);
        return result.ToArray();
    }
}
