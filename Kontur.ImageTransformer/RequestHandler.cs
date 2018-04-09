using System;
using System.Linq;
using System.Net;
using System.ComponentModel.DataAnnotations;
using System.Drawing;

namespace Kontur.ImageTransformer
{
    public class RequestHandler
    {
        [Required]
        [RegularExpression(@"rotate-cw|rotate-ccw|flip-v|flip-h", ErrorMessage = "Wrong method type")]
        public string MethodType { get; set; }

        [Required]
        [RegularExpression(@"^(-?\d+),(-?\d+),(-?\d+),(-?\d+)\z", ErrorMessage = "Wrong coordinates")]
        public string Coordinates { get; set; }

        [Required]
        [RegularExpression("POST", ErrorMessage = "Wrong HTTP method")]
        public string HttpMethod { get; set; }

        [Required]
        [Range(0, 100000, ErrorMessage = "Wrong content length")]
        public long ContentLength { get; set; }

        [Required]
        public Bitmap RequestBody { get; set; }

        [Required]
        [Range(0, 1000, ErrorMessage = "Wrong request body height")]
        public int RequestBodyHeight { get { return RequestBody.Height; } }

        [Required]
        [Range(0, 1000, ErrorMessage = "Wrong request body width")]
        public int RequestBodyWidth { get { return RequestBody.Width; } }

        public RequestHandler(HttpListenerRequest request)
        {
            var url = new Uri(request.Url.ToString());
            MethodType = url.Segments[url.Segments.Count() - 2].Remove(url.Segments[url.Segments.Count() - 2].Length - 1, 1);
            Coordinates = url.Segments.Last();
            ContentLength = request.ContentLength64;
            HttpMethod = request.HttpMethod;
            RequestBody = new Bitmap(request.InputStream);
        }
    }
}
