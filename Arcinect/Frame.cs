﻿using Arcinect.Properties;
using Microsoft.Kinect;
using NLog;
using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Arcinect
{
    /// <summary>
    /// Frame Data
    /// </summary>
    sealed class Frame
    {
        /// <summary>
        /// Logger of current class
        /// </summary>
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Image width of color frame
        /// </summary>
        private int colorWidth;

        /// <summary>
        /// Image height of color frame
        /// </summary>
        private int colorHeight;

        /// <summary>
        /// Intermediate storage for the color data received from the camera in 32bit color
        /// </summary>
        private byte[] colorData;

        /// <summary>
        /// Bitmap to display color camera
        /// </summary>
        private WriteableBitmap colorBitmap;

        /// <summary>
        /// Image Width of depth frame
        /// </summary>
        private int depthWidth;

        /// <summary>
        /// Image height of depth frame
        /// </summary>
        private int depthHeight;

        /// <summary>
        /// Intermediate storage for the extended depth data received from the camera in the current frame
        /// </summary>
        private ushort[] depthData;

        /// <summary>
        /// Bitmap to display depth camera
        /// </summary>
        private WriteableBitmap depthBitmap;

        public delegate void DataUpdateEventHandler(object sender);

        public event DataUpdateEventHandler OnDataUpdate;

        public Frame(int colorWidth, int colorHeight, int depthWidth, int depthHeight)
        {
            var defaultSystemDPI = Settings.Default.DefaultSystemDPI;

            this.colorWidth = colorWidth;
            this.colorHeight = colorHeight;
            this.colorData = new byte[colorWidth * colorHeight * sizeof(int)];
            this.colorBitmap = new WriteableBitmap(colorWidth, colorHeight, defaultSystemDPI, defaultSystemDPI, PixelFormats.Bgr32, null);

            this.depthWidth = depthWidth;
            this.depthHeight = depthHeight;
            this.depthData = new ushort[depthWidth * depthHeight];
            this.depthBitmap = new WriteableBitmap(this.depthWidth, this.depthHeight, defaultSystemDPI, defaultSystemDPI, PixelFormats.Gray8, null);
        }

        /// <summary>
        /// Update frame data of color / depth frames
        /// </summary>
        /// <param name="frameReference"></param>

        #region Update from Kinect sensor

        public void Update(MultiSourceFrameReference frameReference)
        {
            var multiSourceFrame = frameReference.AcquireFrame();
            if (multiSourceFrame == null)
            {
                logger.Trace("Abort update since MultiSourceFrame is null");
                return;
            }

            using (var colorFrame = multiSourceFrame.ColorFrameReference.AcquireFrame())
            {
                if (colorFrame == null)
                {
                    logger.Trace("Abort update since ColorFrame is null");
                    return;
                }

                using (var depthFrame = multiSourceFrame.DepthFrameReference.AcquireFrame())
                {
                    if (depthFrame == null)
                    {
                        logger.Trace("Abort update since DepthFrame is null");
                        return;
                    }

                    UpdateColorData(colorFrame);
                    UpdateDepthData(depthFrame);

                    if (OnDataUpdate != null)
                        OnDataUpdate(this);
                }
            }
        }

        private void UpdateColorData(ColorFrame colorFrame)
        {
            var colorFrameDescription = colorFrame.FrameDescription;
            if (this.colorWidth == colorFrameDescription.Width && this.colorHeight == colorFrameDescription.Height)
            {
                var stopwatch = Stopwatch.StartNew();

                colorFrame.CopyConvertedFrameDataToArray(this.colorData, ColorImageFormat.Rgba);

                using (var colorBuffer = colorFrame.LockRawImageBuffer())
                {
                    this.colorBitmap.Lock();

                    // verify data and write the new color frame data to the display bitmap
                    colorFrame.CopyConvertedFrameDataToIntPtr(
                        this.colorBitmap.BackBuffer,
                        (uint)(this.colorWidth * this.colorHeight * sizeof(int)),
                        ColorImageFormat.Bgra);

                    this.colorBitmap.AddDirtyRect(new Int32Rect(0, 0, this.colorBitmap.PixelWidth, this.colorBitmap.PixelHeight));

                    this.colorBitmap.Unlock();
                }

                logger.Trace("ColorFrame updated. Spent: {0}ms", stopwatch.ElapsedMilliseconds);
            }
            else
            {
                logger.Error("Size of ColorFrame does not match. Expected: {0}x{1}, Actual: {2}x{3}",
                    this.colorWidth, this.colorHeight, colorFrameDescription.Width, colorFrameDescription.Height);
            }
        }

        private void UpdateDepthData(DepthFrame depthFrame)
        {
            var depthFrameDescription = depthFrame.FrameDescription;
            if (this.depthWidth == depthFrameDescription.Width && this.depthHeight == depthFrameDescription.Height)
            {
                var stopwatch = Stopwatch.StartNew();

                depthFrame.CopyFrameDataToArray(this.depthData);

                using (var depthBuffer = depthFrame.LockImageBuffer())
                {
                    this.depthBitmap.Lock();

                    this.depthBitmap.WritePixels(
                        new Int32Rect(0, 0, this.depthBitmap.PixelWidth, this.depthBitmap.PixelHeight),
                        Array.ConvertAll(this.depthData, d => MapDepthToByte(d, depthFrame.DepthMinReliableDistance, depthFrame.DepthMaxReliableDistance)),
                        this.depthBitmap.PixelWidth,
                        0);

                    this.depthBitmap.Unlock();
                }

                logger.Trace("DepthFrame updated. Spent: {0}ms", stopwatch.ElapsedMilliseconds);
            }
            else
            {
                logger.Error("Size of DepthFrame does not match. Expected: {0}x{1}, Actual: {2}x{3}",
                    this.depthWidth, this.depthHeight, depthFrameDescription.Width, depthFrameDescription.Height);
            }
        }

        private byte MapDepthToByte(ushort depth, ushort minDepth, ushort maxDepth)
        {
            if (depth >= maxDepth)
                return byte.MaxValue;

            if (depth <= minDepth)
                return byte.MinValue;

            return (byte)Math.Round(((float)depth / (maxDepth - minDepth)) * byte.MaxValue);
        }

        #endregion

        #region Properties

        public int ColorWidth
        {
            get { return this.colorWidth; }
        }

        public int ColorHeight
        {
            get { return this.colorHeight; }
        }

        public byte[] ColorData
        {
            get { return this.colorData; }
        }

        public BitmapSource ColorBitmap
        {
            get { return this.colorBitmap; }
        }

        public int DepthWidth
        {
            get { return this.depthWidth; }
        }

        public int DepthHeight
        {
            get { return this.depthHeight; }
        }

        public ushort[] DepthData
        {
            get { return this.depthData; }
        }

        public BitmapSource DepthBitmap
        {
            get { return this.depthBitmap; }
        }

        #endregion
    }
}