using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OllaMascot
{
    /// <summary>
    /// Slices the mascot sprite sheet into one image per animation frame, so playing the animation
    /// or mapping a GPU percentage to a pose at runtime is just an array index. Frames are WPF
    /// images, used by the mascot window and as the dashboard's window icon.
    /// </summary>
    public sealed class MascotIcons
    {
        private const string ResourcePath = "mascot_sheet.png";
        private const int CellSize = 32;

        private readonly List<ImageSource> _frames = new List<ImageSource>();

        public int FrameCount => _frames.Count;

        private MascotIcons() { }

        /// <summary>
        /// Loads the frames, or returns null if the sheet is missing or undecodable — the caller then
        /// leaves the window's default icon in place rather than failing to start.
        /// </summary>
        public static MascotIcons? Load()
        {
            try
            {
                var resource = Application.GetResourceStream(new Uri(ResourcePath, UriKind.Relative));
                if (resource == null)
                {
                    App.Log($"Mascot resource '{ResourcePath}' not found.");
                    return null;
                }

                BitmapSource sheet;
                using (var stream = resource.Stream)
                {
                    var decoder = new PngBitmapDecoder(
                        stream,
                        BitmapCreateOptions.PreservePixelFormat,
                        BitmapCacheOption.OnLoad);
                    sheet = decoder.Frames[0];
                }

                int columns = sheet.PixelWidth / CellSize;
                int rows = sheet.PixelHeight / CellSize;
                if (columns == 0 || rows == 0)
                {
                    App.Log($"Mascot sheet is smaller than one {CellSize}x{CellSize} cell.");
                    return null;
                }

                var icons = new MascotIcons();
                for (int row = 0; row < rows; row++)
                {
                    for (int column = 0; column < columns; column++)
                    {
                        var cell = new CroppedBitmap(
                            sheet,
                            new Int32Rect(column * CellSize, row * CellSize, CellSize, CellSize));

                        // Frozen so the poll callback can assign it without WPF cloning each time
                        cell.Freeze();
                        icons._frames.Add(cell);
                    }
                }

                App.Log($"Loaded {icons.FrameCount} mascot frames ({columns}x{rows} grid).");
                return icons;
            }
            catch (Exception ex)
            {
                App.Log($"Exception loading mascot icons: {ex}");
                return null;
            }
        }

        /// <summary>Maps 0-100% linearly onto the frame range: 0% is the first frame, 100% the last.</summary>
        public int IndexForPercent(double percent)
        {
            double clamped = Math.Clamp(percent, 0.0, 100.0);
            int index = (int)Math.Round(clamped / 100.0 * (_frames.Count - 1));
            return Math.Clamp(index, 0, _frames.Count - 1);
        }

        public ImageSource this[int index] => _frames[Math.Clamp(index, 0, _frames.Count - 1)];
    }
}
