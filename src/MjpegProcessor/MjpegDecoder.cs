﻿using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace DotNetProjects.MjpegProcessor
{
    public class MjpegDecoder
    {
        // magic 2 byte header for JPEG images
        private readonly byte[] JpegHeader = { 0xff, 0xd8 };

        // pull down 1024 bytes at a time
        private const int ChunkSize = 1024;

        // used to cancel reading the stream
        private bool _streamActive;

        // current encoded JPEG image
        public byte[] CurrentFrame { get; private set; }

        // used to marshal back to UI thread
        private SynchronizationContext _context;

        // event to get the buffer above handed to you
        public event EventHandler<FrameReadyEventArgs> FrameReady;
        public event EventHandler<ErrorEventArgs> Error;

        public MjpegDecoder()
        {
            _context = SynchronizationContext.Current;
        }

        public void ParseStream(Uri uri)
        {
            ParseStream(uri, null, null);
        }

        public void ParseStream(Uri uri, string username, string password)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(uri);
            if (!string.IsNullOrEmpty(username) || !string.IsNullOrEmpty(password))
                request.Credentials = new NetworkCredential(username, password);

            // asynchronously get a response
            request.BeginGetResponse(OnGetResponse, request);
        }

        public void StopStream()
        {
            _streamActive = false;
        }

        private void OnGetResponse(IAsyncResult asyncResult)
        {
            byte[] imageBuffer = new byte[1024 * 1024];

            // get the response
            var req = (HttpWebRequest)asyncResult.AsyncState;

            HttpWebResponse resp = null;
            try
            {
                resp = (HttpWebResponse) req.EndGetResponse(asyncResult);

                // find our magic boundary value
                string contentType = resp.Headers["Content-Type"];
                if (!string.IsNullOrEmpty(contentType) && !contentType.Contains("="))
                    throw new Exception(
                        "Invalid content-type header.  The camera is likely not returning a proper MJPEG stream.");

                string boundary = resp.Headers["Content-Type"].Split('=')[1].Replace("\"", "");
                byte[] boundaryBytes = Encoding.UTF8.GetBytes(boundary.StartsWith("--") ? boundary : "--" + boundary);

                Stream s = resp.GetResponseStream();
                var br = new BinaryReader(s);

                _streamActive = true;

                byte[] buff = br.ReadBytes(ChunkSize);

                while (_streamActive)
                {
                    // find the JPEG header
                    int imageStart = buff.Find(JpegHeader);

                    if (imageStart != -1)
                    {
                        // copy the start of the JPEG image to the imageBuffer
                        int size = buff.Length - imageStart;
                        Array.Copy(buff, imageStart, imageBuffer, 0, size);

                        while (true)
                        {
                            buff = br.ReadBytes(ChunkSize);

                            // find the boundary text
                            int imageEnd = buff.Find(boundaryBytes);
                            if (imageEnd != -1)
                            {
                                // copy the remainder of the JPEG to the imageBuffer
                                Array.Copy(buff, 0, imageBuffer, size, imageEnd);
                                size += imageEnd;

                                byte[] frame = new byte[size];
                                Array.Copy(imageBuffer, 0, frame, 0, size);

                                ProcessFrame(frame);

                                // copy the leftover data to the start
                                Array.Copy(buff, imageEnd, buff, 0, buff.Length - imageEnd);

                                // fill the remainder of the buffer with new data and start over
                                byte[] temp = br.ReadBytes(imageEnd);

                                Array.Copy(temp, 0, buff, buff.Length - imageEnd, temp.Length);
                                break;
                            }

                            // copy all of the data to the imageBuffer
                            Array.Copy(buff, 0, imageBuffer, size, buff.Length);
                            size += buff.Length;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (Error != null)
                    _context.Post(delegate { Error(this, new ErrorEventArgs(ex.Message)); }, null);
            }
            finally
            {
                resp?.Dispose();
            }
        }

        private void ProcessFrame(byte[] frame)
        {
            CurrentFrame = frame;
            _context.Post(delegate
                {
                    // tell whoever's listening that we have a frame to draw
                    FrameReady?.Invoke(this, new FrameReadyEventArgs(CurrentFrame));
                }, null);
        }                                
    }
}
