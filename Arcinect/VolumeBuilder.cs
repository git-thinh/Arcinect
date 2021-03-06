﻿using Arcinect.Properties;
using Microsoft.Kinect.Fusion;
using NLog;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Arcinect
{
    class VolumeBuilder : Disposable
    {
        /// <summary>
        /// Logger of current class
        /// </summary>
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Settings of Arcinect
        /// </summary>
        private static readonly Settings settings = Settings.Default;

        /// <summary>
        /// Data source
        /// </summary>
        private Scanner source;

        /// <summary>
        /// UI dispatcher
        /// </summary>
        private Dispatcher dispatcher;

        /// <summary>
        /// The Kinect Fusion volume, enabling color reconstruction
        /// </summary
        private ColorReconstruction volume;

        /// <summary>
        /// Intermediate storage for the depth float data converted from depth image frame
        /// </summary>
        private FusionFloatImageFrame depthFloatFrame;

        /// <summary>
        /// Intermediate storage for the smoothed depth float image frame
        /// </summary>
        private FusionFloatImageFrame smoothDepthFloatFrame;

        /// <summary>
        /// Kinect color re-sampled to be the same size as the depth frame
        /// </summary>
        private FusionColorImageFrame resampledColorFrame;

        /// <summary>
        /// Kinect color mapped into depth frame
        /// </summary>
        private FusionColorImageFrame resampledColorFrameDepthAligned;

        /// <summary>
        /// Per-pixel alignment values
        /// </summary>
        private FusionFloatImageFrame deltaFromReferenceFrame;

        /// <summary>
        /// Shaded surface frame from shading point cloud frame
        /// </summary>
        private FusionColorImageFrame shadedSurfaceFrame;

        /// <summary>
        /// Calculated point cloud frame from image integration
        /// </summary>
        private FusionPointCloudImageFrame raycastPointCloudFrame;
        /// <summary>
        /// Calculated point cloud frame from input depth
        /// </summary>
        private FusionPointCloudImageFrame depthPointCloudFrame;

        /// <summary>
        /// Intermediate storage for the depth float data converted from depth image frame
        /// </summary>
        private FusionFloatImageFrame downsampledDepthFloatFrame;

        /// <summary>
        /// Intermediate storage for the depth float data following smoothing
        /// </summary>
        private FusionFloatImageFrame downsampledSmoothDepthFloatFrame;

        /// <summary>
        /// Calculated point cloud frame from image integration
        /// </summary>
        private FusionPointCloudImageFrame downsampledRaycastPointCloudFrame;

        /// <summary>
        /// Calculated point cloud frame from input depth
        /// </summary>
        private FusionPointCloudImageFrame downsampledDepthPointCloudFrame;

        /// <summary>
        /// Kinect color delta from reference frame data from AlignPointClouds
        /// </summary>
        private FusionColorImageFrame downsampledDeltaFromReferenceFrameColorFrame;

        /// <summary>
        /// Bitmap contains shaded surface frame data for rendering
        /// </summary>
        private WriteableBitmap volumeBitmap;

        /// <summary>
        /// Intermediate storage for the color data received from the camera in 32bit color, re-sampled to depth image size
        /// </summary>
        private int[] resampledColorData;

        /// <summary>
        /// Pixel buffer of depth float frame with pixel data in float format, downsampled for AlignPointClouds
        /// </summary>
        private float[] downsampledDepthData;

        /// <summary>
        /// Intermediate storage for the color data downsampled from depth image size and used in AlignPointClouds
        /// </summary>
        private int[] downsampledDeltaFromReferenceColorPixels;

        /// <summary>
        /// Pixel buffer of delta from reference frame in 32bit color
        /// </summary>
        private int[] deltaFromReferenceFramePixelsArgb;

        /// <summary>
        /// Pixels buffer of shaded surface frame in 32bit color
        /// </summary>
        private int[] shadedSurfaceFramePixelsArgb;

        /// <summary>
        /// Alignment energy from AlignDepthFloatToReconstruction for current frame 
        /// </summary>
        private float alignmentEnergy;

        /// <summary>
        /// The counter for image process successes
        /// </summary>
        private int successfulFrameCount;

        /// <summary>
        /// The counter for frames that have been processed
        /// </summary>
        private int processedFrameCount;

        /// <summary>
        /// The transformation between the world and camera view coordinate system
        /// </summary>
        private Matrix4 worldToCameraTransform;

        /// <summary>
        /// The color mapping of the rendered reconstruction visualization. 
        /// </summary>
        private Matrix4 worldToBGRTransform;

        /// <summary>
        /// The default transformation between the world and volume coordinate system
        /// </summary>
        private Matrix4 defaultWorldToVolumeTransform;

        /// <summary>
        /// The counter for image process failures
        /// </summary>
        private int trackingErrorCount = 0;

        /// <summary>
        /// Set true when tracking fails
        /// </summary>
        private bool trackingFailed;

        /// <summary>
        /// Set true when tracking fails and stays false until integration resumes.
        /// </summary>
        private bool trackingHasFailedPreviously;

        /// <summary>
        /// A camera pose finder to store image frames and poseCount to a database then match the input frames
        /// when tracking is lost to help us recover tracking.
        /// </summary>
        private CameraPoseFinder cameraPoseFinder;

        /// <summary>
        /// Set true when the camera pose finder has stored frames in its database and is able to match camera frames.
        /// </summary>
        private bool cameraPoseFinderAvailable;

        /// <summary>
        /// Worker thread to process color and depth data
        /// </summary>
        private Thread workerThread;

        /// <summary>
        /// Event to stop worker thread
        /// </summary>
        private ManualResetEvent workerThreadStopEvent = new ManualResetEvent(false);

        /// <summary>
        /// Event to notify that data is ready for process
        /// </summary>
        private ManualResetEvent frameDataUpdateEvent = new ManualResetEvent(false);

        public VolumeBuilder(Scanner source, Dispatcher dispatcher)
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            this.source = source;
            this.dispatcher = dispatcher;

            // Set the world-view transform to identity, so the world origin is the initial camera location.
            this.worldToCameraTransform = Matrix4.Identity;

            // Map world X axis to blue channel, Y axis to green channel and Z axis to red channel,
            // normalizing each to the range [0, 1]. We also add a shift of 0.5 to both X,Y channels
            // as the world origin starts located at the center of the front face of the volume,
            // hence we need to map negative x,y world vertex locations to positive color values.
            this.worldToBGRTransform = Matrix4.Identity;
            this.worldToBGRTransform.M11 = settings.VoxelsPerMeter / settings.VoxelsX;
            this.worldToBGRTransform.M22 = settings.VoxelsPerMeter / settings.VoxelsY;
            this.worldToBGRTransform.M33 = settings.VoxelsPerMeter / settings.VoxelsZ;
            this.worldToBGRTransform.M41 = 0.5f;
            this.worldToBGRTransform.M42 = 0.5f;
            this.worldToBGRTransform.M44 = 1.0f;

            var volumeParameters = new ReconstructionParameters(settings.VoxelsPerMeter, settings.VoxelsX, settings.VoxelsY, settings.VoxelsZ);
            this.volume = ColorReconstruction.FusionCreateReconstruction(volumeParameters, ReconstructionProcessor.Amp, -1, this.worldToCameraTransform);

            var depthWidth = this.source.Frame.DepthWidth;
            var depthHeight = this.source.Frame.DepthHeight;
            var depthSize = depthWidth * depthHeight;

            this.depthFloatFrame = new FusionFloatImageFrame(depthWidth, depthHeight);
            this.smoothDepthFloatFrame = new FusionFloatImageFrame(depthWidth, depthHeight);
            this.resampledColorFrame = new FusionColorImageFrame(depthWidth, depthHeight);
            this.resampledColorFrameDepthAligned = new FusionColorImageFrame(depthWidth, depthHeight);
            this.deltaFromReferenceFrame = new FusionFloatImageFrame(depthWidth, depthHeight);
            this.shadedSurfaceFrame = new FusionColorImageFrame(depthWidth, depthHeight);
            this.raycastPointCloudFrame = new FusionPointCloudImageFrame(depthWidth, depthHeight);
            this.depthPointCloudFrame = new FusionPointCloudImageFrame(depthWidth, depthHeight);

            var downsampledDepthWidth = depthWidth / settings.DownsampleFactor;
            var downsampledDepthHeight = depthHeight / settings.DownsampleFactor;
            var downsampledDepthSize = downsampledDepthWidth * downsampledDepthHeight;

            this.downsampledDepthFloatFrame = new FusionFloatImageFrame(downsampledDepthWidth, downsampledDepthHeight);
            this.downsampledSmoothDepthFloatFrame = new FusionFloatImageFrame(downsampledDepthWidth, downsampledDepthHeight);
            this.downsampledRaycastPointCloudFrame = new FusionPointCloudImageFrame(downsampledDepthWidth, downsampledDepthHeight);
            this.downsampledDepthPointCloudFrame = new FusionPointCloudImageFrame(downsampledDepthWidth, downsampledDepthHeight);
            this.downsampledDeltaFromReferenceFrameColorFrame = new FusionColorImageFrame(downsampledDepthWidth, downsampledDepthHeight);

            this.resampledColorData = new int[depthSize];
            this.downsampledDepthData = new float[downsampledDepthSize];
            this.downsampledDeltaFromReferenceColorPixels = new int[downsampledDepthSize];
            this.deltaFromReferenceFramePixelsArgb = new int[depthSize];
            this.shadedSurfaceFramePixelsArgb = new int[this.shadedSurfaceFrame.PixelDataLength];

            this.defaultWorldToVolumeTransform = this.volume.GetCurrentWorldToVolumeTransform();

            this.volumeBitmap = new WriteableBitmap(depthWidth, depthHeight, settings.DefaultSystemDPI, settings.DefaultSystemDPI, PixelFormats.Bgr32, null);

            // Create a camera pose finder with default parameters
            this.cameraPoseFinder = CameraPoseFinder.FusionCreateCameraPoseFinder(CameraPoseFinderParameters.Defaults);

            this.workerThread = new Thread(WorkerThreadProc);
            this.workerThread.Start();
            this.source.Frame.OnDataUpdate += OnFrameDataUpdate;
        }

        protected override void DisposeManaged()
        {
            this.workerThreadStopEvent.Set();
            this.workerThread.Join();

            this.source.Frame.OnDataUpdate -= OnFrameDataUpdate;

            SafeDispose(ref this.volume);

            SafeDispose(ref this.depthFloatFrame);
            SafeDispose(ref this.smoothDepthFloatFrame);
            SafeDispose(ref this.resampledColorFrame);
            SafeDispose(ref this.resampledColorFrameDepthAligned);
            SafeDispose(ref this.deltaFromReferenceFrame);
            SafeDispose(ref this.shadedSurfaceFrame);
            SafeDispose(ref this.raycastPointCloudFrame);
            SafeDispose(ref this.depthPointCloudFrame);
            SafeDispose(ref this.downsampledDepthFloatFrame);
            SafeDispose(ref this.downsampledSmoothDepthFloatFrame);
            SafeDispose(ref this.downsampledRaycastPointCloudFrame);
            SafeDispose(ref this.downsampledDepthPointCloudFrame);
            SafeDispose(ref this.downsampledDeltaFromReferenceFrameColorFrame);

            SafeDispose(ref this.cameraPoseFinder);

            base.DisposeManaged();
        }

        /// <summary>
        /// Reset reconstruction object to initial state
        /// </summary>
        private void ResetReconstruction()
        {
            logger.Trace("Start reset reconstruction");

            // Reset tracking error counter
            this.trackingErrorCount = 0;

            // Set the world-view transform to identity, so the world origin is the initial camera location.
            this.worldToCameraTransform = Matrix4.Identity;

            // Reset volume
            try
            {
                // Translate the reconstruction volume location away from the world origin by an amount equal
                // to the minimum depth threshold. This ensures that some depth signal falls inside the volume.
                // If set false, the default world origin is set to the center of the front face of the 
                // volume, which has the effect of locating the volume directly in front of the initial camera
                // position with the +Z axis into the volume along the initial camera direction of view.
                if (settings.TranslateResetPoseByMinDepthThreshold)
                {
                    var worldToVolumeTransform = this.defaultWorldToVolumeTransform;

                    // Translate the volume in the Z axis by the minDepthClip distance
                    float minDistance = Math.Min(settings.MinDepthClip, settings.MaxDepthClip);
                    worldToVolumeTransform.M43 -= minDistance * settings.VoxelsPerMeter;

                    this.volume.ResetReconstruction(this.worldToCameraTransform, worldToVolumeTransform);
                }
                else
                {
                    this.volume.ResetReconstruction(this.worldToCameraTransform);
                }
            }
            catch (InvalidOperationException error)
            {
                logger.Error("Failed to reset reconstruction. Error: {0}", error);
            }

            logger.Trace("Finish reset reconstruction");
        }

        #region Worker Thread

        private void WorkerThreadProc(object state)
        {
            var events = new WaitHandle[] { this.frameDataUpdateEvent, this.workerThreadStopEvent };

            for (var i = WaitHandle.WaitAny(events); i != 1; i = WaitHandle.WaitAny(events))
            {
                switch (i)
                {
                    case 0:
                        this.frameDataUpdateEvent.Reset();
                        Process();
                        break;

                    default:
                        throw new ArgumentOutOfRangeException("Unexpected event index");
                }
            }
        }

        #endregion

        #region Process frames

        private void OnFrameDataUpdate(object sender)
        {
            this.frameDataUpdateEvent.Set();
        }

        /// <summary>
        /// The main process function
        /// </summary>
        private void Process()
        {
            try
            {
                var stopwatch = Stopwatch.StartNew();

                // Check if camera pose finder is available
                this.cameraPoseFinderAvailable = this.cameraPoseFinder.GetStoredPoseCount() > 0;

                // Convert depth to float and render depth frame
                this.volume.DepthToDepthFloatFrame(this.source.Frame.DepthData, this.depthFloatFrame, settings.MinDepthClip, settings.MaxDepthClip, false);

                // Track camera pose
                TrackCamera();

                // Only continue if we do not have tracking errors
                if (this.trackingErrorCount == 0)
                {
                    // Integrate depth
                    IntegrateData();

                    // Raycast and render
                    UpdateVolumeData();

                    // Update camera pose finder, adding key frames to the database
                    if (!this.trackingHasFailedPreviously
                        && this.successfulFrameCount > settings.MinSuccessfulTrackingFramesForCameraPoseFinder
                        && this.processedFrameCount % settings.CameraPoseFinderProcessFrameCalculationInterval == 0)
                    {
                        UpdateCameraPoseFinder();
                    }
                }

                logger.Trace("Volume data processed. Spent {0}ms", stopwatch.ElapsedMilliseconds);
            }
            catch (InvalidOperationException ex)
            {
                logger.Error("Failed to process. Error: {0}", ex);
            }
        }

        /// <summary>
        /// Track the camera pose
        /// </summary>
        private void TrackCamera()
        {
            bool calculateDeltaFrame = this.processedFrameCount % settings.DeltaFrameCalculationInterval == 0;

            // Get updated camera transform from image alignment
            var calculatedCameraPos = this.worldToCameraTransform;

            // Track using AlignPointClouds
            var trackingSucceeded = TrackCameraAlignPointClouds(ref calculateDeltaFrame, ref calculatedCameraPos);

            if (!trackingSucceeded && this.successfulFrameCount != 0)
            {
                this.SetTrackingFailed();

                if (this.cameraPoseFinderAvailable)
                {
                    // Here we try to find the correct camera pose, to re-localize camera tracking.
                    // We can call either the version using AlignDepthFloatToReconstruction or the
                    // version using AlignPointClouds, which typically has a higher success rate.
                    // trackingSucceeded = this.FindCameraPoseAlignDepthFloatToReconstruction();
                    trackingSucceeded = FindCameraPoseAlignPointClouds();
                }
            }
            else
            {
                SetTrackingSucceeded();

                this.worldToCameraTransform = calculatedCameraPos;
            }

            if (trackingSucceeded)
            {
                // Increase processed frame counter
                this.processedFrameCount++;
            }
        }

        /// <summary>
        /// Track camera pose using AlignPointClouds
        /// </summary>
        /// <param name="calculateDeltaFrame">A flag to indicate it is time to calculate the delta frame.</param>
        /// <param name="calculatedCameraPose">The calculated camera pose.</param>
        /// <returns>Returns true if tracking succeeded, false otherwise.</returns>
        private bool TrackCameraAlignPointClouds(ref bool calculateDeltaFrame, ref Matrix4 calculatedCameraPose)
        {
            var trackingSucceeded = false;

            DownsampleDepthFrameNearestNeighbor();

            // Smooth the depth frame
            this.volume.SmoothDepthFloatFrame(this.downsampledDepthFloatFrame, this.downsampledSmoothDepthFloatFrame, settings.SmoothingKernelWidth, settings.SmoothingDistanceThreshold);

            // Calculate point cloud from the smoothed frame
            FusionDepthProcessor.DepthFloatFrameToPointCloud(this.downsampledSmoothDepthFloatFrame, this.downsampledDepthPointCloudFrame);

            // Get the saved pose view by raycasting the volume from the current camera pose
            this.volume.CalculatePointCloud(this.downsampledRaycastPointCloudFrame, calculatedCameraPose);

            var initialPose = calculatedCameraPose;

            // Note that here we only calculate the deltaFromReferenceFrame every 
            // DeltaFrameCalculationInterval frames to reduce computation time
            if (calculateDeltaFrame)
            {
                trackingSucceeded = FusionDepthProcessor.AlignPointClouds(
                    this.downsampledRaycastPointCloudFrame,
                    this.downsampledDepthPointCloudFrame,
                    FusionDepthProcessor.DefaultAlignIterationCount,
                    this.downsampledDeltaFromReferenceFrameColorFrame,
                    ref calculatedCameraPose);

                UpsampleColorDeltasFrameNearestNeighbor();

                // Set calculateDeltaFrame to false as we are rendering it here
                calculateDeltaFrame = false;
            }
            else
            {
                // Don't bother getting the residual delta from reference frame to cut computation time
                trackingSucceeded = FusionDepthProcessor.AlignPointClouds(
                    this.downsampledRaycastPointCloudFrame,
                    this.downsampledDepthPointCloudFrame,
                    FusionDepthProcessor.DefaultAlignIterationCount,
                    null,
                    ref calculatedCameraPose);
            }

            if (trackingSucceeded)
            {
                trackingSucceeded = MathUtils.CheckTransformChange(
                    initialPose,
                    calculatedCameraPose,
                    settings.MaxTranslationDeltaAlignPointClouds,
                    settings.MaxRotationDeltaAlignPointClouds);
            }

            return trackingSucceeded;
        }

        /// <summary>
        /// Downsample depth pixels with nearest neighbor
        /// </summary>
        /// <param name="dest">The destination depth image.</param>
        /// <param name="factor">The downsample factor (2=x/2,y/2, 4=x/4,y/4, 8=x/8,y/8, 16=x/16,y/16).</param>
        private void DownsampleDepthFrameNearestNeighbor()
        {
            var width = this.source.Frame.DepthWidth;
            var height = this.source.Frame.DepthHeight;
            var downsampledWidth = this.source.Frame.DepthWidth / settings.DownsampleFactor;
            var downsampledHeight = this.source.Frame.DepthHeight / settings.DownsampleFactor;

            for (var y = 0; y < downsampledHeight; y++)
            {
                int flippedDestIndex = (y * downsampledWidth) + (downsampledWidth - 1);
                int sourceIndex = y * width * settings.DownsampleFactor;

                for (int x = 0; x < downsampledWidth; ++x, --flippedDestIndex, sourceIndex += settings.DownsampleFactor)
                {
                    // Copy depth value
                    this.downsampledDepthData[flippedDestIndex] = (float)this.source.Frame.DepthData[sourceIndex] * 0.001f;
                }
            }


            this.downsampledDepthFloatFrame.CopyPixelDataFrom(this.downsampledDepthData);
        }

        /// <summary>
        /// Up sample color delta from reference frame with nearest neighbor - replicates pixels
        /// </summary>
        /// <param name="factor">The up sample factor (2=x*2,y*2, 4=x*4,y*4, 8=x*8,y*8, 16=x*16,y*16).</param>
        private void UpsampleColorDeltasFrameNearestNeighbor()
        {
            var width = this.source.Frame.DepthWidth;
            var height = this.source.Frame.DepthHeight;
            var downsampledWidth = this.source.Frame.DepthWidth / settings.DownsampleFactor;
            var downsampledHeight = this.source.Frame.DepthHeight / settings.DownsampleFactor;
            var factor = settings.DownsampleFactor;

            this.downsampledDeltaFromReferenceFrameColorFrame.CopyPixelDataTo(this.downsampledDeltaFromReferenceColorPixels);

            for (var y = 0; y < downsampledHeight; y++)
            {
                var destIndex = y * width * factor;
                var sourceColorIndex = y * downsampledWidth;

                for (var x = 0; x < downsampledWidth; ++x, ++sourceColorIndex)
                {
                    var color = this.source.Frame.DepthData[sourceColorIndex];

                    // Replicate pixels horizontally
                    for (var colFactorIndex = 0; colFactorIndex < factor; ++colFactorIndex, ++destIndex)
                    {
                        // Replicate pixels vertically
                        for (var rowFactorIndex = 0; rowFactorIndex < factor; ++rowFactorIndex)
                        {
                            // Copy color pixel
                            this.deltaFromReferenceFramePixelsArgb[destIndex + (rowFactorIndex * width)] = color;
                        }
                    }
                }
            }

            var sizeOfInt = sizeof(int);
            var rowByteSize = downsampledHeight * sizeOfInt;

            // Duplicate the remaining rows with memcpy
            for (var y = 0; y < downsampledHeight; ++y)
            {
                // iterate only for the smaller number of rows
                var srcRowIndex = width * factor * y;

                // Duplicate lines
                for (var r = 1; r < factor; ++r)
                {
                    int index = width * ((y * factor) + r);

                    Buffer.BlockCopy(
                        this.deltaFromReferenceFramePixelsArgb, srcRowIndex * sizeOfInt, this.deltaFromReferenceFramePixelsArgb, index * sizeOfInt, rowByteSize);
                }
            }
        }

        /// <summary>
        /// Set variables if camera tracking succeeded
        /// </summary>
        private void SetTrackingFailed()
        {
            // Clear successful frame count and increment the track error count
            this.trackingFailed = true;
            this.trackingHasFailedPreviously = true;
            this.trackingErrorCount++;
            this.successfulFrameCount = 0;
        }

        /// <summary>
        /// Set variables if camera tracking succeeded
        /// </summary>
        private void SetTrackingSucceeded()
        {
            // Clear track error count and increment the successful frame count
            this.trackingFailed = false;
            this.trackingErrorCount = 0;
            this.successfulFrameCount++;
        }

        /// <summary>
        /// Reset tracking variables
        /// </summary>
        private void ResetTracking()
        {
            this.trackingFailed = false;
            this.trackingHasFailedPreviously = false;
            this.trackingErrorCount = 0;
            this.successfulFrameCount = 0;

            this.cameraPoseFinder.ResetCameraPoseFinder();
        }

        /// <summary>
        /// Perform camera pose finding when tracking is lost using AlignPointClouds.
        /// This is typically more successful than FindCameraPoseAlignDepthFloatToReconstruction.
        /// </summary>
        /// <returns>Returns true if a valid camera pose was found, otherwise false.</returns>
        private bool FindCameraPoseAlignPointClouds()
        {
            if (!this.cameraPoseFinderAvailable)
            {
                return false;
            }

            ProcessColorForCameraPoseFinder();

            var matchCandidates = this.cameraPoseFinder.FindCameraPose(
                this.depthFloatFrame,
                this.resampledColorFrame);

            if (matchCandidates == null)
            {
                return false;
            }

            var poseCount = matchCandidates.GetPoseCount();
            var minDistance = matchCandidates.CalculateMinimumDistance();

            if (poseCount == 0 || minDistance >= settings.CameraPoseFinderDistanceThresholdReject)
            {
                return false;
            }

            // Smooth the depth frame
            this.volume.SmoothDepthFloatFrame(this.depthFloatFrame, this.smoothDepthFloatFrame, settings.SmoothingKernelWidth, settings.SmoothingDistanceThreshold);

            // Calculate point cloud from the smoothed frame
            FusionDepthProcessor.DepthFloatFrameToPointCloud(this.smoothDepthFloatFrame, this.depthPointCloudFrame);

            var smallestEnergy = double.MaxValue;
            var smallestEnergyNeighborIndex = -1;

            var bestNeighborIndex = -1;
            var bestNeighborCameraPose = Matrix4.Identity;

            var bestNeighborAlignmentEnergy = settings.MaxAlignPointCloudsEnergyForSuccess;

            // Run alignment with best matched poseCount (i.e. k nearest neighbors (kNN))
            var maxTests = Math.Min(settings.MaxCameraPoseFinderPoseTests, poseCount);

            var neighbors = matchCandidates.GetMatchPoses();

            for (var n = 0; n < maxTests; n++)
            {
                // Run the camera tracking algorithm with the volume
                // this uses the raycast frame and pose to find a valid camera pose by matching the raycast against the input point cloud
                var poseProposal = neighbors[n];

                // Get the saved pose view by raycasting the volume
                this.volume.CalculatePointCloud(this.raycastPointCloudFrame, poseProposal);

                var success = this.volume.AlignPointClouds(
                    this.raycastPointCloudFrame,
                    this.depthPointCloudFrame,
                    FusionDepthProcessor.DefaultAlignIterationCount,
                    this.resampledColorFrame,
                    out this.alignmentEnergy,
                    ref poseProposal);

                var relocSuccess = success && this.alignmentEnergy < bestNeighborAlignmentEnergy && this.alignmentEnergy > settings.MinAlignPointCloudsEnergyForSuccess;

                if (relocSuccess)
                {
                    bestNeighborAlignmentEnergy = this.alignmentEnergy;
                    bestNeighborIndex = n;

                    // This is after tracking succeeds, so should be a more accurate pose to store...
                    bestNeighborCameraPose = poseProposal;

                    // Update the delta image
                    this.resampledColorFrame.CopyPixelDataTo(this.deltaFromReferenceFramePixelsArgb);
                }

                // Find smallest energy neighbor independent of tracking success
                if (this.alignmentEnergy < smallestEnergy)
                {
                    smallestEnergy = this.alignmentEnergy;
                    smallestEnergyNeighborIndex = n;
                }
            }

            matchCandidates.Dispose();

            // Use the neighbor with the smallest residual alignment energy
            // At the cost of additional processing we could also use kNN+Mean camera pose finding here
            // by calculating the mean pose of the best n matched poses and also testing this to see if the 
            // residual alignment energy is less than with kNN.
            if (bestNeighborIndex > -1)
            {
                this.worldToCameraTransform = bestNeighborCameraPose;
                this.SetReferenceFrame(this.worldToCameraTransform);

                // Tracking succeeded!
                this.SetTrackingSucceeded();

                return true;
            }
            else
            {
                this.worldToCameraTransform = neighbors[smallestEnergyNeighborIndex];
                this.SetReferenceFrame(this.worldToCameraTransform);

                // Camera pose finding failed - return the tracking failed error code
                this.SetTrackingFailed();

                return false;
            }
        }

        /// <summary>
        /// Process input color image to make it equal in size to the depth image
        /// </summary>
        private void ProcessColorForCameraPoseFinder()
        {
            var rawDepthHeight = this.source.Frame.DepthWidth / 4 * 3;
            var factor = this.source.Frame.ColorWidth / rawDepthHeight;
            var filledZeroMargin = (this.source.Frame.DepthHeight - rawDepthHeight) / 2;

            for (var y = filledZeroMargin; y < this.source.Frame.DepthHeight - filledZeroMargin; y++)
            {
                var destIndex = y * this.source.Frame.DepthWidth;

                for (var x = 0; x < this.source.Frame.DepthWidth; ++x, ++destIndex)
                {
                    var srcX = (int)(x * factor);
                    var srcY = (int)(y * factor);
                    var sourceColorIndex = (srcY * this.source.Frame.ColorWidth) + srcX;

                    this.resampledColorData[destIndex] = this.source.Frame.ColorData[destIndex];
                }
            }

            this.resampledColorFrame.CopyPixelDataFrom(this.resampledColorData);
        }

        /// <summary>
        /// This is used to set the reference frame.
        /// </summary>
        /// <param name="pose">The pose to use.</param>
        private void SetReferenceFrame(Matrix4 pose)
        {
            // Get the saved pose view by raycasting the volume
            this.volume.CalculatePointCloudAndDepth(this.raycastPointCloudFrame, this.smoothDepthFloatFrame, null, pose);

            // Set this as the reference frame for the next call to AlignDepthFloatToReconstruction
            this.volume.SetAlignDepthFloatToReconstructionReferenceFrame(this.smoothDepthFloatFrame);
        }

        /// <summary>
        /// Perform volume depth data integration
        /// </summary>
        /// <returns>Returns true if a color frame is available for further processing, false otherwise.</returns>
        private void IntegrateData()
        {
            // Don't integrate depth data into the volume if:
            // 1) tracking failed
            // 2) camera pose finder is off and we have paused capture
            // 3) camera pose finder is on and we are still under the m_cMinSuccessfulTrackingFramesForCameraPoseFinderAfterFailure
            //    number of successful frames count.
            var integrateData = !this.trackingFailed
                    && (!this.cameraPoseFinderAvailable
                    || (this.cameraPoseFinderAvailable
                    && !(this.trackingHasFailedPreviously
                    && this.successfulFrameCount < settings.MinSuccessfulTrackingFramesForCameraPoseFinderAfterFailure)));

            // Integrate the frame to volume
            if (integrateData)
            {
                // Reset this flag as we are now integrating data again
                this.trackingHasFailedPreviously = false;

                // Just integrate depth
                this.volume.IntegrateFrame(
                    this.depthFloatFrame,
                    settings.IntegrationWeight,
                    this.worldToCameraTransform);
            }
        }

        /// <summary>
        /// Update the camera pose finder data.
        /// </summary
        private void UpdateCameraPoseFinder()
        {
            ProcessColorForCameraPoseFinder();

            bool poseHistoryTrimmed;
            bool addedPose;

            // This function will add the pose to the camera pose finding database when the input frame's minimum
            // distance to the existing database is equal to or above CameraPoseFinderDistanceThresholdAccept 
            // (i.e. indicating that the input has become dis-similar to the existing database and a new frame 
            // should be captured). Note that the color and depth frames must be the same size, however, the 
            // horizontal mirroring setting does not have to be consistent between depth and color. It does have
            // to be consistent between camera pose finder database creation and calling FindCameraPose though,
            // hence we always reset both the reconstruction and database when changing the mirror depth setting.
            this.cameraPoseFinder.ProcessFrame(this.depthFloatFrame, this.resampledColorFrame, this.worldToCameraTransform, settings.CameraPoseFinderDistanceThresholdAccept,
                    out addedPose, out poseHistoryTrimmed);
        }

        /// <summary>
        /// Update the volume data.
        /// </summary
        private void UpdateVolumeData()
        {
            // For capture color
            //this.volume.CalculatePointCloud(this.raycastPointCloudFrame, this.shadedSurfaceFrame, this.worldToCameraTransform);

            this.volume.CalculatePointCloud(this.raycastPointCloudFrame, this.worldToCameraTransform);

            // Shade point cloud frame for rendering
            FusionDepthProcessor.ShadePointCloud(
                    this.raycastPointCloudFrame,
                    this.worldToCameraTransform,
                    this.worldToBGRTransform,
                    this.shadedSurfaceFrame, null);

            // Copy pixel data to pixel buffer
            this.shadedSurfaceFrame.CopyPixelDataTo(this.shadedSurfaceFramePixelsArgb);

            this.dispatcher.BeginInvoke((Action)RenderVolumeBitmap);
        }

        private void RenderVolumeBitmap()
        {
            if (!this.Disposed)
            {
                this.volumeBitmap.Lock();

                // Write pixels to bitmap
                this.volumeBitmap.WritePixels(
                        new Int32Rect(0, 0, this.shadedSurfaceFrame.Width, this.shadedSurfaceFrame.Height),
                        this.shadedSurfaceFramePixelsArgb,
                        this.volumeBitmap.PixelWidth * sizeof(int),
                        0);

                this.volumeBitmap.Unlock();
            }
        }

        #endregion

        public ColorMesh CreateMesh()
        {
            return this.volume.CalculateMesh(1);
        }

        #region Properties

        public BitmapSource VolumeBitmap
        {
            get { return this.volumeBitmap; }
        }

        #endregion
    }
}