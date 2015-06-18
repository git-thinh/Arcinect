﻿using Microsoft.Kinect;
using Microsoft.Kinect.Fusion;
using NLog;
using System;

namespace Arcinect
{
    /// <summary>
    /// Scanner
    /// </summary>
    sealed class Scanner : Disposable
    {
        /// <summary>
        /// Logger of current class
        /// </summary>
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Singleton instance of Scanner
        /// </summary>
        private static Scanner instance;

        /// <summary>
        /// Active Kinect sensor
        /// </summary>
        private KinectSensor sensor;

        /// <summary>
        /// Reader for depth & color
        /// </summary>
        private MultiSourceFrameReader reader;

        #region Init / Dispose

        Scanner(KinectSensor sensor)
        {
            this.sensor = sensor;
            this.sensor.Open();

            this.reader = sensor.OpenMultiSourceFrameReader(FrameSourceTypes.Depth | FrameSourceTypes.Color);
            this.reader.MultiSourceFrameArrived += Reader_MultiSourceFrameArrived;
        }

        /// <summary>
        /// Factory method of Scanner
        /// </summary>
        /// <returns>return a singleton instance of Scanner</returns>
        public static Scanner Open()
        {
            if (instance == null && IsHardwareCompatible())
            {
                var sensor = KinectSensor.GetDefault();
                if (sensor == null || !sensor.IsAvailable)
                {
                    logger.Error("Kinect sensor is neither connected nor available");
                }
                else
                {
                    logger.Trace("Found a Kinect sensor");

                    instance = new Scanner(sensor);
                }
            }

            return instance;
        }

        /// <summary>
        /// Check to ensure suitable DirectX11 compatible hardware exists before initializing Kinect Fusion
        /// </summary>
        /// <returns></returns>
        private static bool IsHardwareCompatible()
        {
            try
            {
                string deviceDescription;
                string deviceInstancePath;
                int deviceMemoryKB;
                FusionDepthProcessor.GetDeviceInfo(ReconstructionProcessor.Amp, -1, out deviceDescription, out deviceInstancePath, out deviceMemoryKB);

                return true;
            }
            catch (IndexOutOfRangeException ex)
            {
                // Thrown when index is out of range for processor type or there is no DirectX11 capable device installed.
                // As we set -1 (auto-select default) for the DeviceToUse above, this indicates that there is no DirectX11 
                // capable device. The options for users in this case are to either install a DirectX11 capable device 
                // (see documentation for recommended GPUs) or to switch to non-real-time CPU based reconstruction by 
                // changing ProcessorType to ReconstructionProcessor.Cpu

                logger.Error("No DirectX11 device detected, or invalid device index", ex);
                return false;
            }
            catch (DllNotFoundException ex)
            {
                logger.Error("A prerequisite component for Kinect Fusion is missing", ex);
                return false;
            }
            catch (InvalidOperationException ex)
            {
                logger.Error("Unknown exception", ex);
                return false;
            }
        }

        /// <summary>
        /// Close scanner
        /// </summary>
        public void Close()
        {
            logger.Trace("Closing");

            Dispose();
        }

        /// <summary>
        /// Dispose resources
        /// </summary>
        protected override void DisposeManaged()
        {
            if (sensor != null)
            {
                sensor.Close();
                sensor = null;
            }

            instance = null;

            base.DisposeManaged();
        }

        #endregion

        private void Reader_MultiSourceFrameArrived(object sender, MultiSourceFrameArrivedEventArgs e)
        {
        }
    }
}
