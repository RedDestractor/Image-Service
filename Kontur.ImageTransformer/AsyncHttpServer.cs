using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Kontur.ImageTransformer
{
    internal class AsyncHttpServer : IDisposable
    {
        private readonly HttpListener listener;
        private Thread listenerThread;
        private Thread requestQueueWorkerThread;
        private bool disposed;
        private volatile bool isRunning;
        private const int taskBound = 50;
        private const int requestBound = 50;
        private int taskCount = 0;
        private ConcurrentQueue<Tuple<HttpListenerContext, DateTime>> queueListenerContext = new ConcurrentQueue<Tuple<HttpListenerContext, DateTime>>();
        private System.Timers.Timer timer = new System.Timers.Timer();
        
        public AsyncHttpServer()
        {
            listener = new HttpListener();
        }

        public void Start(string prefix)
        {
            lock (listener)
            {
                if (!isRunning)
                {
                    listener.Prefixes.Clear();
                    listener.Prefixes.Add(prefix);
                    listener.Start();

                    listenerThread = new Thread(Listen)
                    {
                        IsBackground = true,
                        Priority = ThreadPriority.Highest
                    };
                    listenerThread.Start();

                    requestQueueWorkerThread = new Thread(RequestQueueWorker)
                    {
                        IsBackground = true,
                        Priority = ThreadPriority.Highest
                    };
                    requestQueueWorkerThread.Start();

                    RequestQueueCleanerTimer();
                }

                isRunning = true;
            }
        }

        public void Stop()
        {
            lock (listener)
            {
                if (!isRunning)
                    return;

                listener.Stop();

                listenerThread.Abort();
                requestQueueWorkerThread.Abort();
                listenerThread.Join();
                requestQueueWorkerThread.Join();

                isRunning = false;
            }
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;

            Stop();

            listener.Close();
        }

        private void Listen()
        {
            while (true)
            {
                try
                {
                    if (listener.IsListening)
                    {
                        var context = listener.GetContext();
                        if (queueListenerContext.Count() > requestBound)
                        {
                            SetListenerContextError(context, (int)HttpStatusCode.ServiceUnavailable);
                        }
                        else
                        {
                            queueListenerContext.Enqueue(Tuple.Create(context, DateTime.Now));
                        }
                    }
                    else Thread.Sleep(0);
                }
                catch (Exception)
                {
                    return;
                }
            }
        }

        private void RequestQueueWorker()
        {
            while (true)
            {
                try
                {
                    if (queueListenerContext.TryDequeue(out Tuple<HttpListenerContext, DateTime> tuple) && taskCount < taskBound)
                    {
                        Interlocked.Increment(ref taskCount);
                        Task.Run(() =>
                        {
                            HandleContextAsync(tuple.Item1);
                        }).ContinueWith(_ => Interlocked.Decrement(ref taskCount));
                    }
                }
                catch (Exception)
                {
                    return;
                }
            }
        }

        private void RequestQueueCleaner(object sender, System.Timers.ElapsedEventArgs e)
        {
            lock (queueListenerContext)
            {
                queueListenerContext = new ConcurrentQueue<Tuple<HttpListenerContext, DateTime>>
                (queueListenerContext.Where(tupleItem =>
                {
                    var currentDateTime = tupleItem.Item2;
                    if (currentDateTime.AddMilliseconds(1000) < DateTime.Now)
                    {                        
                        SetListenerContextError(tupleItem.Item1, (int)HttpStatusCode.ServiceUnavailable);
                        return false;
                    }
                    return true;
                }));
            }
        }

        void RequestQueueCleanerTimer()
        {
            timer.Interval = 1000;
            timer.Elapsed += RequestQueueCleaner;
            timer.Enabled = true;
        }

        private void HandleContextAsync(HttpListenerContext listenerContext)
        {
            var canseletionToken = new CancellationTokenSource();
            canseletionToken.CancelAfter(1000);
            try
            {
                var requestHandler = new RequestHandler(listenerContext.Request);
                var context = new ValidationContext(requestHandler);
                var results = new List<ValidationResult>();

                if (!Validator.TryValidateObject(requestHandler, context, results, true))
                {
                    SetListenerContextError(listenerContext, (int)HttpStatusCode.BadRequest);
                    return;
                }

                var rectangleRequest = GetRequestCoordinatesRectangle(requestHandler);

                if (rectangleRequest.IsEmpty)
                {
                    SetListenerContextError(listenerContext, (int)HttpStatusCode.BadRequest);
                    return;
                }

                var rectangleImage = new Rectangle(0, 0, requestHandler.RequestBody.Width, requestHandler.RequestBody.Height);
                var rectangleIntersection = Rectangle.Intersect(rectangleImage, rectangleRequest);

                if (rectangleIntersection == Rectangle.Empty)
                {
                    SetListenerContextError(listenerContext, (int)HttpStatusCode.NoContent);
                    return;
                }

                CheckRequestHandlerMethod(requestHandler);

                var IntersectionBody = requestHandler.RequestBody.Clone(rectangleIntersection, requestHandler.RequestBody.PixelFormat);
                IntersectionBody.Save(listenerContext.Response.OutputStream, ImageFormat.Png);

                SetListenerContextOK(listenerContext);
            }
            catch (OperationCanceledException)
            {
                SetListenerContextError(listenerContext, (int)HttpStatusCode.ServiceUnavailable);
                return;
            }
        }

        private void CheckRequestHandlerMethod(RequestHandler requestHandler)
        {
            switch (requestHandler.MethodType)
            {
                case "flip-h":
                    requestHandler.RequestBody.RotateFlip(RotateFlipType.RotateNoneFlipX);
                    break;
                case "flip-v":
                    requestHandler.RequestBody.RotateFlip(RotateFlipType.RotateNoneFlipY);
                    break;
                case "rotate-cw":
                    requestHandler.RequestBody.RotateFlip(RotateFlipType.Rotate90FlipNone);
                    break;
                case "rotate-ccw":
                    requestHandler.RequestBody.RotateFlip(RotateFlipType.Rotate270FlipNone);
                    break;
            }
        }

        private Rectangle GetRequestCoordinatesRectangle(RequestHandler requestHandler)
        {
            var coordinatesString = requestHandler.Coordinates.Split(',');
            var coordinatesInt = new int[4];

            for (var i = 0; i < coordinatesString.Length; i++)
            {
                if (!int.TryParse(coordinatesString[i], out coordinatesInt[i]))
                {
                    return new Rectangle();
                }
            }

            return new Rectangle(coordinatesInt[0], coordinatesInt[1], coordinatesInt[2], coordinatesInt[3]);
        }

        private void SetListenerContextOK(HttpListenerContext listenerContext)
        {
            listenerContext.Response.ContentType = "image/png";
            listenerContext.Response.StatusCode = (int)HttpStatusCode.OK;
            listenerContext.Response.OutputStream.Close();
            listenerContext.Response.Close();
        }

        private void SetListenerContextError(HttpListenerContext listenerContext, int code)
        {
            listenerContext.Response.StatusCode = code;
            listenerContext.Response.OutputStream.Close();
            listenerContext.Response.Close();
        }
    }
}