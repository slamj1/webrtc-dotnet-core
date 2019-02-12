﻿using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net.WebSockets;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using SharpDX;
using SharpDX.Mathematics.Interop;

namespace WonderMediaProductions.WebRtc
{
    public static class RtcRenderingServer
    {
        const int VideoFrameWidth = 1920;
        const int VideoFrameHeight = 1080;
        const int VideoFrameRate = 60;

        private static IRenderer CreateRenderer(ObservableVideoTrack videoTrack, ILogger logger)
        {
            bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

            // TODO: Add support for OpenGL, and test it.
            // Maybe use https://github.com/mellinoe/veldrid
            return isWindows
                ? (IRenderer)new D3D11Renderer(videoTrack,
                    new GraphicsD3D11.RendererOptions
                    {
                        VideoFrameWidth = VideoFrameWidth,
                        VideoFrameHeight = VideoFrameHeight,
                    })
                : new ImageSharpRenderer(VideoFrameWidth, VideoFrameHeight, videoTrack);
        }

        class SharedState : IDisposable
        {
            public readonly ILogger Logger;

            public readonly ObservableVideoTrack VideoTrack;

            public readonly ConcurrentQueue<MouseMessage> MouseMessageQueue = new ConcurrentQueue<MouseMessage>();

            public SharedState(ObservableVideoTrack videoTrack, ILogger logger)
            {
                VideoTrack = videoTrack;
                Logger = logger;
            }

            public void Dispose()
            {
                VideoTrack?.Dispose();
            }
        }

        private static void VideoRenderer(object parameter)
        {
            var state = (SharedState)parameter;
            var videoTrack = state.VideoTrack;
            var peerConnection = videoTrack.PeerConnection;
            var logger = state.Logger;

            try
            {

                TimeSpan startTime = TimeSpan.Zero;
                TimeSpan nextFrameTime = TimeSpan.Zero;
                TimeSpan previousTime = TimeSpan.Zero;

                long nextFrameIndex = 0;

                var sw = new Stopwatch();

                // TODO: Stop using polling, use a Win32 WaitableTimer and GetSystemTimePreciseAsFileTime as I did in the 3D Streaming Toolkit fork.
                using (var renderer = CreateRenderer(videoTrack, logger))
                {
                    while (Thread.CurrentThread.IsAlive && !peerConnection.IsDisposed)
                    {
                        if (peerConnection.SignalingState == SignalingState.Stable)
                        {
                            var currentTime = PeerConnection.GetRealtimeClockTimeInMicroseconds();

                            if (currentTime == previousTime)
                            {
                                Thread.Sleep(0);
                                continue;
                            }

                            previousTime = currentTime;

                            if (startTime == TimeSpan.Zero)
                            {
                                startTime = currentTime;
                                sw.Start();
                            }

                            var sleep = nextFrameTime - currentTime;

                            MouseMessage lastMouseMessage = null;

                            while (state.MouseMessageQueue.TryDequeue(out var mouseMessage))
                            {
                                lastMouseMessage = mouseMessage;
                            }

                            if (lastMouseMessage != null)
                            {
                                renderer.BallPosition = lastMouseMessage.Kind != MouseEventKind.Up
                                    ? lastMouseMessage.Pos
                                    : (RawVector2?)null;

                                var elapsedTime = currentTime - startTime;
                                var frameIndex = (int)(elapsedTime.Ticks * videoTrack.FrameRate / TimeSpan.TicksPerSecond);
                                renderer.SendFrame(elapsedTime, frameIndex);
                            }
                            else if (sleep.Ticks <= 0)
                            {
                                // Console.Write($"{sw.ElapsedMilliseconds:D06}\t");
                                sw.Restart();

                                var elapsedTime = currentTime - startTime;
                                var frameIndex =
                                    (int)(elapsedTime.Ticks * videoTrack.FrameRate / TimeSpan.TicksPerSecond);

                                var skippedFrameCount = frameIndex - nextFrameIndex;
                                Debug.Assert(skippedFrameCount >= 0);

                                if (skippedFrameCount >= 1)
                                {
                                    logger.LogWarning($"Skipped {skippedFrameCount} frames!");
                                }

                                renderer.SendFrame(elapsedTime, frameIndex);

                                nextFrameIndex = frameIndex + 1;

                                // TODO: Use Math.DivRem and take remainder into account?
                                // TODO: Should get feedback from connected peer about frame-rate and resolution.
                                nextFrameTime =
                                    startTime + TimeSpan.FromTicks(
                                        nextFrameIndex * TimeSpan.TicksPerSecond / videoTrack.FrameRate);
                            }
                            else
                            {
                                // TODO: Use Win32 waitable timers, or expose webrtc's high-precision (?) TaskQueue
                                Thread.Sleep(0);
                            }
                        }
                        else
                        {
                            // Wait until peer connection is stable before sending frames.
                            Thread.Sleep(500);
                        }
                    }
                }
            }

            catch (ThreadInterruptedException)
            {
            }

            catch (Exception ex)
            {
                logger.LogError(ex, "Error in RtcRendererServer thread");
            }
        }

        public static async Task Run(WebSocket ws, CancellationToken cancellation, ILogger logger)
        {
            var renderThread = new Thread(VideoRenderer);

            // PeerConnection.Configure(options => options.IsSingleThreaded = true);

            using (var pc = new ObservablePeerConnection(new PeerConnectionOptions
            {
                Name = "WebRTC Server"
            }))
            using (pc.LocalIceCandidateStream.Subscribe(ice => ws.SendJsonAsync("ice", ice, cancellation)))
            using (pc.LocalSessionDescriptionStream.Subscribe(sd => ws.SendJsonAsync("sdp", sd, cancellation)))
            using (var videoTrack = new ObservableVideoTrack(pc,
                VideoEncoderOptions.OptimizedFor(VideoFrameWidth, VideoFrameHeight, VideoFrameRate)))
            {
                var msgStream = Observable.Never<DataMessage>();
                var iceStream = new Subject<IceCandidate>();
                var sdpStream = new Subject<SessionDescription>();

                var sharedState = new SharedState(videoTrack, logger);
                renderThread.Start(sharedState);

                pc.Connect(msgStream, sdpStream, iceStream);

                pc.AddDataChannel(new DataChannelOptions());

                pc.CreateOffer();

                var reader = new WebSocketReader(ws, cancellation);

                while (reader.CanRead && !cancellation.IsCancellationRequested)
                {
                    var message = await reader.ReadJsonAsync();
                    if (message == null)
                        break;

                    var payload = message["payload"];

                    if (payload.Any())
                    {
                        switch (message["action"].Value<string>())
                        {
                            case "ice":
                            {
                                iceStream.OnNext(new IceCandidate(payload));
                                break;
                            }

                            case "sdp":
                            {
                                sdpStream.OnNext(new SessionDescription(payload));
                                break;
                            }

                            case "pos":
                            {
                                sharedState.MouseMessageQueue.Enqueue(new MouseMessage(payload));
                                break;
                            }
                        }
                    }
                }

                logger.LogInformation(ws.CloseStatus.HasValue ? "Websocket was closed by client" : "Application is stopping...");

                renderThread.Interrupt();
                renderThread.Join();
            }
        }
    }
}