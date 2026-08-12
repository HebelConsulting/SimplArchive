using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.Views;

/// <summary>
/// Pick an image and choose a square crop of it (ADR "User profile photo").
/// </summary>
/// <remarks>
/// Extracted from <see cref="ProfilePhotoDialog"/> so the "Edit profile" dialog can host the crop INLINE rather
/// than opening a second modal on top of itself (#464) — which would also have meant refreshing the photo it
/// displays after that modal closed. The dialog still exists and still works: it is now a window around this
/// control, and the admin path that sets another principal's photo is unchanged.
///
/// The control owns the picture and the crop and nothing else. It does not save, upload, or know whose photo
/// this is — the host asks for <see cref="CroppedPng"/> when its own Save is pressed.
/// </remarks>
public partial class ProfilePhotoEditor : UserControl
{
    private const double MaxDisplay = 340.0;
    private const double MinCrop = 32.0;
    private const int OutputSize = 256;

    private Bitmap? _source;
    private double _displayW, _displayH, _pixelScale;
    private double _cropX, _cropY, _cropSize;

    public ProfilePhotoEditor()
    {
        InitializeComponent();
    }

    /// <summary>Raised when an image is loaded or cleared, so a host can enable its own Save.</summary>
    public event EventHandler? ImageChanged;

    /// <summary>Whether there is a picture to crop — <c>false</c> until one is chosen.</summary>
    public bool HasImage => _source is not null;

    /// <summary>
    /// The chosen square as a 256×256 PNG, or <c>null</c> when no image has been picked. Rendered on demand
    /// rather than kept, because the crop can move right up until the host saves.
    /// </summary>
    public byte[]? CroppedPng()
    {
        if (_source is null)
        {
            return null;
        }

        var srcRect = new Rect(_cropX * _pixelScale, _cropY * _pixelScale, _cropSize * _pixelScale, _cropSize * _pixelScale);
        var target = new RenderTargetBitmap(new PixelSize(OutputSize, OutputSize));
        using (var ctx = target.CreateDrawingContext())
        {
            ctx.DrawImage(_source, srcRect, new Rect(0, 0, OutputSize, OutputSize));
        }

        using var outMs = new MemoryStream();
        target.Save(outMs);
        return outMs.ToArray();
    }

    private async void OnChoose(object? sender, RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
        {
            return;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
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
            Hint.Text = Strings.Get("PpLoadFailed");
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
        Hint.Text = Strings.Get("PpDragHint");
        UpdateCropVisual();
        ImageChanged?.Invoke(this, EventArgs.Empty);
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
}
