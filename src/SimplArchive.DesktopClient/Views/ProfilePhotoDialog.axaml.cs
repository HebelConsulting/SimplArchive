using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace SimplArchive.DesktopClient.Views;

// Profile-photo picker with a draggable/resizable square crop (ADR "User profile photo"). Pick an image,
// position the square, Save — the selected square is rendered to a 256×256 PNG. ShowDialog<byte[]?> returns
// the PNG bytes, or null if cancelled. The caller uploads them.
public partial class ProfilePhotoDialog : Window
{
    private const double MaxDisplay = 340.0;
    private const double MinCrop = 32.0;
    private const int OutputSize = 256;

    private Bitmap? _source;
    private double _displayW, _displayH, _pixelScale;
    private double _cropX, _cropY, _cropSize;

    public ProfilePhotoDialog()
    {
        InitializeComponent();
    }

    private async void OnChoose(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.bmp", "*.webp"] }],
        });
        if (files.Count == 0)
        {
            return;
        }

        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            ms.Position = 0;
            _source = new Bitmap(ms);
            SetupDisplay();
        }
        catch (Exception)
        {
            Hint.Text = "That image could not be loaded.";
        }
    }

    private void SetupDisplay()
    {
        if (_source is null)
        {
            return;
        }

        var scale = Math.Min(1.0, MaxDisplay / Math.Max(_source.PixelSize.Width, _source.PixelSize.Height));
        _displayW = _source.PixelSize.Width * scale;
        _displayH = _source.PixelSize.Height * scale;
        _pixelScale = _source.PixelSize.Width / _displayW;

        Preview.Source = _source;
        Preview.Width = _displayW;
        Preview.Height = _displayH;
        Host.Width = _displayW;
        Host.Height = _displayH;

        _cropSize = Math.Min(_displayW, _displayH) * 0.8;
        _cropX = (_displayW - _cropSize) / 2;
        _cropY = (_displayH - _cropSize) / 2;

        MoveThumb.IsVisible = true;
        ResizeThumb.IsVisible = true;
        SaveButton.IsEnabled = true;
        Hint.Text = "Drag to move, or drag the white dot to resize the square.";
        UpdateCropVisual();
    }

    private void UpdateCropVisual()
    {
        MoveThumb.Width = _cropSize;
        MoveThumb.Height = _cropSize;
        Canvas.SetLeft(MoveThumb, _cropX);
        Canvas.SetTop(MoveThumb, _cropY);
        Canvas.SetLeft(ResizeThumb, _cropX + _cropSize - 8);
        Canvas.SetTop(ResizeThumb, _cropY + _cropSize - 8);
    }

    private void OnMove(object? sender, VectorEventArgs e)
    {
        _cropX = Math.Clamp(_cropX + e.Vector.X, 0, _displayW - _cropSize);
        _cropY = Math.Clamp(_cropY + e.Vector.Y, 0, _displayH - _cropSize);
        UpdateCropVisual();
    }

    private void OnResize(object? sender, VectorEventArgs e)
    {
        var delta = Math.Max(e.Vector.X, e.Vector.Y);
        var max = Math.Min(_displayW - _cropX, _displayH - _cropY);
        _cropSize = Math.Clamp(_cropSize + delta, MinCrop, max);
        UpdateCropVisual();
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_source is null)
        {
            return;
        }

        var srcRect = new Rect(_cropX * _pixelScale, _cropY * _pixelScale, _cropSize * _pixelScale, _cropSize * _pixelScale);
        var target = new RenderTargetBitmap(new PixelSize(OutputSize, OutputSize));
        using (var ctx = target.CreateDrawingContext())
        {
            ctx.DrawImage(_source, srcRect, new Rect(0, 0, OutputSize, OutputSize));
        }

        using var outMs = new MemoryStream();
        target.Save(outMs);
        Close(outMs.ToArray());
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
